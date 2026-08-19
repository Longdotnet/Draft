using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal enum ZaloAutoSessionOrganizerRoute
{
    ActiveOrganizer,
    AllowTakeover,
    IgnoreBystander,
    RejectEarlyTakeover
}

/// <summary>
/// Keeps one human owner on an Auto Session conversation at a time.
/// Other current Zalo admins are treated as bystanders until takeover is explicitly safe.
/// This prevents random admin chatter from stealing the draft or resetting reminders.
/// </summary>
internal static class ZaloAutoSessionOrganizerRouting
{
    private static readonly Regex ExplicitTakeover = new(
        @"(?<![a-z0-9])(?:nhan\s+(?:xu\s*ly|lam)|de\s+(?:tui|toi|minh)\s+(?:xu\s*ly|lam)|(?:tui|toi|minh)\s+(?:xu\s*ly|lam)|take\s*over|takeover)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SubstantiveAction = new(
        @"(?<![a-z0-9])(?:(?:t|thu)\s*[2-7]|cn|chu\s*nhat|\d{1,2}\s*(?:h|:)|san\b|tao\b|lam\s+di\b|chot\b|trien\b|bo\b|khoi\b|them\b|doi\b|reset\b|nhu\s+ban\s+dau)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ZaloAutoSessionOrganizerRoute Evaluate(
        string senderId,
        string activeOrganizerId,
        bool activeOrganizerStillAuthorized,
        bool stronglyAddressed,
        bool takeoverEscalated,
        string? content)
    {
        if (string.Equals(senderId, activeOrganizerId, StringComparison.Ordinal))
            return ZaloAutoSessionOrganizerRoute.ActiveOrganizer;

        if (!stronglyAddressed)
            return ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        var explicitTakeover = IsExplicitTakeover(content);
        var substantiveAction = LooksLikeSubstantiveAction(content);

        // If the previous owner is no longer a current creator/admin, another current
        // organizer may take over immediately, but only through a deliberate bot-addressed action.
        if (!activeOrganizerStillAuthorized)
            return explicitTakeover || substantiveAction
                ? ZaloAutoSessionOrganizerRoute.AllowTakeover
                : ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        // Before escalation, another admin must not mutate the draft just because they
        // happen to reply to the bot. An explicit takeover request gets one deterministic
        // explanation; ordinary chatter/action-like comments are silently ignored.
        if (!takeoverEscalated)
            return explicitTakeover
                ? ZaloAutoSessionOrganizerRoute.RejectEarlyTakeover
                : ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        // After escalation, "ok", "ừ", jokes, and other short chatter still do not claim
        // ownership. A takeover needs either explicit ownership language or a concrete
        // schedule/location/create/cancel action addressed to the bot.
        return explicitTakeover || substantiveAction
            ? ZaloAutoSessionOrganizerRoute.AllowTakeover
            : ZaloAutoSessionOrganizerRoute.IgnoreBystander;
    }

    internal static bool IsExplicitTakeover(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        return normalized.Length > 0 && ExplicitTakeover.IsMatch(normalized);
    }

    internal static bool LooksLikeSubstantiveAction(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 160) return false;
        return SubstantiveAction.IsMatch(normalized);
    }
}
