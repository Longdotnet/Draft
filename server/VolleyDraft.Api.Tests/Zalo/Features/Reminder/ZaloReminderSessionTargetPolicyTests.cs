using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Reminder;

public sealed class ZaloReminderSessionTargetPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(7));

    private static readonly ZaloSessionReference[] SundaySessions =
    [
        new("sun-13", "CN 13/9", At(13, 17, 30)),
        new("sun-20", "CN 20/9", At(20, 17, 30))
    ];

    [Theory]
    [InlineData("nhắc CN 13/9 lúc 12h")]
    [InlineData("nhắc Chủ nhật 13/09 lúc 12h")]
    [InlineData("CN 13-9 nhắc nếu còn thiếu slot")]
    public void Exact_date_wins_over_weekday_alias_for_reminder_targeting(string selector)
    {
        var result = ZaloReminderSessionTargetPolicy.ResolveExplicitCalendarDateCandidateIds(
            selector,
            SundaySessions,
            Now);

        Assert.NotNull(result);
        Assert.Equal(["sun-13"], result);
    }

    [Fact]
    public void Exact_date_and_time_can_choose_one_of_multiple_same_day_sessions()
    {
        ZaloSessionReference[] candidates =
        [
            new("sun-early", "CN chiều", At(13, 15, 0)),
            new("sun-late", "CN tối", At(13, 19, 0))
        ];

        var result = ZaloReminderSessionTargetPolicy.ResolveExplicitCalendarDateCandidateIds(
            "nhắc CN 13/9 lúc 19h",
            candidates,
            Now);

        Assert.NotNull(result);
        Assert.Equal(["sun-late"], result);
    }

    [Theory]
    [InlineData("nhắc CN nếu còn thiếu slot")]
    [InlineData("nhắc chủ nhật")]
    [InlineData("nhắc T6")]
    public void Non_calendar_selectors_stay_on_existing_reminder_targeting_path(string selector)
    {
        var result = ZaloReminderSessionTargetPolicy.ResolveExplicitCalendarDateCandidateIds(
            selector,
            SundaySessions,
            Now);

        Assert.Null(result);
    }

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(7));
}
