namespace VolleyDraft.Api.Services;

/// <summary>
/// Proves that a direct reply to a bot message is anchored to the same durable
/// conversation task that produced that bot reply. This is addressing/context
/// evidence only; it never grants mutation authority.
/// </summary>
internal static class ZaloQuotedTaskRecoveryPolicy
{
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
