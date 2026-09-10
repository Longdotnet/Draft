namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Keeps conversational context lifetime separate from mutation/consent authority.
/// A session-choice prompt is context only: keeping it around longer cannot execute a
/// draft, change a roster, or grant permission. Destructive confirmations continue to
/// use the short expiry supplied by their owning workflow.
/// </summary>
public static class ZaloConversationLifetimePolicy
{
    public const string DraftReadinessSessionChoiceIntent = "DraftReadinessSessionChoice";

    // A normal group conversation can easily bury an NPC prompt for an hour or more.
    // Four hours covers delayed replies without turning context into indefinite memory.
    public static readonly TimeSpan MinimumDraftSessionChoiceLifetime = TimeSpan.FromHours(4);

    public static DateTimeOffset NormalizeExpiry(
        string? intent,
        DateTimeOffset requestedExpiresAt,
        DateTimeOffset now)
    {
        if (!string.Equals(
                intent,
                DraftReadinessSessionChoiceIntent,
                StringComparison.Ordinal))
            return requestedExpiresAt;

        var durableContextExpiry = now.Add(MinimumDraftSessionChoiceLifetime);
        return requestedExpiresAt >= durableContextExpiry
            ? requestedExpiresAt
            : durableContextExpiry;
    }
}
