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
    public void Newcomer_is_due_twice_per_day_but_not_back_to_back()
    {
        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(Context(joinedDaysAgo: 0), Settings());

        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, null, lifecycle));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddMinutes(-120), lifecycle));
        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddMinutes(-181), lifecycle));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 2, Now.AddHours(-6), lifecycle));
    }

    [Fact]
    public void Integrating_member_is_limited_to_one_touch_per_day()
    {
        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(Context(joinedDaysAgo: 4), Settings());

        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, Now.AddHours(-1), lifecycle));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddHours(-12), lifecycle));
    }

    [Fact]
    public void Passive_integrating_member_requires_two_days_between_touches()
    {
        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(Context(joinedDaysAgo: 6), Settings());

        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, Now.AddHours(-47), lifecycle));
        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, Now.AddHours(-49), lifecycle));
    }

    private static ZaloMemberIntegrationContext Context(int joinedDaysAgo) => new(
        ZaloConnectionId: "conn",
        AccountId: "account",
        GroupId: "group",
        ZaloUserId: "user",
        DisplayName: "Member",
        JoinedAt: Now.AddDays(-joinedDaysAgo),
        IsRejoin: false,
        SnapshotAt: Now,
        Signals: new ZaloMemberIntegrationSignals(),
        Facts: new ZaloMemberIntegrationGroupFacts(
            "Group", "Thanh Long", "Nick Tran", "Purpose", "Culture", 28000, 18, []),
        Roles: new ZaloMemberIntegrationRoleDirectory(null, []),
        PeerCandidates: [],
        LoreHints: []);

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
