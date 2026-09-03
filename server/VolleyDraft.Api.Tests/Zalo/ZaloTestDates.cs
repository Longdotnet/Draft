namespace VolleyDraft.Api.Tests;

internal static class ZaloTestDates
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static DateTimeOffset Next(DayOfWeek dayOfWeek, int hour = 19)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
        var daysAhead = ((int)dayOfWeek - (int)now.DayOfWeek + 7) % 7;
        if (daysAhead == 0)
            daysAhead = 7;

        var localDateTime = now.Date.AddDays(daysAhead).AddHours(hour);
        return new DateTimeOffset(localDateTime, VietnamOffset);
    }
}
