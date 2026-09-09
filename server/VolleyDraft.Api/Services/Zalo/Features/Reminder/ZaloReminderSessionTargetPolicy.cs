using System.Text.RegularExpressions;
using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Keeps exact calendar dates authoritative for reminder targeting while the legacy
/// reminder handler still supports its broader multi-session weekday/name grammar.
/// A concrete date must never fall through to a weekday token and schedule another
/// week's match (for example, "CN 13/9" must not also target "CN 20/9").
///
/// Reminder text often contains a trigger clock (for example, "lúc 12h"). That clock
/// is not evidence about the session's start time, so exact-date targeting resolves a
/// date-only selector and leaves same-day multi-session ambiguity for the caller to
/// clarify instead of silently choosing a match.
/// </summary>
internal static class ZaloReminderSessionTargetPolicy
{
    private static readonly Regex CalendarDateRegex = new(
        @"(?<!\d)(?<date>\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlySet<string>? ResolveExplicitCalendarDateCandidateIds(
        string selector,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var normalized = ZaloTextNormalizer.Normalize(selector);
        var dateMatch = CalendarDateRegex.Match(normalized);
        if (!dateMatch.Success)
            return null;

        // Delegate calendar interpretation, year handling and authoritative candidate
        // matching to the canonical resolver. Passing only the concrete date prevents
        // reminder trigger clocks from being mistaken for session-start clocks.
        var resolution = ZaloSessionResolver.Resolve(dateMatch.Groups["date"].Value, candidates, now);
        if (!string.Equals(resolution.Reason, "calendar_date", StringComparison.Ordinal))
            return null;

        return resolution.CandidateIds.ToHashSet(StringComparer.Ordinal);
    }
}
