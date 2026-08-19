using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDailySocialRhythmTests
{
    [Fact]
    public void Presence_defaults_to_six_am_through_one_am_vietnam_time()
    {
        var settings = ZaloSocialPresenceSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(6, settings.StartHour);
        Assert.Equal(1, settings.EndHour);
        Assert.True(settings.Enabled);
        Assert.True(settings.SendEnabled);
    }

    [Fact]
    public void Daily_rhythm_is_enabled_with_warm_greetings_and_images_by_default()
    {
        var settings = ZaloDailySocialSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(settings.Enabled);
        Assert.True(settings.MorningGreetingEnabled);
        Assert.True(settings.NightGreetingEnabled);
        Assert.Equal(5, settings.GreetingDaysPerWeek);
        Assert.Equal(14, settings.GreetingRepeatDays);
        Assert.True(settings.GreetingImagesEnabled);
    }

    [Fact]
    public void One_am_to_six_am_is_hard_quiet_for_proactive_presence()
    {
        var now = DateTimeOffset.Parse("2026-08-18T19:30:00+00:00"); // 02:30 VN
        Assert.True(ZaloDailyGreetingEngine.IsHardQuiet(now));

        var snapshot = new ZaloSocialPresenceSnapshot(
            "g1",
            now,
            now.AddHours(-5),
            now.AddHours(-5),
            0,
            0,
            null,
            null,
            null,
            null);
        var move = ZaloGroupEngagementDirector.Plan(
            snapshot,
            ZaloSocialPresenceSettings.FromConfiguration(new ConfigurationBuilder().Build()));

        Assert.Null(move);
    }

    [Theory]
    [InlineData("2026-08-19T00:15:00+00:00", true)]  // 07:15 VN
    [InlineData("2026-08-19T16:00:00+00:00", true)]  // 23:00 VN
    [InlineData("2026-08-19T17:30:00+00:00", true)]  // 00:30 VN
    [InlineData("2026-08-19T06:00:00+00:00", false)] // 13:00 VN
    public void Morning_and_bedtime_are_soft_non_trash_zones(string utc, bool expected)
    {
        Assert.Equal(expected, ZaloDailyGreetingEngine.IsSoftGreetingZone(DateTimeOffset.Parse(utc)));
    }

    [Fact]
    public void Morning_greeting_is_warm_and_never_uses_trash_talk_language()
    {
        var now = DateTimeOffset.Parse("2026-08-19T01:44:00+00:00"); // 08:44 VN
        var plan = ZaloDailyGreetingEngine.Plan(
            Snapshot("morning", now),
            AlwaysGreeting(),
            minBotIntervalMinutes: 60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Morning, plan!.Kind);
        Assert.False(ZaloTrashTalkPolicy.ContainsProfanityOrInsult(ZaloBotIntelligence.Normalize(plan.Message)));
        Assert.DoesNotContain("đm", plan.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vcl", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Night_greeting_is_affectionate_without_becoming_trash_talk()
    {
        var now = DateTimeOffset.Parse("2026-08-19T17:19:00+00:00"); // 00:19 VN, service date 19/8
        var plan = ZaloDailyGreetingEngine.Plan(
            Snapshot("night", now),
            AlwaysGreeting(),
            minBotIntervalMinutes: 60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Night, plan!.Kind);
        Assert.False(ZaloTrashTalkPolicy.ContainsProfanityOrInsult(ZaloBotIntelligence.Normalize(plan.Message)));
        Assert.True(plan.Message.Contains("ngủ", StringComparison.OrdinalIgnoreCase) ||
                    plan.Message.Contains("night", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exact_phrase_used_recently_is_not_repeated()
    {
        var now = DateTimeOffset.Parse("2026-08-19T01:44:00+00:00");
        var first = ZaloDailyGreetingEngine.Plan(
            Snapshot("repeat", now),
            AlwaysGreeting(),
            60);
        Assert.NotNull(first);

        var history = new[]
        {
            new ZaloSocialHistoryMessage(first!.Message, now.AddDays(-1))
        };
        var second = ZaloDailyGreetingEngine.Plan(
            Snapshot("repeat", now, history),
            AlwaysGreeting(),
            60);

        Assert.NotNull(second);
        Assert.NotEqual(first.Message, second!.Message);
    }

    [Fact]
    public void Same_service_day_never_gets_two_morning_greetings()
    {
        var now = DateTimeOffset.Parse("2026-08-19T01:44:00+00:00");
        var first = ZaloDailyGreetingEngine.Plan(
            Snapshot("once", now),
            AlwaysGreeting(),
            60);
        Assert.NotNull(first);

        var history = new[]
        {
            new ZaloSocialHistoryMessage(first!.Message, now.AddMinutes(-20))
        };
        var duplicate = ZaloDailyGreetingEngine.Plan(
            Snapshot("once", now, history),
            AlwaysGreeting(),
            60);

        Assert.Null(duplicate);
    }

    [Fact]
    public void Greeting_cards_are_occasional_not_every_greeting()
    {
        var now = DateTimeOffset.Parse("2026-08-19T01:44:00+00:00");
        var plans = Enumerable.Range(1, 40)
            .Select(index => ZaloDailyGreetingEngine.Plan(
                Snapshot($"g-{index}", now),
                AlwaysGreeting(),
                60))
            .Where(plan => plan is not null)
            .Cast<ZaloDailyGreetingPlan>()
            .ToArray();

        Assert.Contains(plans, plan => plan.UseImage);
        Assert.Contains(plans, plan => !plan.UseImage);
    }

    [Fact]
    public void Greeting_card_renderer_returns_real_png()
    {
        var cases = new[]
        {
            (ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.Warm),
            (ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.PlayfulRomantic),
            (ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.Warm),
            (ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.MenlySupportive)
        };

        foreach (var (kind, mood) in cases)
        {
            var png = ZaloSocialGreetingCardRenderer.Render(kind, mood);
            Assert.True(png.Length > 10_000);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        }
    }

    [Fact]
    public void Mood_selector_exposes_warm_romantic_and_supportive_personas()
    {
        Assert.Equal(ZaloDailyGreetingMood.Warm, ZaloDailyGreetingEngine.SelectMood(10));
        Assert.Equal(ZaloDailyGreetingMood.PlayfulRomantic, ZaloDailyGreetingEngine.SelectMood(70));
        Assert.Equal(ZaloDailyGreetingMood.MenlySupportive, ZaloDailyGreetingEngine.SelectMood(92));
    }

    private static ZaloDailyGreetingSnapshot Snapshot(
        string groupId,
        DateTimeOffset now,
        IReadOnlyList<ZaloSocialHistoryMessage>? history = null) => new(
            groupId,
            now,
            LastBotMessageAt: null,
            RecentTwoMinuteMessageCount: 0,
            BotHistory: history ?? []);

    private static ZaloDailySocialSettings AlwaysGreeting() => new(
        Enabled: true,
        MorningGreetingEnabled: true,
        NightGreetingEnabled: true,
        GreetingDaysPerWeek: 7,
        GreetingRepeatDays: 14,
        GreetingImagesEnabled: true);
}
