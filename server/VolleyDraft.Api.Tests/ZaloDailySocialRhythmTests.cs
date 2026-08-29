using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
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
        Assert.False(ContainsExplicitTrash(plan.Message));
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
        Assert.False(ContainsExplicitTrash(plan.Message));
        Assert.True(plan.Message.Contains("ngủ", StringComparison.OrdinalIgnoreCase) ||
                    plan.Message.Contains("night", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_curated_greeting_pool_is_free_of_explicit_trash_talk()
    {
        foreach (var kind in Enum.GetValues<ZaloDailyGreetingKind>())
            Assert.DoesNotContain(ZaloDailyGreetingPhraseCatalog.All(kind), ContainsExplicitTrash);
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
    public void Morning_greetings_always_require_cards_when_images_are_enabled()
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

        Assert.NotEmpty(plans);
        Assert.All(plans, plan =>
        {
            Assert.Equal(ZaloDailyGreetingKind.Morning, plan.Kind);
            Assert.True(plan.RequiresImage);
            Assert.True(plan.UseImage);
        });
    }

    [Fact]
    public void Morning_still_requires_a_card_when_greeting_images_are_disabled()
    {
        var now = DateTimeOffset.Parse("2026-08-19T01:44:00+00:00");
        var settings = AlwaysGreeting() with { GreetingImagesEnabled = false };
        var plan = ZaloDailyGreetingEngine.Plan(
            Snapshot("morning-no-media", now),
            settings,
            60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Morning, plan!.Kind);
        Assert.True(plan.RequiresImage);
        Assert.False(plan.UseImage);
    }

    [Fact]
    public void Night_cards_can_still_be_optional_when_card_first_is_disabled()
    {
        var now = DateTimeOffset.Parse("2026-08-19T17:19:00+00:00"); // 00:19 VN
        var settings = AlwaysGreeting() with { NightGreetingCardFirst = false };
        var plans = Enumerable.Range(1, 80)
            .Select(index => ZaloDailyGreetingEngine.Plan(
                Snapshot($"night-{index}", now),
                settings,
                60))
            .Where(plan => plan is not null)
            .Cast<ZaloDailyGreetingPlan>()
            .ToArray();

        Assert.NotEmpty(plans);
        Assert.All(plans, plan => Assert.False(plan.RequiresImage));
        Assert.Contains(plans, plan => plan.UseImage);
        Assert.Contains(plans, plan => !plan.UseImage);
    }

    [Fact]
    public void Dynamic_card_renderer_uses_each_active_morning_background_and_returns_real_jpeg()
    {
        var copy = new ZaloSocialCardCopy(
            "CHÀO NGÀY MỚI",
            "Đủ năng lượng để làm việc ngon lành, tối còn sức ra sân.",
            "Hôm nay cứ vui trước đã");

        foreach (var backgroundId in ZaloSocialCardBackgroundCatalog.ActiveIds)
        {
            var image = ZaloSocialGreetingCardRenderer.Render(
                backgroundId,
                "CLB Bóng Chuyền Sài Gòn",
                copy);

            Assert.True(image.Length > 100_000);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, image.Take(3).ToArray());
        }
    }

    [Fact]
    public void Legacy_morning_background_five_resolves_to_background_one_resource()
    {
        Assert.Equal(
            ZaloSocialCardBackgroundCatalog.LogicalResourceName(1),
            ZaloSocialCardBackgroundCatalog.LogicalResourceName(5));
        Assert.DoesNotContain(5, ZaloSocialCardBackgroundCatalog.ActiveIds);
    }

    [Fact]
    public void Background_deck_uses_all_four_before_repeating_and_avoids_cycle_boundary_repeat()
    {
        var firstCycle = ZaloSocialCardDeckLogic.BuildShuffledDeck(null);
        Assert.Equal(4, firstCycle.Count);
        Assert.Equal(4, firstCycle.Distinct().Count());
        Assert.All(firstCycle, id => Assert.InRange(id, 1, 4));

        var secondCycle = ZaloSocialCardDeckLogic.BuildShuffledDeck(firstCycle[^1]);
        Assert.Equal(4, secondCycle.Distinct().Count());
        Assert.NotEqual(firstCycle[^1], secondCycle[0]);
    }

    [Fact]
    public async Task Social_card_memory_is_idempotent_and_rotates_without_repeat_for_first_four()
    {
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.OpenConnectionAsync();

        var assigned = new List<ZaloSocialCardMemory>();
        for (var index = 1; index <= 4; index++)
        {
            assigned.Add(await ZaloSocialCardMemoryStore.RememberAsync(
                db,
                $"occurrence-{index}",
                "connection-1",
                "group-1",
                "CLB Test",
                new ZaloSocialCardCopy(
                    $"Headline {index}",
                    $"Body copy number {index} đủ dài để hợp lệ.",
                    $"Ribbon {index}")));
        }

        Assert.Equal(4, assigned.Select(item => item.BackgroundId).Distinct().Count());
        Assert.All(assigned, item => Assert.Equal(1, item.CycleNumber));

        var retry = await ZaloSocialCardMemoryStore.RememberAsync(
            db,
            "occurrence-1",
            "connection-1",
            "group-1",
            "Tên group đã đổi",
            new ZaloSocialCardCopy(
                "Headline khác",
                "Body copy khác nhưng retry không được ghi đè.",
                "Ribbon khác"));

        Assert.Equal(assigned[0].BackgroundId, retry.BackgroundId);
        Assert.Equal(assigned[0].Headline, retry.Headline);
        Assert.Equal("CLB Test", retry.GroupName);

        var fifth = await ZaloSocialCardMemoryStore.RememberAsync(
            db,
            "occurrence-5",
            "connection-1",
            "group-1",
            "CLB Test",
            new ZaloSocialCardCopy(
                "Headline 5",
                "Body copy number 5 đủ dài để hợp lệ.",
                "Ribbon 5"));

        Assert.Equal(2, fifth.CycleNumber);
        Assert.NotEqual(assigned[^1].BackgroundId, fifth.BackgroundId);

        var recent = await ZaloSocialCardMemoryStore.GetRecentAsync(
            db,
            "connection-1",
            "group-1",
            take: 5);
        Assert.Equal(5, recent.Count);
    }

    [Theory]
    [InlineData("Sáng lên mood", "Uống nước rồi chiến một ngày thật gọn nha.", "Tối còn sức ra sân", true)]
    [InlineData("Hi", "Uống nước rồi chiến một ngày thật gọn nha.", "Tối còn sức ra sân", false)]
    [InlineData("Sáng lên mood", "Xem https://example.com để biết thêm thông tin.", "Tối còn sức ra sân", false)]
    [InlineData("Sáng lên mood", "đm hôm nay chiến cho đã nha.", "Tối còn sức ra sân", false)]
    public void Ai_card_copy_is_validated_before_rendering(
        string headline,
        string body,
        string ribbon,
        bool expected)
    {
        Assert.Equal(
            expected,
            ZaloSocialCardCopyGenerator.IsValid(new ZaloSocialCardCopy(headline, body, ribbon)));
    }

    [Fact]
    public void Mood_selector_exposes_warm_romantic_and_supportive_personas()
    {
        Assert.Equal(ZaloDailyGreetingMood.Warm, ZaloDailyGreetingEngine.SelectMood(10));
        Assert.Equal(ZaloDailyGreetingMood.PlayfulRomantic, ZaloDailyGreetingEngine.SelectMood(70));
        Assert.Equal(ZaloDailyGreetingMood.MenlySupportive, ZaloDailyGreetingEngine.SelectMood(92));
    }

    private static bool ContainsExplicitTrash(string message)
    {
        var normalized = $" {ZaloBotIntelligence.Normalize(message)} ";
        string[] forbidden =
        [
            " dm ", " đm ", " vcl ", " vl ", " cc ",
            " thang lon ", " oc cho ", " nhu cc ", " ga vai ", " phe vl "
        ];
        return forbidden.Any(normalized.Contains);
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
