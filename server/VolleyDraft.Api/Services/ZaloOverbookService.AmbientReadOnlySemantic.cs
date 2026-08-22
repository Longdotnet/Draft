using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleAmbientReadOnlySemanticAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        if (ambientSettings.ShadowMode) return false;

        // Preserve the existing deterministic natural read-only resolver as a cheap
        // fast path. A miss here no longer disables the feature; it falls through to
        // the generic semantic planner below.
        if (ZaloAmbientReadOnlyNaturalIntentResolver.TryResolve(incoming.Content, out var fastIntent))
        {
            var fastDecision = decision with
            {
                WouldReply = true,
                Score = Math.Max(decision.Score, 100),
                Kind = ZaloAmbientParticipationKind.Fact,
                Intent = fastIntent.ToString(),
                IntentConfidence = 1,
                Signals = decision.Signals
                    .Append("readonly_deterministic_fast_path")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            var fastFact = await new ZaloAmbientFactResponder(db).TryBuildAsync(
                incoming.AccountId,
                groupId,
                incoming,
                fastDecision,
                minimumScore: 60,
                cancellationToken);
            if (fastFact is not null)
            {
                await TrySendAmbientFactAsync(
                    connectionId,
                    groupId,
                    senderId,
                    incoming,
                    fastDecision,
                    fastFact,
                    cancellationToken);
                await WriteReadOnlySemanticTraceAsync(
                    groupId,
                    senderId,
                    incoming,
                    fastIntent.ToString(),
                    1,
                    decision.Situation.RecentMessageIds,
                    fastFact.SessionId,
                    aiCalled: false,
                    "readonly_fast_path",
                    cancellationToken);
                return true;
            }
        }

        var settings = ZaloReadOnlySemanticSettings.FromConfiguration(configuration);
        if (!ZaloReadOnlySemanticGate.IsEligible(incoming, ambientSettings, settings)) return false;

        // Cost gate is generic: ordinary statements with no existing Fact/Social/Action
        // participation signal do not spend an AI call. A question with no volleyball
        // keywords is still Kind=Social via the generic question signal and reaches AI.
        if (decision.Kind == ZaloAmbientParticipationKind.None) return false;

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

        var snapshot = await new ZaloReadOnlyGroundingSnapshotBuilder(db).BuildAsync(
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
            settings.MaxContextMessages,
            cancellationToken);
        var plan = await new ZaloReadOnlySemanticPlanner(configuration, logger).PlanAsync(
            connectionId,
            groupId,
            incoming,
            context,
            snapshot,
            settings,
            cancellationToken);
        var aiCalled = plan.Reason is not "semantic_disabled" and
                       not "semantic_ai_not_configured" and
                       not "semantic_budget_exhausted";

        if (plan.Route == ZaloReadOnlySemanticRoute.MutationRequest)
        {
            await WriteReadOnlySemanticTraceAsync(
                groupId,
                senderId,
                incoming,
                plan.FactKind.ToString(),
                plan.Confidence,
                context.MessageIds,
                plan.SessionId,
                aiCalled,
                "semantic_mutation_request",
                cancellationToken,
                plan);
            // Never execute mutation from the read-only layer. Let the existing action
            // architecture inspect the untouched incoming message.
            return false;
        }

        if (plan.Route is ZaloReadOnlySemanticRoute.None or ZaloReadOnlySemanticRoute.GeneralChat)
        {
            if (aiCalled)
            {
                await WriteReadOnlySemanticTraceAsync(
                    groupId,
                    senderId,
                    incoming,
                    plan.FactKind.ToString(),
                    plan.Confidence,
                    context.MessageIds,
                    plan.SessionId,
                    true,
                    plan.Reason,
                    cancellationToken,
                    plan);
            }
            return false;
        }

        var validation = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            incoming,
            context,
            snapshot,
            settings);
        if (!validation.Accepted)
        {
            await WriteReadOnlySemanticTraceAsync(
                groupId,
                senderId,
                incoming,
                plan.FactKind.ToString(),
                plan.Confidence,
                context.MessageIds,
                plan.SessionId,
                aiCalled,
                validation.Reason,
                cancellationToken,
                plan);
            // A read-only question that failed authoritative validation must not fall
            // into Social AI, which could otherwise invent a factual answer.
            return true;
        }

        var groundedPlan = validation.Plan;
        var fact = await new ZaloReadOnlyGroundedFactResolver(db).TryBuildAsync(
            incoming.AccountId,
            connectionId,
            groupId,
            incoming,
            decision,
            groundedPlan,
            snapshot,
            cancellationToken);
        if (fact is null)
        {
            await WriteReadOnlySemanticTraceAsync(
                groupId,
                senderId,
                incoming,
                groundedPlan.FactKind.ToString(),
                groundedPlan.Confidence,
                context.MessageIds,
                groundedPlan.SessionId,
                aiCalled,
                "semantic_fact_unresolved",
                cancellationToken,
                groundedPlan);
            return true;
        }

        var semanticDecision = decision with
        {
            WouldReply = true,
            Score = Math.Max(decision.Score, (int)Math.Round(groundedPlan.Confidence * 100)),
            Kind = ZaloAmbientParticipationKind.Fact,
            Intent = fact.Intent.ToString(),
            IntentConfidence = groundedPlan.Confidence,
            Signals = decision.Signals
                .Append("grounded_readonly_semantic")
                .Append($"grounded_readonly_{groundedPlan.FactKind}")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        await TrySendAmbientFactAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            semanticDecision,
            fact,
            cancellationToken);

        await WriteReadOnlySemanticTraceAsync(
            groupId,
            senderId,
            incoming,
            groundedPlan.FactKind.ToString(),
            groundedPlan.Confidence,
            context.MessageIds,
            fact.SessionId ?? groundedPlan.SessionId,
            aiCalled,
            "semantic_readonly_accepted",
            cancellationToken,
            groundedPlan);
        return true;
    }

    private async Task WriteReadOnlySemanticTraceAsync(
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        string intent,
        double confidence,
        IReadOnlyList<string> contextMessageIds,
        string? resolvedSessionId,
        bool aiCalled,
        string reason,
        CancellationToken cancellationToken,
        ZaloReadOnlySemanticPlan? plan = null)
    {
        try
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            var details = plan is null
                ? reason
                : $"{reason}|route:{plan.Route}|subject:{plan.SubjectMemberId ?? (plan.SubjectIsCurrentSender ? "current_sender" : "-")}|ref:{plan.ReferencedMemberId ?? "-"}|offer:{plan.OpenOfferId ?? "-"}|clarify:{plan.NeedsClarification}|model_reason:{plan.Reason}";
            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    MessageId: ZaloOverbookLogic.NormalizeId(incoming.MessageId),
                    GroupId: groupId,
                    SenderZaloUserId: senderId,
                    AddressReason: "AmbientReadOnlySemantic",
                    IntentSource: aiCalled ? "GroundedReadOnlySemanticAi" : "DeterministicFastPath",
                    Intent: intent,
                    Confidence: confidence,
                    QuotedMessageId: plan?.SourceMessageId ?? quote.MessageId,
                    ContextMessageIdsJson: JsonSerializer.Serialize(contextMessageIds.Take(24)),
                    ResolvedSessionId: resolvedSessionId,
                    AiCalled: aiCalled,
                    FallbackReason: details),
                cancellationToken);
        }
        catch (Exception traceException)
        {
            logger.LogWarning(
                traceException,
                "Read-only semantic trace failed Group={GroupId} Message={MessageId} Reason={Reason}",
                groupId,
                incoming.MessageId,
                reason);
        }

        logger.LogInformation(
            "Ambient read-only semantic Group={GroupId} Message={MessageId} AiCalled={AiCalled} Intent={Intent} Confidence={Confidence} Session={SessionId} Reason={Reason}",
            groupId,
            incoming.MessageId,
            aiCalled,
            intent,
            confidence,
            resolvedSessionId,
            reason);
    }
}
