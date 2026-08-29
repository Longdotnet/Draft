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
/// A Zalo admin is not automatically an Auto Session operator: non-owner admins are
/// bystanders unless they are explicitly trusted for takeover.
/// </summary>
internal static class ZaloAutoSessionOrganizerRouting
{
    private static readonly Regex ExplicitTakeover = new(
        @"(?<![a-z0-9])(?:nhan\s+(?:xu\s*ly|lam)|de\s+(?:tui|toi|minh)\s+(?:xu\s*ly|lam)|(?:tui|toi|minh)\s+(?:xu\s*ly|lam)|take\s*over|takeover)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SubstantiveAction = new(
        @"(?<![a-z0-9])(?:(?:t|thu)\s*[2-7]|cn|chu\s*nhat|\d{1,2}\s*(?:h|:)|san\b|tao\b|lam\s+di\b|chot\b|trien\b|bo\b|khoi\b|them\b|doi\b|reset\b|nhu\s+ban\s+dau)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A plain group-chat message must never execute Auto Session. However, when there is
    // exactly one active conversation, the current owner may come back long after the
    // three-minute implicit window and type an unmistakable create phrase. The service can
    // use this narrow matcher only to resurface the draft and ask for an explicit reply/@bot
    // confirmation. It is deliberately stricter than the normal addressed command parser.
    private static readonly Regex SafeUnaddressedCreateRecovery = new(
        @"^(?:tao(?: di| nha| nhe| luon| website)?|ok\s+tao(?:\s+di)?|xac\s+nhan\s+tao|chot\s+tao(?:\s+di)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ZaloAutoSessionOrganizerRoute Evaluate(
        string senderId,
        string activeOrganizerId,
        bool activeOrganizerStillAuthorized,
        bool senderTrustedForTakeover,
        bool stronglyAddressed,
        bool takeoverEscalated,
        string? content)
    {
        if (string.Equals(senderId, activeOrganizerId, StringComparison.Ordinal))
            return ZaloAutoSessionOrganizerRoute.ActiveOrganizer;

        if (!stronglyAddressed)
            return ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        // Being a current Zalo admin is necessary for authorization, but not sufficient
        // to become the Auto Session operator. Until the explicit trusted-operator UI is
        // implemented, the service supplies only the group creator as the trusted fallback.
        if (!senderTrustedForTakeover)
            return ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        var explicitTakeover = IsExplicitTakeover(content);
        var substantiveAction = LooksLikeSubstantiveAction(content);

        // If the previous owner lost creator/admin rights, a trusted fallback may take over
        // immediately, but only through a deliberate bot-addressed action.
        if (!activeOrganizerStillAuthorized)
            return explicitTakeover || substantiveAction
                ? ZaloAutoSessionOrganizerRoute.AllowTakeover
                : ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        // Before escalation, even a trusted fallback must not edit the owner's draft.
        if (!takeoverEscalated)
            return explicitTakeover
                ? ZaloAutoSessionOrganizerRoute.RejectEarlyTakeover
                : ZaloAutoSessionOrganizerRoute.IgnoreBystander;

        // After escalation, short chatter still does not claim ownership.
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

    internal static bool IsSafeUnaddressedCreateRecovery(
        string senderId,
        string activeOrganizerId,
        string? content)
    {
        if (!string.Equals(senderId, activeOrganizerId, StringComparison.Ordinal))
            return false;

        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 40)
            return false;

        return SafeUnaddressedCreateRecovery.IsMatch(normalized);
    }
}
