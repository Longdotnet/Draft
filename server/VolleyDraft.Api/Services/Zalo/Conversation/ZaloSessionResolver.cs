using System.Globalization;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Canonical resolver for all session references used by Zalo features.
/// Exact dates dominate weekday aliases; bare weekdays resolve to the nearest still-relevant occurrence.
/// </summary>
public static class ZaloSessionResolver
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

    public static IReadOnlyList<string> SelectOperationalCandidateIds(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var resolution = Resolve(value, candidates, now);
        if (resolution.HasExplicitSelector)
            return resolution.CandidateIds;

        var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-4);
        return candidates
            .Where(candidate => candidate.StartTime is null || candidate.StartTime >= cutoff)
            .Select(candidate => candidate.Id)
            .ToList();
    }

    public static ZaloSessionResolution Resolve(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var normalized = ZaloTextNormalizer.Normalize(value);
        if (normalized.Length == 0 || candidates.Count == 0)
            return new([], "no_selector", false, false);

        var localNow = (now ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);

        var dateMatches = CalendarDateRegex.Matches(normalized);
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

        var nameMatches = candidates
            .Where(candidate =>
            {
                var name = ZaloTextNormalizer.Normalize(candidate.Name);
                return name.Length >= 3 &&
                       normalized.Contains(name, StringComparison.Ordinal) &&
                       !IsGenericWeekdayName(name);
            })
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (nameMatches.Count > 0)
            return new(nameMatches, "canonical_name", true, nameMatches.Count == 1);

        if (RelativeDateRegex.IsMatch(normalized))
        {
            var targetDate = normalized.Contains("ngay mai", StringComparison.Ordinal) ||
                             normalized.Contains("mai nay", StringComparison.Ordinal) ||
                             Regex.IsMatch(
                                 normalized,
                                 @"(?<![a-z0-9])mai(?![a-z0-9])",
                                 RegexOptions.CultureInvariant)
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

        var weekdayMatch = WeekdayRegex.Match(normalized);
        if (!weekdayMatch.Success)
            return new([], "no_selector", false, false);

        var targetDay = ParseWeekday(weekdayMatch.Groups["weekday"].Value);
        var datedMatches = candidates
            .Where(candidate => candidate.StartTime is not null &&
                                candidate.StartTime.Value.ToOffset(VietnamOffset).DayOfWeek == targetDay)
            .OrderBy(candidate => candidate.StartTime)
            .ToList();

        var undatedAliasMatches = candidates
            .Where(candidate => candidate.StartTime is null && GenericWeekdayNameMatches(candidate.Name, targetDay))
            .ToList();

        if (datedMatches.Count == 0)
        {
            var undatedIds = undatedAliasMatches
                .Select(candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new(
                undatedIds,
                undatedIds.Count > 0 ? "undated_weekday_name" : "weekday_no_match",
                true,
                undatedIds.Count == 1);
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-4);
        var upcoming = datedMatches
            .Where(candidate => candidate.StartTime >= cutoff)
            .ToList();
        var pool = upcoming.Count > 0 ? upcoming : datedMatches;

        var nearestDate = pool
            .Select(candidate => candidate.StartTime!.Value.ToOffset(VietnamOffset).Date)
            .OrderBy(date => Math.Abs((date - localNow.Date).TotalDays))
            .First();

        var nearestIds = pool
            .Where(candidate => candidate.StartTime!.Value.ToOffset(VietnamOffset).Date == nearestDate)
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new(nearestIds, "nearest_weekday", true, nearestIds.Count == 1);
    }

    public static bool LooksLikeSelector(string value)
    {
        var normalized = ZaloTextNormalizer.Normalize(value);
        return CalendarDateRegex.IsMatch(normalized) ||
               RelativeDateRegex.IsMatch(normalized) ||
               WeekdayRegex.IsMatch(normalized);
    }

    private static bool MatchesCalendarDate(Match match, DateTimeOffset localSession, int currentYear)
    {
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month))
            return false;

        if (day != localSession.Day || month != localSession.Month)
            return false;

        var yearText = match.Groups["year"].Value;
        if (yearText.Length == 0)
            return true;

        if (!int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return false;

        if (year < 100)
            year += currentYear / 100 * 100;

        return year == localSession.Year;
    }

    private static bool IsGenericWeekdayName(string name) =>
        Regex.IsMatch(
            name,
            @"^(?:t[2-7]|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|cn|chu\s+nhat)$",
            RegexOptions.CultureInvariant);

    private static bool GenericWeekdayNameMatches(string? name, DayOfWeek targetDay)
    {
        var normalized = ZaloTextNormalizer.Normalize(name);
        return IsGenericWeekdayName(normalized) && ParseWeekday(normalized) == targetDay;
    }

    private static DayOfWeek ParseWeekday(string value)
    {
        var normalized = ZaloTextNormalizer.Normalize(value);
        if (Regex.IsMatch(normalized, @"^(?:t2|thu\s+(?:2|hai))$")) return DayOfWeek.Monday;
        if (Regex.IsMatch(normalized, @"^(?:t3|thu\s+(?:3|ba))$")) return DayOfWeek.Tuesday;
        if (Regex.IsMatch(normalized, @"^(?:t4|thu\s+(?:4|tu))$")) return DayOfWeek.Wednesday;
        if (Regex.IsMatch(normalized, @"^(?:t5|thu\s+(?:5|nam))$")) return DayOfWeek.Thursday;
        if (Regex.IsMatch(normalized, @"^(?:t6|thu\s+(?:6|sau))$")) return DayOfWeek.Friday;
        if (Regex.IsMatch(normalized, @"^(?:t7|thu\s+(?:7|bay))$")) return DayOfWeek.Saturday;
        return DayOfWeek.Sunday;
    }
}
