using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationLifecycleTests
{
    private static readonly DateTimeOffset Snapshot = DateTimeOffset.Parse("2026-10-25T19:00:00+07:00");

    [Fact]
    public void Newcomer_does_not_graduate_before_two_days_even_with_strong_signals()
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(
                joinedDaysAgo: 1,
                signals: IntegratedSignals()),
            Settings());

        Assert.Equal(ZaloMemberIntegrationLifecycleStage.Newcomer, decision.Stage);
        Assert.False(decision.ShouldAnnounceGraduation);
        Assert.False(decision.StopOnboarding);
        Assert.Equal(2, decision.TargetPerDay);
    }

    [Fact]
    public void Newcomer_graduates_after_real_multi_day_human_integration()
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(
                joinedDaysAgo: 2,
                signals: IntegratedSignals()),
            Settings());

        Assert.Equal(ZaloMemberIntegrationLifecycleStage.Established, decision.Stage);
        Assert.True(decision.ShouldAnnounceGraduation);
        Assert.True(decision.StopOnboarding);
    }

    [Fact]
    public void Rejoin_graduates_with_one_real_human_interaction()
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(
                joinedDaysAgo: 0,
                isRejoin: true,
                signals: new ZaloMemberIntegrationSignals(
                    MessageCount: 1,
                    ActiveMessageDays: 1,
                    DistinctHumanInteractionCount: 1,
                    HasSpoken: true,
                    HasRepliedToMember: true)),
            Settings());

        Assert.Equal(ZaloMemberIntegrationLifecycleStage.Established, decision.Stage);
        Assert.True(decision.ShouldAnnounceGraduation);
    }

    [Fact]
    public void Rejoin_without_activity_stops_after_three_days_without_fake_graduation()
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(joinedDaysAgo: 3, isRejoin: true),
            Settings());

        Assert.Equal(ZaloMemberIntegrationLifecycleStage.PassiveMember, decision.Stage);
        Assert.True(decision.StopOnboarding);
        Assert.False(decision.ShouldAnnounceGraduation);
    }

    [Theory]
    [InlineData(0, ZaloMemberIntegrationLifecycleStage.Newcomer, 2)]
    [InlineData(2, ZaloMemberIntegrationLifecycleStage.Newcomer, 2)]
    [InlineData(3, ZaloMemberIntegrationLifecycleStage.Integrating, 1)]
    [InlineData(5, ZaloMemberIntegrationLifecycleStage.Integrating, 1)]
    [InlineData(6, ZaloMemberIntegrationLifecycleStage.PassiveIntegrating, 1)]
    [InlineData(7, ZaloMemberIntegrationLifecycleStage.PassiveIntegrating, 1)]
    [InlineData(8, ZaloMemberIntegrationLifecycleStage.PassiveMember, 0)]
    public void Newcomer_cadence_tapers_with_age(
        int days,
        ZaloMemberIntegrationLifecycleStage expectedStage,
        int expectedDailyTarget)
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(joinedDaysAgo: days),
            Settings());

        Assert.Equal(expectedStage, decision.Stage);
        Assert.Equal(expectedDailyTarget, decision.TargetPerDay);
        if (days >= 8)
            Assert.True(decision.StopOnboarding);
    }

    [Fact]
    public void Passive_integrating_spacing_is_two_days()
    {
        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(
            Context(joinedDaysAgo: 6),
            Settings());

        Assert.Equal(TimeSpan.FromDays(2), decision.MinimumMemberSpacing);
    }

    [Fact]
    public void Graduation_message_is_human_facing_and_does_not_expose_internal_score()
    {
        var suggestion = ZaloMemberIntegrationLifecyclePolicy.BuildGraduationSuggestion(
            Context(joinedDaysAgo: 2, signals: IntegratedSignals()));

        Assert.Equal("graduated", suggestion.CapabilityKey);
        Assert.Contains("ĐÃ NHẬP HỘI", suggestion.Text);
        Assert.Contains("không réo ông theo kiểu hướng dẫn tân binh nữa", suggestion.Text);
        Assert.DoesNotContain("55", suggestion.Text);
        Assert.DoesNotContain("score", suggestion.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovered_newcomer_uses_integration_anchor_without_inventing_join_time()
    {
        var recovered = Context(joinedDaysAgo: 2, signals: IntegratedSignals()) with
        {
            JoinedAt = null,
            IntegrationStartedAt = Snapshot.AddDays(-2),
            IsRecoveredNewcomer = true
        };

        var decision = ZaloMemberIntegrationLifecyclePolicy.Evaluate(recovered, Settings());
        var key = ZaloMemberIntegrationLifecyclePolicy.GraduationContentKey(recovered);

        Assert.Equal(ZaloMemberIntegrationLifecycleStage.Established, decision.Stage);
        Assert.Contains("user-1", key);
        Assert.EndsWith("20261023120000", key);
    }

    [Fact]
    public void Graduation_is_scoped_to_the_current_membership_period()
    {
        var current = Context(joinedDaysAgo: 2, signals: IntegratedSignals());
        var currentKey = ZaloMemberIntegrationLifecyclePolicy.GraduationContentKey(current);
        var currentHistory = History(current, currentKey);

        Assert.True(ZaloMemberIntegrationLifecyclePolicy.HasCurrentPeriodGraduation(current, [currentHistory]));

        var rejoin = current with
        {
            JoinedAt = Snapshot.AddHours(-2),
            IsRejoin = true
        };

        Assert.False(ZaloMemberIntegrationLifecyclePolicy.HasCurrentPeriodGraduation(rejoin, [currentHistory]));
        Assert.NotEqual(currentKey, ZaloMemberIntegrationLifecyclePolicy.GraduationContentKey(rejoin));
    }

    [Fact]
    public void Returning_member_uses_returning_badge_copy()
    {
        var suggestion = ZaloMemberIntegrationLifecyclePolicy.BuildGraduationSuggestion(
            Context(
                joinedDaysAgo: 0,
                isRejoin: true,
                signals: new ZaloMemberIntegrationSignals(
                    MessageCount: 1,
                    DistinctHumanInteractionCount: 1,
                    HasSpoken: true)));

        Assert.Contains("ĐÃ TRỞ LẠI ĐỘI HÌNH", suggestion.Text);
        Assert.Contains("bỏ chế độ welcome back/hướng dẫn", suggestion.Text);
    }

    private static ZaloMemberIntegrationSignals IntegratedSignals() => new(
        MessageCount: 3,
        ActiveMessageDays: 2,
        VoteCount: 2,
        DistinctHumanInteractionCount: 2,
        HasSpoken: true,
        HasRepliedToMember: true,
        HasBeenRepliedTo: true);

    private static ZaloMemberIntegrationContext Context(
        int joinedDaysAgo,
        bool isRejoin = false,
        ZaloMemberIntegrationSignals? signals = null) => new(
            ZaloConnectionId: "conn-1",
            AccountId: "account-1",
            GroupId: "group-1",
            ZaloUserId: "user-1",
            DisplayName: "Nguyễn Test",
            JoinedAt: Snapshot.AddDays(-joinedDaysAgo),
            IsRejoin: isRejoin,
            SnapshotAt: Snapshot,
            Signals: signals ?? new ZaloMemberIntegrationSignals(),
            Facts: new ZaloMemberIntegrationGroupFacts(
                GroupName: "CLB Tân binh-The First Spike",
                BotCreatorName: "Thanh Long",
                GroupFounderName: "Nick Tran",
                GroupPurpose: "Giao lưu bóng chuyền vui vẻ",
                CultureSummary: "Vui vẻ, hòa đồng",
                NormalCourtFeeVnd: 28000,
                MaxPlayersPerSession: 18,
                ApprovedLore: []),
            Roles: new ZaloMemberIntegrationRoleDirectory(null, []),
            PeerCandidates: [],
            LoreHints: []);

    private static ZaloProactiveMessageHistoryData History(
        ZaloMemberIntegrationContext context,
        string contentKey) => new(
            Id: "history-1",
            ConnectionId: context.ZaloConnectionId,
            GroupId: context.GroupId,
            LocalDate: "2026-10-25",
            Lane: ZaloProactiveLane.MemberIntegration,
            Kind: "member_integration_graduated",
            ContentKey: contentKey,
            SubjectUserId: context.ZaloUserId,
            SubjectName: context.DisplayName,
            MessageText: "graduated",
            SentAt: Snapshot,
            ProviderMessageId: "provider-1",
            IdempotencyKey: "idem-1");

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
