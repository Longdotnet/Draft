namespace VolleyDraft.Api.Services;

/// <summary>
/// Result of resolving a user-provided session reference against the currently known sessions.
/// Kept in the public Services namespace for compatibility while implementation lives under Zalo/Conversation.
/// </summary>
public sealed record ZaloSessionResolution(
    IReadOnlyList<string> CandidateIds,
    string Reason,
    bool HasExplicitSelector,
    bool IsExact);

public enum ZaloPendingTurnDisposition
{
    ContinuePending,
    CancelPending,
    SwitchToNewIntent,
    IgnoreCurrentTurn
}
