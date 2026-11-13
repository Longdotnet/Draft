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
    public void Newcomer_is_due_once_per_day()
    {
        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(Context(joinedDaysAgo: 0), Settings());

        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 0, null, lifecycle));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, Now.AddHours(-12), lifecycle));
    }

    [Fact]
    public void Fresh_verified_join_welcome_is_immediate()
    {
        var context = Context(joinedDaysAgo: 0) with { JoinedAt = Now.AddMinutes(-2) };
        var selection = new ZaloMemberIntegrationSelection(
            context,
            new ZaloMemberIntegrationSuggestion(
                "welcome",
                100,
                "Welcome",
                "welcome:user",
                "verified_join",
                []));

        Assert.True(ZaloMemberIntegrationDeliveryPolicy.IsImmediateWelcome(selection, Now));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsImmediateWelcome(
            selection with { Context = context with { JoinedAt = Now.AddMinutes(-16) } },
            Now));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsImmediateWelcome(
            selection with { Context = context with { IsRecoveredNewcomer = true } },
            Now));
    }

    [Fact]
    public void Same_day_match_guide_recipient_counts_as_member_integration_touch()
    {
        var marker = new ZaloProactiveMessageHistoryData(
            "history-1",
            "conn",
            "group",
            "2026-10-25",
            ZaloProactiveLane.MemberIntegrationRecipient,
            "member_integration_match_guide",
            "member-integration:match-guide:session-1",
            "user",
            "Member",
            "@Member coi sơ đồ sân nha",
            Now.AddHours(-2),
            "provider-1",
            "guide:recipient:user");
        var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(Context(joinedDaysAgo: 0), Settings());

        Assert.True(ZaloMemberMatchGuidePolicy.CountsTowardMemberDelivery(marker));
        Assert.False(ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(Now, 1, marker.SentAt, lifecycle));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public void Same_day_match_guide_targets_members_while_they_are_still_onboarding(
        int joinedDaysAgo,
        bool expected)
    {
        Assert.Equal(
            expected,
            ZaloMemberMatchGuidePolicy.IsEligible(
                Context(joinedDaysAgo),
                Settings()));
    }

    [Fact]
    public void Same_day_match_guide_excludes_current_period_graduated_member()
    {
        var context = Context(joinedDaysAgo: 4);
        var graduation = new ZaloProactiveMessageHistoryData(
            "history-graduated",
            "conn",
            "group",
            "2026-10-25",
            ZaloProactiveLane.MemberIntegration,
            "member_integration_graduated",
            ZaloMemberIntegrationLifecyclePolicy.GraduationContentKey(context),
            "user",
            "Member",
            "đã nhập hội",
            Now.AddHours(-1),
            "provider-graduated",
            "graduated:user");

        Assert.False(ZaloMemberMatchGuidePolicy.IsEligible(
            context,
            Settings(),
            [graduation]));
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
            "Group", "5 thành viên cốt cán", "Purpose", "Culture", 30000, 18, []),
        PeerCandidates: [],
        LoreHints: []);

    private static ZaloMemberIntegrationDeliverySettings Settings() => new(
        Enabled: true,
        PresenceQuietMinutes: 10,
        QuietMinutes: 20,
        PassiveQuietMinutes: 90,
        RecentActivityMinutes: 360,
        TargetPerMemberPerDay: 1,
        MinMemberIntervalMinutes: 720,
        MinGroupIntervalMinutes: 60,
        MaxPerDay: 8,
        StartHour: 9,
        EndHour: 22);
}
