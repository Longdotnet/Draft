using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSocialNpcPersonalityTests
{
    [Fact]
    public void Direct_user_started_profanity_can_roast_back_at_street_level()
    {
        var profile = new ZaloSocialVibeProfile(
            ZaloTrashTalkLevel.Street,
            UsesProfanity: true,
            EmojiStyle: "ascii-laugh",
            SlangTokens: ["dm", "vl", "=))"],
            SampleCount: 12);
        var situation = new ZaloSocialSituation(
            DirectToBot: true,
            HumanTargeted: false,
            PileOnRisk: false,
            DistinctRecentSpeakers: 1,
            RecentPlayfulSignals: 2);

        var plan = ZaloTrashTalkPolicy.Decide(
            "dm bot ngu vcl",
            profile,
            situation,
            leaseTurn: false,
            maxLevel: 3,
            allowProfanity: true,
            allowHardRoast: false);

        Assert.True(plan.CanRoastBack);
        Assert.Equal(ZaloTrashTalkLevel.Street, plan.Level);
        Assert.True(plan.AllowProfanity);
        Assert.False(plan.AllowHardRoast);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Human_target_or_pile_on_never_grants_roast_back(bool humanTargeted, bool pileOn)
    {
        var profile = new ZaloSocialVibeProfile(
            ZaloTrashTalkLevel.Combat,
            true,
            "ascii-laugh",
            ["dm", "vcl"],
            20);
        var situation = new ZaloSocialSituation(
            DirectToBot: true,
            HumanTargeted: humanTargeted,
            PileOnRisk: pileOn,
            DistinctRecentSpeakers: 4,
            RecentPlayfulSignals: 5);

        var plan = ZaloTrashTalkPolicy.Decide(
            "dm bot ngu vcl",
            profile,
            situation,
            leaseTurn: false,
            maxLevel: 4,
            allowProfanity: true,
            allowHardRoast: true);

        Assert.False(plan.CanRoastBack);
    }

    [Fact]
    public void Plain_bot_reference_is_not_mistaken_for_trash_talk()
    {
        var address = new ZaloConversationalAddressDecision(
            ZaloConversationalTarget.Bot,
            ZaloConversationalSpeechAct.Unknown,
            .98,
            "explicit_bot_reference");

        Assert.False(ZaloTrashTalkPolicy.LooksLikeDirectTrashTalk("con bot haha", address, leaseTurn: false));
        Assert.True(ZaloTrashTalkPolicy.LooksLikeDirectTrashTalk("con bot ngu vl", address, leaseTurn: false));
    }

    [Fact]
    public void Vibe_profile_learns_coarse_style_from_same_member_history()
    {
        var profile = ZaloSocialVibeProfileBuilder.Build(
        [
            "dm nay danh vui =))",
            "vl qua cha noi =))",
            "bot ngu nay =))",
            "kkk nay on",
            "mai danh tiep"
        ]);

        Assert.True(profile.UsesProfanity);
        Assert.True(profile.TrashTalkComfort >= ZaloTrashTalkLevel.Friend);
        Assert.Equal("ascii-laugh", profile.EmojiStyle);
        Assert.Contains("dm", profile.SlangTokens);
    }

    [Fact]
    public void Generic_coarse_roast_is_allowed_when_direct_plan_allows_profanity()
    {
        var plan = new ZaloTrashTalkPlan(
            CanRoastBack: true,
            Level: ZaloTrashTalkLevel.Street,
            AllowProfanity: true,
            AllowHardRoast: false,
            PileOnRisk: false,
            Reason: "test");

        Assert.True(ZaloSocialSafetyPolicy.IsSafeCandidate(
            "đm hỏi ngu xong quay qua đổ tại bot hả cha nội =))",
            plan));
    }

    [Theory]
    [InlineData("tìm tới nhà xử mày giờ")]
    [InlineData("mẹ mày nói chuyện vậy hả")]
    [InlineData("mập vậy còn gáy")]
    [InlineData("đưa số điện thoại đây")]
    public void Safety_blocks_real_threat_family_appearance_and_private_data(string candidate)
    {
        var plan = new ZaloTrashTalkPlan(
            true,
            ZaloTrashTalkLevel.Combat,
            true,
            true,
            false,
            "test");

        Assert.False(ZaloSocialSafetyPolicy.IsSafeCandidate(candidate, plan));
    }

    [Fact]
    public void Inside_joke_retriever_only_returns_real_similar_history()
    {
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00+00:00");
        var history = new[]
        {
            new ZaloSocialHistoryMessage("hôm nay tao đánh nhẹ thôi nha", now.AddDays(-7)),
            new ZaloSocialHistoryMessage("mai đi ăn lẩu không", now.AddDays(-2))
        };

        var hints = ZaloInsideJokeRetriever.FindHints(
            "nay tao đánh nhẹ thôi",
            history,
            maxHints: 2);

        var hint = Assert.Single(hints);
        Assert.Equal("hôm nay tao đánh nhẹ thôi nha", hint.Text);
    }

    [Fact]
    public void Presence_is_disabled_for_send_by_default()
    {
        var settings = ZaloSocialPresenceSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.False(settings.Enabled);
        Assert.False(settings.SendEnabled);
        Assert.Equal(4, settings.MaxProactivePerDay);
        Assert.Equal(90, settings.QuietMinutes);
        Assert.Equal(60, settings.MinBotIntervalMinutes);
    }

    [Fact]
    public void Quiet_group_can_receive_an_engagement_move()
    {
        var now = DateTimeOffset.Parse("2026-08-19T13:00:00+00:00"); // 20:00 VN
        var snapshot = Snapshot(now) with
        {
            LastUserMessageAt = now.AddHours(-3),
            LastBotMessageAt = now.AddHours(-3),
            BotMessagesToday = 1,
            RecentTwoMinuteMessageCount = 0
        };

        var move = ZaloGroupEngagementDirector.Plan(snapshot, EnabledPresence());

        Assert.NotNull(move);
        Assert.Contains(move!.Kind, new[] { ZaloEngagementMoveKind.QuietWake, ZaloEngagementMoveKind.HotTake });
    }

    [Fact]
    public void Recent_activity_quota_and_busy_group_each_suppress_proactive_chat()
    {
        var now = DateTimeOffset.Parse("2026-08-19T13:00:00+00:00");
        var settings = EnabledPresence();

        Assert.Null(ZaloGroupEngagementDirector.Plan(
            Snapshot(now) with { LastUserMessageAt = now.AddMinutes(-10) },
            settings));
        Assert.Null(ZaloGroupEngagementDirector.Plan(
            Snapshot(now) with { BotMessagesToday = settings.MaxProactivePerDay },
            settings));
        Assert.Null(ZaloGroupEngagementDirector.Plan(
            Snapshot(now) with { RecentTwoMinuteMessageCount = 8 },
            settings));
    }

    [Fact]
    public void Upcoming_session_prefers_pregame_banter_after_quiet_window()
    {
        var now = DateTimeOffset.Parse("2026-08-19T13:00:00+00:00");
        var snapshot = Snapshot(now) with
        {
            LastUserMessageAt = now.AddHours(-3),
            LastBotMessageAt = now.AddHours(-3),
            UpcomingSessionName = "T5",
            UpcomingSessionAt = now.AddHours(2)
        };

        var move = ZaloGroupEngagementDirector.Plan(snapshot, EnabledPresence());

        Assert.NotNull(move);
        Assert.Equal(ZaloEngagementMoveKind.PregameBanter, move!.Kind);
    }

    [Fact]
    public void Recently_finished_session_prefers_debrief_after_quiet_window()
    {
        var now = DateTimeOffset.Parse("2026-08-19T13:00:00+00:00");
        var snapshot = Snapshot(now) with
        {
            LastUserMessageAt = now.AddHours(-3),
            LastBotMessageAt = now.AddHours(-3),
            RecentFinishedSessionName = "T4",
            RecentFinishedSessionAt = now.AddHours(-2)
        };

        var move = ZaloGroupEngagementDirector.Plan(snapshot, EnabledPresence());

        Assert.NotNull(move);
        Assert.Equal(ZaloEngagementMoveKind.PostgameDebrief, move!.Kind);
    }

    private static ZaloSocialPresenceSettings EnabledPresence() => new(
        Enabled: true,
        SendEnabled: false,
        QuietMinutes: 90,
        MinBotIntervalMinutes: 60,
        MaxProactivePerDay: 4,
        StartHour: 8,
        EndHour: 23,
        TrashTalkLevel: 3);

    private static ZaloSocialPresenceSnapshot Snapshot(DateTimeOffset now) => new(
        GroupId: "g1",
        Now: now,
        LastUserMessageAt: now.AddHours(-4),
        LastBotMessageAt: now.AddHours(-4),
        BotMessagesToday: 0,
        RecentTwoMinuteMessageCount: 0,
        UpcomingSessionName: null,
        UpcomingSessionAt: null,
        RecentFinishedSessionName: null,
        RecentFinishedSessionAt: null);
}
