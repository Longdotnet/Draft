using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleAmbientSemanticMemberAssistAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        if (ambientSettings.ShadowMode) return false;

        // Preserve read-only as a hard semantic boundary. It gets first refusal and
        // explicitly hands MutationRequest back without ever mutating state.
        if (await TryHandleAmbientReadOnlySemanticAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                decision,
                ambientSettings,
                cancellationToken))
            return true;

        var memberAssistSettings = ZaloMemberAssistSettings.FromConfiguration(configuration);
        var actionSettings = ZaloSemanticActionSettings.FromConfiguration(configuration);
        if (!memberAssistSettings.Enabled ||
            !ZaloSemanticActionGate.IsEligible(incoming, ambientSettings, actionSettings))
            return false;

        // Generic safety/idempotency guard. Importantly, there is no domain keyword
        // requirement here: regex miss must not disable semantic action planning.
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        var observed = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId && item.MessageId == messageId,
                cancellationToken);
        if (observed is null) return false;
        if (observed.BotReplySentAt is not null) return true;
        if (observed.ReplyOutcome is not null &&
            observed.ReplyOutcome is not "ambient_processing" and
            not "ambient_send_failed" and
            not "ambient_social_processing" and
            not "ambient_social_send_failed")
            return true;

        var snapshot = await new ZaloActionGroundingSnapshotBuilder(db).BuildAsync(
            connectionId,
            groupId,
            senderId,
            cancellationToken);
        var context = await ZaloReadOnlyConversationContextLoader.LoadAsync(
            db,
            connectionId,
            groupId,
            incoming,
            decision.Situation.RecentMessageIds,
            actionSettings.MaxContextMessages,
            cancellationToken);
        var plan = await new ZaloSemanticActionPlanner(configuration, logger).PlanAsync(
            connectionId,
            groupId,
            incoming,
            context,
            snapshot,
            actionSettings,
            cancellationToken);
        var aiCalled = plan.Reason is not "semantic_action_disabled" and
                       not "semantic_action_ai_not_configured" and
                       not "semantic_action_budget_exhausted";

        // Action planner is not allowed to steal questions. Read-only already had the
        // first chance; if the action planner also sees a question/general chat, leave
        // the message untouched for the remaining non-mutation ambient flow.
        if (plan.Route != ZaloSemanticActionRoute.MutationRequest)
        {
            if (aiCalled || plan.Reason.StartsWith("semantic_action_", StringComparison.Ordinal))
            {
                await WriteSemanticActionTraceAsync(
                    groupId,
                    senderId,
                    incoming,
                    context.MessageIds,
                    plan,
                    null,
                    plan.Reason,
                    aiCalled,
                    cancellationToken);
            }
            return false;
        }

        var validation = ZaloSemanticActionPlanValidator.Validate(
            plan,
            incoming,
            snapshot,
            actionSettings);
        if (!validation.Accepted)
        {
            await WriteSemanticActionTraceAsync(
                groupId,
                senderId,
                incoming,
                context.MessageIds,
                plan,
                null,
                validation.Reason,
                aiCalled,
                cancellationToken);

            // A confidently understood mutation that fails grounding/validation must
            // never fall through to another mutation guess. Give a grounded refusal so
            // the bot does not go silent while leaving the database unchanged.
            if (plan.Confidence >= actionSettings.MinimumConfidence)
            {
                await SendSemanticActionReplyAsync(
                    connectionId,
                    groupId,
                    senderId,
                    incoming,
                    decision,
                    plan.Confidence,
                    "Tui hiểu đây là yêu cầu đổi slot, nhưng dữ liệu thật chưa khớp nên tui chưa làm gì nha.",
                    null,
                    ["grounded_semantic_action_rejected", validation.Reason],
                    cancellationToken);
                return true;
            }
            return false;
        }

        var execution = await new ZaloSemanticActionExecutor(db).ExecuteAsync(
            connectionId,
            groupId,
            incoming,
            validation,
            snapshot,
            cancellationToken);
        var replyText = ZaloGroundedActionResultComposer.Compose(execution);
        var resolvedSessionId = execution.Results
            .Select(result => result.SessionId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var reason = execution.HasSuccess && execution.HasFailure
            ? "semantic_action_partial_success"
            : execution.HasSuccess
                ? "semantic_action_accepted"
                : "semantic_action_grounded_failure";

        await SendSemanticActionReplyAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            decision,
            plan.Confidence,
            replyText,
            resolvedSessionId,
            [
                "grounded_semantic_action",
                $"grounded_semantic_action_{plan.Action}",
                reason
            ],
            cancellationToken);

        await WriteSemanticActionTraceAsync(
            groupId,
            senderId,
            incoming,
            context.MessageIds,
            plan,
            execution,
            reason,
            aiCalled,
            cancellationToken);
        return true;
    }

    private async Task SendSemanticActionReplyAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        double confidence,
        string text,
        string? sessionId,
        IReadOnlyList<string> signals,
        CancellationToken cancellationToken)
    {
        var semanticScore = (int)Math.Round(confidence * 100, MidpointRounding.AwayFromZero);
        var actionDecision = decision with
        {
            WouldReply = true,
            Score = Math.Max(decision.Score, semanticScore),
            Kind = ZaloAmbientParticipationKind.Fact,
            Intent = ZaloBotIntent.SlotTransfer.ToString(),
            IntentConfidence = confidence,
            Signals = decision.Signals
                .Concat(signals)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        await TrySendAmbientFactAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            actionDecision,
            new ZaloAmbientFactReply(ZaloBotIntent.SlotTransfer, text, sessionId),
            cancellationToken);
    }

    private async Task WriteSemanticActionTraceAsync(
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<string> contextMessageIds,
        ZaloSemanticActionPlan plan,
        ZaloSemanticActionExecutionResult? execution,
        string reason,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        try
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            var traceTargets = execution is null
                ? plan.Targets.Select(target => new
                {
                    target.ReferenceText,
                    target.ResolvedDate,
                    target.SessionId,
                    target.ReferencedMemberId,
                    target.OpenOfferId,
                    Disposition = target.Disposition.ToString(),
                    target.Confidence
                })
                : execution.Results.Select(result => new
                {
                    result.Target.ReferenceText,
                    result.Target.ResolvedDate,
                    SessionId = result.SessionId ?? result.Target.SessionId,
                    result.Target.ReferencedMemberId,
                    OpenOfferId = result.OpenOfferId ?? result.Target.OpenOfferId,
                    Disposition = result.Target.Disposition.ToString(),
                    result.Target.Confidence,
                    Outcome = result.Status.ToString(),
                    result.Code
                });
            var resolvedSessionId = execution?.Results
                .Select(result => result.SessionId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                ?? plan.Targets.Select(target => target.SessionId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    MessageId: ZaloOverbookLogic.NormalizeId(incoming.MessageId),
                    GroupId: groupId,
                    SenderZaloUserId: senderId,
                    AddressReason: "AmbientSemanticAction",
                    IntentSource: aiCalled ? "GroundedSemanticActionAi" : "GroundedSemanticActionGate",
                    Intent: plan.Action.ToString(),
                    Confidence: plan.Confidence,
                    QuotedMessageId: quote.MessageId,
                    ContextMessageIdsJson: JsonSerializer.Serialize(contextMessageIds.Take(24)),
                    ResolvedSessionId: resolvedSessionId,
                    AiCalled: aiCalled,
                    FallbackReason: $"{reason}|route:{plan.Route}|actor:{plan.ActorKind}:{plan.ActorMemberId ?? "-"}|clarify:{plan.NeedsClarification}|model_reason:{plan.Reason}|targets:{JsonSerializer.Serialize(traceTargets)}"),
                cancellationToken);
        }
        catch (Exception traceException)
        {
            logger.LogWarning(
                traceException,
                "Semantic action trace failed Group={GroupId} Message={MessageId} Reason={Reason}",
                groupId,
                incoming.MessageId,
                reason);
        }

        logger.LogInformation(
            "Ambient semantic action Group={GroupId} Message={MessageId} AiCalled={AiCalled} Route={Route} Action={Action} Confidence={Confidence} Targets={TargetCount} Reason={Reason}",
            groupId,
            incoming.MessageId,
            aiCalled,
            plan.Route,
            plan.Action,
            plan.Confidence,
            plan.Targets.Count,
            reason);
    }
}
