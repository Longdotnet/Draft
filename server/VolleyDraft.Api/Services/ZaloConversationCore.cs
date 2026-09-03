using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Backward-compatible facade for callers that have not yet moved to feature modules.
/// New code should depend on the focused services under Services/Zalo/Conversation directly.
/// </summary>
public static class ZaloConversationCore
{
    public static IReadOnlyList<string> SelectOperationalSessionCandidateIds(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null) =>
        ZaloSessionResolver.SelectOperationalCandidateIds(value, candidates, now);

    public static IReadOnlyList<string> ResolveSessionReference(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null) =>
        ZaloSessionResolver.Resolve(value, candidates, now).CandidateIds;

    public static ZaloSessionResolution ResolveSession(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null) =>
        ZaloSessionResolver.Resolve(value, candidates, now);

    public static bool LooksLikeSessionSelector(string value) =>
        ZaloSessionResolver.LooksLikeSelector(value);

    public static bool TryGetMenuCommand(
        string value,
        out int command,
        out string? sessionReference) =>
        ZaloMenuCommandParser.TryParse(value, out command, out sessionReference);

    public static bool IsNaturalCancel(string value) =>
        ZaloPendingTurnPolicy.IsNaturalCancel(value);

    public static ZaloPendingTurnDisposition ClassifyPendingSessionTurn(
        string pendingIntent,
        string currentQuestion,
        bool mentionedBot,
        string? freshIntent = null,
        double freshConfidence = 0) =>
        ZaloPendingTurnPolicy.ClassifySessionTurn(
            pendingIntent,
            currentQuestion,
            mentionedBot,
            freshIntent,
            freshConfidence);
}
