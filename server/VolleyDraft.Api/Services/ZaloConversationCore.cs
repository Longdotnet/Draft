using System.Globalization;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public enum ZaloPendingTurnDisposition
{
    ContinuePending,
    CancelPending,
    SwitchToNewIntent,
    IgnoreCurrentTurn
}

public sealed record ZaloSessionResolution(
    IReadOnlyList<string> CandidateIds,
    string Reason,
    bool HasExplicitSelector,
    bool IsExact);

/// <summary>
/// Canonical deterministic semantics shared by all Zalo conversation lanes.
/// This class deliberately owns session/date resolution and pending-turn relevance
/// so operational workflows do not depend on an LLM or on route-specific regexes.
/// </summary>
public static class ZaloConversationCore
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly Regex CalendarDateRegex = new(
        @"(?<!\d)(?<day>\d{1,2})[/-](?<month>\d{1,2})(?:[/-](?<year>\d{2,4}))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeDateRegex = new(
        @"(?<![a-z0-9])(?:hom\s+nay|bua\s+nay|ngay\s+mai|mai\s+nay|mai)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WeekdayRegex = new(
        @"(?<![a-z0-9])(?<weekday>t[2-7]|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|cn|chu\s+nhat)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MenuCommandRegex = new(
        @"^(?:@?(?:[a-z0-9._-]*bot|npc|volley\s*bot)\s+)?(?<command>10|12|[1-9])(?:\s+(?<reference>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> SelectOperationalSessionCandidateIds(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var resolution = ResolveSession(value, candidates, now);
        if (resolution.HasExplicitSelector)
            return resolution.CandidateIds;

        var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-4);
        return candidates
            .Where(candidate => candidate.StartTime is null || candidate.StartTime >= cutoff)
            .Select(candidate => candidate.Id)
            .ToList();
    }

    public static IReadOnlyList<string> ResolveSessionReference(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null) =>
        ResolveSession(value, candidates, now).CandidateIds;

    public static ZaloSessionResolution ResolveSession(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var q = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        if (q.Length == 0 || candidates.Count == 0)
            return new([], "no_selector", false, false);

        var localNow = (now ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);

        // Exact calendar dates always dominate weekday aliases. This prevents
        // "T4 02/09" from matching every historical Wednesday.
        var dateMatches = CalendarDateRegex.Matches(q);
        if (dateMatches.Count > 0)
        {
            var ids = candidates
                .Where(candidate => candidate.StartTime is not null && dateMatches.Any(match =>
                    MatchesCalendarDate(match, candidate.StartTime!.Value.ToOffset(VietnamOffset), localNow.Year)))
                .Select(candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new(ids, "calendar_date", true, ids.Count == 1);
        }

        // A full configured session name is stronger than a bare weekday.
        var nameMatches = candidates
            .Where(candidate =>
            {
                var name = ZaloBotIntelligence.Normalize(candidate.Name);
                return name.Length >= 3 && q.Contains(name, StringComparison.Ordinal) && !IsGenericWeekdayName(name);
            })
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (nameMatches.Count > 0)
            return new(nameMatches, "canonical_name", true, nameMatches.Count == 1);

        if (RelativeDateRegex.IsMatch(q))
        {
            var targetDate = q.Contains("ngay mai", StringComparison.Ordinal) ||
                             q.Contains("mai nay", StringComparison.Ordinal) ||
                             Regex.IsMatch(q, @"(?<![a-z0-9])mai(?![a-z0-9])", RegexOptions.CultureInvariant)
                ? localNow.Date.AddDays(1)
                : localNow.Date;
            var ids = candidates
                .Where(candidate => candidate.StartTime is not null &&
                                    candidate.StartTime.Value.ToOffset(VietnamOffset).Date == targetDate)
                .Select(candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new(ids, "relative_date", true, ids.Count == 1);
        }

        var weekdayMatch = WeekdayRegex.Match(q);
        if (weekdayMatch.Success)
        {
            var targetDay = ParseWeekday(weekdayMatch.Groups["weekday"].Value);
            var sameDayCandidates = candidates
                .Where(candidate => candidate.StartTime is not null &&
                                    candidate.StartTime.Value.ToOffset(VietnamOffset).DayOfWeek == targetDay)
                .OrderBy(candidate => candidate.StartTime)
                .ToList();
            if (sameDayCandidates.Count == 0)
                return new([], "weekday_no_match", true, false);

            // Operational weekday references mean the nearest still-relevant occurrence.
            // Keep sessions on the nearest local date together in case the group has two
            // courts/times on that date, but never drag previous weeks into the choice.
            var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-4);
            var upcoming = sameDayCandidates
                .Where(candidate => candidate.StartTime >= cutoff)
                .ToList();
            var pool = upcoming.Count > 0 ? upcoming : sameDayCandidates;
            var nearestDate = pool
                .Select(candidate => candidate.StartTime!.Value.ToOffset(VietnamOffset).Date)
                .OrderBy(date => Math.Abs((date - localNow.Date).TotalDays))
                .First();
            var ids = pool
                .Where(candidate => candidate.StartTime!.Value.ToOffset(VietnamOffset).Date == nearestDate)
                .Select(candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new(ids, "nearest_weekday", true, ids.Count == 1);
        }

        return new([], "no_selector", false, false);
    }

    public static bool LooksLikeSessionSelector(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return CalendarDateRegex.IsMatch(q) || RelativeDateRegex.IsMatch(q) || WeekdayRegex.IsMatch(q);
    }

    public static bool TryGetMenuCommand(string value, out int command, out string? sessionReference)
    {
        command = 0;
        sessionReference = null;
        var q = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        var match = MenuCommandRegex.Match(q);
        if (!match.Success || !int.TryParse(match.Groups["command"].Value, out command))
        {
            command = 0;
            return false;
        }

        var reference = match.Groups["reference"].Value.Trim();
        if (reference.Length == 0) return true;
        if (!LooksLikeSessionSelector(reference))
        {
            command = 0;
            return false;
        }

        sessionReference = reference;
        return true;
    }

    public static bool IsNaturalCancel(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value ?? string.Empty).Trim(' ', '.', '!', '?', ',', ';', ':');
        if (q.Length == 0) return false;
        return q is "huy" or "cancel" or "thoi" or "bo qua" or "khong can nua" or
               "thoi khoi" or "thoi khoi di" or "hoi khoi di" or "khoi" or "khoi di" or "bo di" or "khong lam nua" ||
               q.StartsWith("huy ", StringComparison.Ordinal) ||
               q.StartsWith("cancel ", StringComparison.Ordinal) ||
               q.StartsWith("thoi khoi", StringComparison.Ordinal) ||
               q.StartsWith("hoi khoi", StringComparison.Ordinal) ||
               q.StartsWith("khoi di", StringComparison.Ordinal) ||
               q.StartsWith("bo qua ", StringComparison.Ordinal) ||
               q.Contains("khong can nua", StringComparison.Ordinal);
    }

    public static ZaloPendingTurnDisposition ClassifyPendingSessionTurn(
        string pendingIntent,
        string currentQuestion,
        bool mentionedBot,
        string? freshIntent = null,
        double freshConfidence = 0)
    {
        if (IsNaturalCancel(currentQuestion)) return ZaloPendingTurnDisposition.CancelPending;
        if (LooksLikeSessionSelector(currentQuestion)) return ZaloPendingTurnDisposition.ContinuePending;
        if (IsStrongConfirmation(currentQuestion)) return ZaloPendingTurnDisposition.ContinuePending;

        if (TryGetMenuCommand(currentQuestion, out _, out _))
            return ZaloPendingTurnDisposition.SwitchToNewIntent;

        if (!string.IsNullOrWhiteSpace(freshIntent) &&
            freshConfidence >= .85 &&
            !string.Equals(pendingIntent, freshIntent, StringComparison.OrdinalIgnoreCase))
            return ZaloPendingTurnDisposition.SwitchToNewIntent;

        // An explicit @Npc turn is a new user-directed turn unless it actually looks
        // like a valid answer to the pending selector above. Ambient chatter must not
        // be consumed by somebody's old pending state.
        return mentionedBot
            ? ZaloPendingTurnDisposition.SwitchToNewIntent
            : ZaloPendingTurnDisposition.IgnoreCurrentTurn;
    }

    private static bool IsStrongConfirmation(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return q is "xac nhan" or "xac nhan draft" or "dong y" or "ok" or "ok chay" or
               "chay di" or "draft di" or "chot" or "lam di" or "thuc hien di" ||
               q.StartsWith("xac nhan ", StringComparison.Ordinal) ||
               q.StartsWith("dong y ", StringComparison.Ordinal) ||
               q.StartsWith("chot ", StringComparison.Ordinal);
    }

    private static bool MatchesCalendarDate(Match match, DateTimeOffset localSession, int currentYear)
    {
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month))
            return false;
        if (day != localSession.Day || month != localSession.Month) return false;

        var yearText = match.Groups["year"].Value;
        if (yearText.Length == 0) return true;
        if (!int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return false;
        if (year < 100) year += currentYear / 100 * 100;
        return year == localSession.Year;
    }

    private static bool IsGenericWeekdayName(string name) =>
        Regex.IsMatch(
            name,
            @"^(?:t[2-7]|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|cn|chu\s+nhat)$",
            RegexOptions.CultureInvariant);

    private static DayOfWeek ParseWeekday(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value);
        if (Regex.IsMatch(q, @"^(?:t2|thu\s+(?:2|hai))$")) return DayOfWeek.Monday;
        if (Regex.IsMatch(q, @"^(?:t3|thu\s+(?:3|ba))$")) return DayOfWeek.Tuesday;
        if (Regex.IsMatch(q, @"^(?:t4|thu\s+(?:4|tu))$")) return DayOfWeek.Wednesday;
        if (Regex.IsMatch(q, @"^(?:t5|thu\s+(?:5|nam))$")) return DayOfWeek.Thursday;
        if (Regex.IsMatch(q, @"^(?:t6|thu\s+(?:6|sau))$")) return DayOfWeek.Friday;
        if (Regex.IsMatch(q, @"^(?:t7|thu\s+(?:7|bay))$")) return DayOfWeek.Saturday;
        return DayOfWeek.Sunday;
    }
}