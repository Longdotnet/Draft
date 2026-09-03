using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDailyGreetingRecoveryPolicyTests
{
    [Fact]
    public void Missed_morning_can_recover_before_ten_am_vietnam_time()
    {
        var now = DateTimeOffset.Parse("2026-08-19T02:30:00+00:00"); // 09:30 VN
        var plan = ZaloDailyGreetingRecoveryPolicy.Plan(
            Snapshot("morning-recovery", now),
            AlwaysGreeting(),
            60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Morning, plan!.Kind);
    }

    [Fact]
    public void Missed_night_can_recover_before_one_am_vietnam_time()
    {
        var now = DateTimeOffset.Parse("2026-08-19T17:45:00+00:00"); // 00:45 VN on 20/8
        var plan = ZaloDailyGreetingRecoveryPolicy.Plan(
            Snapshot("night-recovery", now),
            AlwaysGreeting(),
            60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Night, plan!.Kind);
        Assert.Equal(new DateOnly(2026, 8, 19), plan.ServiceDate);
    }

    [Theory]
    [InlineData("2026-08-19T03:00:00+00:00")] // 10:00 VN
    [InlineData("2026-08-19T18:00:00+00:00")] // 01:00 VN on 20/8
    public void Recovery_stops_at_the_hard_deadline(string utc)
    {
        var now = DateTimeOffset.Parse(utc);

        var plan = ZaloDailyGreetingRecoveryPolicy.Plan(
            Snapshot("deadline", now),
            AlwaysGreeting(),
            60);

        Assert.Null(plan);
    }

    [Fact]
    public void Recovery_preserves_real_bot_cooldown_age()
    {
        var now = DateTimeOffset.Parse("2026-08-19T02:30:00+00:00"); // 09:30 VN
        var recentBot = now.AddMinutes(-30);

        var blocked = ZaloDailyGreetingRecoveryPolicy.Plan(
            Snapshot("cooldown", now, recentBot),
            AlwaysGreeting(),
            60);

        Assert.Null(blocked);

        var oldBot = now.AddMinutes(-61);
        var allowed = ZaloDailyGreetingRecoveryPolicy.Plan(
            Snapshot("cooldown", now, oldBot),
            AlwaysGreeting(),
            60);

        Assert.NotNull(allowed);
    }

    private static ZaloDailyGreetingSnapshot Snapshot(
        string groupId,
        DateTimeOffset now,
        DateTimeOffset? lastBot = null) =>
        new(groupId, now, lastBot, 0, []);

    private static ZaloDailySocialSettings AlwaysGreeting() =>
        new(
            Enabled: true,
            MorningGreetingEnabled: true,
            NightGreetingEnabled: true,
            GreetingDaysPerWeek: 7,
            GreetingRepeatDays: 14,
            GreetingImagesEnabled: false)
        {
            MorningGreetingDaysPerWeek = 7,
            NightGreetingDaysPerWeek = 7,
            NightGreetingCardFirst = true
        };
}
