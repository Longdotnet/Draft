using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Proves that a direct reply to a bot message is anchored to the same durable
/// conversation task that produced that bot reply. This is addressing/context
/// evidence only; it never grants mutation authority.
/// </summary>
internal static class ZaloQuotedTaskRecoveryPolicy
{
    internal static async Task<bool> IsExactDraftSessionChoiceAnchorAsync(
        VolleyDraftDbContext db,
        string connectionId,
        string groupId,
        string senderId,
        ZaloQuotedSemanticContext quote,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId) ||
            string.IsNullOrWhiteSpace(groupId) ||
            string.IsNullOrWhiteSpace(senderId) ||
            !quote.RepliesToBot ||
            string.IsNullOrWhiteSpace(quote.MessageId))
        {
            return false;
        }

        // ConversationState V2 is physically connection-scoped. Recovery happens before
        // the normal bot lane establishes its own scope, so delayed quoted replies must
        // explicitly enter the resolved provider connection here. Otherwise a valid
        // state saved by DraftAutopilot cannot be found after the ambient lease expires,
        // and a legacy unscoped row could be consulted instead.
        using var conversationStateScope = ZaloConversationStateScope.Push(connectionId);
        var state = await new ZaloConversationStateV2Store(db)
            .LoadActiveAsync(groupId, senderId, cancellationToken);
        if (state is null)
            return false;

        var quotedBotRelation = await new ZaloMessageGraphStore(db)
            .LoadRelationAsync(connectionId, groupId, quote.MessageId, cancellationToken);
        return IsExactDraftSessionChoiceAnchor(state, quote, quotedBotRelation);
    }

    internal static bool IsExactDraftSessionChoiceAnchor(
        ZaloConversationStateV2Snapshot? state,
        ZaloQuotedSemanticContext quote,
        ZaloMessageGraphRelation? quotedBotRelation)
    {
        if (state is null || !quote.RepliesToBot || string.IsNullOrWhiteSpace(quote.MessageId))
            return false;

        if (!string.Equals(state.Intent, "DraftReadinessSessionChoice", StringComparison.Ordinal))
            return false;

        // Future-compatible fast path if callers persist the outbound provider ID
        // directly as the state's latest prompt/message anchor.
        if (string.Equals(state.LastMessageId, quote.MessageId, StringComparison.Ordinal))
            return true;

        if (quotedBotRelation is null ||
            !string.Equals(quotedBotRelation.RelationType, "BotReply", StringComparison.Ordinal) ||
            !string.Equals(quotedBotRelation.FromMessageId, quote.MessageId, StringComparison.Ordinal))
            return false;

        // Draft replies are already recorded as provider bot message -> inbound
        // message edges. Match that parent against the durable state's provenance.
        var parentMessageId = quotedBotRelation.ToMessageId;
        if (string.IsNullOrWhiteSpace(parentMessageId))
            return false;

        return string.Equals(state.SourceMessageId, parentMessageId, StringComparison.Ordinal) ||
               string.Equals(state.LastMessageId, parentMessageId, StringComparison.Ordinal);
    }
}
