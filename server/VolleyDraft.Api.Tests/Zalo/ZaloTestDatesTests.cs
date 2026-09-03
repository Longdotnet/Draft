using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTestDatesTests
{
    [Fact]
    public void Next_uses_Vietnam_weekday_when_utc_is_still_previous_day()
    {
        var referenceUtc = new DateTimeOffset(2026, 9, 3, 23, 30, 0, TimeSpan.Zero);

        var nextFriday = ZaloTestDates.Next(DayOfWeek.Friday, referenceUtc);

        Assert.Equal(DayOfWeek.Friday, nextFriday.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(7), nextFriday.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 19, 0, 0, TimeSpan.FromHours(7)), nextFriday);
    }

    [Fact]
    public void Next_returns_following_week_when_reference_is_already_requested_weekday()
    {
        var referenceUtc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var nextFriday = ZaloTestDates.Next(DayOfWeek.Friday, referenceUtc, 20);

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.FromHours(7)), nextFriday);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void Next_rejects_invalid_fixture_hour(int hour)
    {
        var referenceUtc = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZaloTestDates.Next(DayOfWeek.Friday, referenceUtc, hour));
    }
}
