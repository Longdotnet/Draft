namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Shared precedence contract for pending conversational ownership.
///
/// This deliberately owns only the cross-domain decision that had drifted between
/// legacy/session pending handlers and ConversationState V2: exact bare controls vs
/// a high-confidence fresh deterministic intent. Domain-specific continuation grammar
/// (session selectors, profile answers, reminder semantics, etc.) remains with callers.
/// </summary>
public static class ZaloPendingOwnershipPolicy
{
    public enum SharedDisposition
    {
        None,
        CancelPending,
        ConfirmPending,
        SwitchToFreshIntent
    }

    public static SharedDisposition ClassifySharedControl(
        string pendingIntent,
        string currentQuestion,
        string? freshIntent,
        double freshConfidence = 0)
    {
        var normalized = ZaloTextNormalizer.Normalize(currentQuestion ?? string.Empty)
            .Trim(' ', '.', '!', '?', ',', ';', ':');

        // Exact bare controls belong to the pending workflow even if a classifier
        // produces an unrelated guess. They carry no domain qualifier of their own.
        if (normalized is "huy" or "cancel" or "thoi khoi")
            return SharedDisposition.CancelPending;
        if (normalized == "xac nhan")
            return SharedDisposition.ConfirmPending;

        // A domain-qualified deterministic intent owns the current turn before broad
        // cancel/confirm helpers run. This is the invariant behind #171/#172: e.g.
        // `hủy reminder` must not cancel an unrelated draft/session pending workflow.
        if (!string.IsNullOrWhiteSpace(freshIntent) &&
            freshConfidence >= .85 &&
            !string.Equals(pendingIntent, freshIntent, StringComparison.OrdinalIgnoreCase))
            return SharedDisposition.SwitchToFreshIntent;

        return SharedDisposition.None;
    }
}
