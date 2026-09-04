namespace VolleyDraft.Api.Tests;

internal static class ZaloTestDates
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static DateTimeOffset Next(DayOfWeek dayOfWeek, int hour = 19) =>
        Next(dayOfWeek, DateTimeOffset.UtcNow, hour);

    internal static DateTimeOffset Next(
        DayOfWeek dayOfWeek,
        DateTimeOffset referenceTime,
        int hour = 19)
    {
        if (hour is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(hour));

        var now = referenceTime.ToOffset(VietnamOffset);
        var daysAhead = ((int)dayOfWeek - (int)now.DayOfWeek + 7) % 7;
        if (daysAhead == 0)
            daysAhead = 7;

        var localDateTime = now.Date.AddDays(daysAhead).AddHours(hour);
        return new DateTimeOffset(localDateTime, VietnamOffset);
    }
}
