using System.Text.Json;
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

        var memberAssistSettings = ZaloMemberAssistSettings.FromConfiguration(configuration);
        var semanticSettings = ZaloAmbientDomainIntentSettings.FromConfiguration(configuration);
        if (!memberAssistSettings.Enabled ||
            !semanticSettings.Enabled ||
            !ZaloAmbientDomainIntentResolver.LooksLikeCandidate(incoming))
            return false;

        var semantic = await new ZaloAmbientDomainIntentResolver(db, configuration, logger)
            .ResolveAsync(
                connectionId,
                groupId,
                incoming,
                decision.Situation.RecentMessageIds,
                semanticSettings,
                cancellationToken);
        if (semantic.Kind == ZaloAmbientDomainIntentKind.None ||
            semantic.Confidence < semanticSettings.MinimumConfidence)
        {
            logger.LogDebug(
                "Ambient semantic member-assist skipped Group={GroupId} Message={MessageId} Kind={Kind} Confidence={Confidence} Reason={Reason}",
                groupId,
                incoming.MessageId,
                semantic.Kind,
                semantic.Confidence,
                semantic.Reason);
            return false;
        }

        var promoted = ZaloAmbientDomainIntentPromotion.Promote(incoming, semantic);
        if (promoted is null) return false;

        // AI supplies meaning only. Re-enter the existing deterministic service so
        // sender ownership, session state, open-offer state and confirmation rules
        // remain authoritative and fail closed when the model guessed wrong.
        var assist = await new ZaloMemberAssistService(db).TryBuildAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        if (assist is null)
        {
            logger.LogInformation(
                "Ambient semantic member-assist rejected by deterministic validator Group={GroupId} Message={MessageId} Kind={Kind} Confidence={Confidence}",
                groupId,
                incoming.MessageId,
                semantic.Kind,
                semantic.Confidence);
            return false;
        }

        var semanticScore = (int)Math.Round(semantic.Confidence * 100, MidpointRounding.AwayFromZero);
        var assistDecision = decision with
        {
            WouldReply = true,
            Score = Math.Max(decision.Score, semanticScore),
            Kind = ZaloAmbientParticipationKind.Fact,
            Intent = ZaloBotIntent.SlotTransfer.ToString(),
            IntentConfidence = semantic.Confidence,
            Signals = decision.Signals
                .Append("member_assist_semantic_ai")
                .Append($"member_assist_semantic_{semantic.Kind}")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        await TrySendAmbientFactAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            assistDecision,
            new ZaloAmbientFactReply(
                ZaloBotIntent.SlotTransfer,
                assist.Text,
                assist.SessionId),
            cancellationToken);

        try
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    MessageId: ZaloOverbookLogic.NormalizeId(incoming.MessageId),
                    GroupId: groupId,
                    SenderZaloUserId: senderId,
                    AddressReason: "AmbientSemanticMemberAssist",
                    IntentSource: "StructuredAiMeaning",
                    Intent: semantic.Kind.ToString(),
                    Confidence: semantic.Confidence,
                    QuotedMessageId: quote.MessageId,
                    ContextMessageIdsJson: JsonSerializer.Serialize(decision.Situation.RecentMessageIds.Take(12)),
                    ResolvedSessionId: assist.SessionId,
                    AiCalled: true,
                    FallbackReason: semantic.Reason),
                cancellationToken);
        }
        catch (Exception traceException)
        {
            logger.LogWarning(
                traceException,
                "Ambient semantic member-assist trace failed Group={GroupId} Message={MessageId}",
                groupId,
                incoming.MessageId);
        }

        logger.LogInformation(
            "Ambient semantic member-assist accepted Group={GroupId} Message={MessageId} Kind={Kind} Confidence={Confidence} Session={SessionId}",
            groupId,
            incoming.MessageId,
            semantic.Kind,
            semantic.Confidence,
            assist.SessionId);
        return true;
    }
}
