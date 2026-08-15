using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Ambient-only fallback for natural status questions that are clearly read-only but
/// do not match the legacy deterministic command vocabulary. It deliberately never
/// emits an action intent and therefore cannot authorize a mutation.
/// </summary>
public static class ZaloAmbientReadOnlyNaturalIntentResolver
{
    private static readonly Regex ReadOnlyStatusEnding = new(
        @"(?:\?|hien\s+sao|ra\s+sao(?:\s+roi)?|sao\s+roi|the\s+nao|xong\s+chua|co\s+chua|con\s+ai|dang\s+sao|dang\s+the\s+nao)[.!?]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TeamLanguage = new(
        @"(?<![a-z0-9])(?:team|doi|doi\s+hinh|chia\s+doi|chia\s+team)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WaitlistLanguage = new(
        @"(?<![a-z0-9])(?:waitlist|danh\s+sach\s+cho|nguoi\s+cho|ai\s+(?:dang\s+)?cho|con\s+ai\s+cho)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReminderLanguage = new(
        @"(?<![a-z0-9])(?:lich\s+nhac|reminder|hen\s+nhac)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryResolve(string? content, out ZaloBotIntent intent)
    {
        intent = ZaloBotIntent.Unknown;
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length == 0 || !ReadOnlyStatusEnding.IsMatch(normalized))
            return false;

        if (ReminderLanguage.IsMatch(normalized))
        {
            intent = ZaloBotIntent.ReminderStatus;
            return true;
        }

        if (WaitlistLanguage.IsMatch(normalized))
        {
            intent = ZaloBotIntent.WaitlistStatus;
            return true;
        }

        if (TeamLanguage.IsMatch(normalized))
        {
            intent = ZaloBotIntent.TeamLineup;
            return true;
        }

        return false;
    }
}
