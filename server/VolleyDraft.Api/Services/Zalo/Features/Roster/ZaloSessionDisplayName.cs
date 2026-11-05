using System.Globalization;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal static class ZaloSessionDisplayName
{
    private static readonly Regex EmbeddedClock = new(
        @"(?<!\d)(?:[01]?\d|2[0-3]):[0-5]\d(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string WithCanonicalStartTime(string? sessionName, DateTimeOffset? startTime)
    {
        var cleanName = (sessionName ?? string.Empty).Trim();
        if (cleanName.Length == 0 || startTime is null) return cleanName;

        var localStart = startTime.Value.ToOffset(TimeSpan.FromHours(7));
        var canonicalClock = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        return EmbeddedClock.Replace(cleanName, canonicalClock, 1);
    }
}
