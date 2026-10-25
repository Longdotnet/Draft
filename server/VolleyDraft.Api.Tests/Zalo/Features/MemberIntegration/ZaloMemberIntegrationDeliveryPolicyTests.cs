using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationDeliveryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T19:00:00+07:00");

    [Fact]
    public void Recent_newcomer_activity_uses_short_quiet_window()
    {
        var settings = Settings();
        var activity = new ZaloMemberIntegrationActivityHint(
            "user-1",
            Now.AddMinutes(-30),
            "message");

        Assert.Equal(20, ZaloMemberIntegrationDeliveryPolicy.RequiredQuietMinutes(Now, activity, settings));
    }

    [Fact]
    public void Seen_receipt_uses_presence_quiet_window()
    {
        var settings = Settings();
        var activity = new ZaloMemberIntegrationActivityHint(
            "user-1",
            Now.AddMinutes(-5),
            "seen");

        Assert.Equal(10, ZaloMemberIntegrationDeliveryPolicy.RequiredQuietMinutes(Now, activity, settings));
    }

    [Fact]
    public void Stale_or_missing_activity_requires_longer_passive_quiet_window()
    {
        var settings = Settings();

        Assert.Equal(
            90,
            ZaloMemberIntegrationDeliveryPolicy.RequiredQuietMinutes(
                Now,
                new ZaloMemberIntegrationActivityHint("user-1", Now.AddHours(-7), "message"),
                settings));
        Assert.Equal(
            90,
            ZaloMemberIntegrationDeliveryPolicy.RequiredQuietMinutes(
                Now,
                new ZaloMemberIntegrationActivityHint("user-2", null, null),
                settings));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(21, true)]
    [InlineData(22, false)]
    public void Delivery_window_is_independent_from_community_schedule(int hour, bool expected)
    {
        Assert.Equal(expected, ZaloMemberIntegrationDeliveryPolicy.IsInsideWindow(hour, Settings()));
    }

    [Fact]
    public void Member_is_due_twice_per_day_but_not_back_to_back()
    {
        var settings = Settings();

        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, null, settings));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddMinutes(-120), settings));
        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddMinutes(-181), settings));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 2, Now.AddHours(-6), settings));
    }

    private static ZaloMemberIntegrationDeliverySettings Settings() => new(
        Enabled: true,
        PresenceQuietMinutes: 10,
        QuietMinutes: 20,
        PassiveQuietMinutes: 90,
        RecentActivityMinutes: 360,
        TargetPerMemberPerDay: 2,
        MinMemberIntervalMinutes: 180,
        MinGroupIntervalMinutes: 20,
        MaxPerDay: 40,
        StartHour: 9,
        EndHour: 22);
}
