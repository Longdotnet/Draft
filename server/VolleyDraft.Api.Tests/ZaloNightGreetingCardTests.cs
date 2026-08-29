using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNightGreetingCardTests
{
    [Fact]
    public void Night_greeting_is_card_first_by_default_with_independent_day_setting()
    {
        var settings = ZaloDailySocialSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(settings.NightGreetingEnabled);
        Assert.True(settings.NightGreetingCardFirst);
        Assert.Equal(5, settings.DaysPerWeek(ZaloDailyGreetingKind.Morning));
        Assert.Equal(5, settings.DaysPerWeek(ZaloDailyGreetingKind.Night));
    }

    [Theory]
    [InlineData(0, "TenderRomantic")]
    [InlineData(39, "TenderRomantic")]
    [InlineData(40, "LonelyComfort")]
    [InlineData(69, "LonelyComfort")]
    [InlineData(70, "CozyGroupLove")]
    [InlineData(89, "CozyGroupLove")]
    [InlineData(90, "LightPlayfulSweet")]
    [InlineData(99, "LightPlayfulSweet")]
    public void Night_mood_distribution_is_40_30_20_10(int selector, string expectedName)
    {
        var expected = Enum.Parse<ZaloDailyGreetingMood>(expectedName);
        Assert.Equal(expected, ZaloDailyGreetingEngine.SelectNightMood(selector));
    }

    [Fact]
    public void Late_night_plan_requires_card_and_keeps_previous_service_date_after_midnight()
    {
        var now = DateTimeOffset.Parse("2026-08-19T17:19:00+00:00"); // 00:19 VN on Aug 20
        var settings = new ZaloDailySocialSettings(
            Enabled: true,
            MorningGreetingEnabled: true,
            NightGreetingEnabled: true,
            GreetingDaysPerWeek: 7,
            GreetingRepeatDays: 14,
            GreetingImagesEnabled: true)
        {
            NightGreetingCardFirst = true
        };

        var plan = ZaloDailyGreetingEngine.Plan(
            new ZaloDailyGreetingSnapshot(
                "night-card-first",
                now,
                LastBotMessageAt: null,
                RecentTwoMinuteMessageCount: 0,
                BotHistory: Array.Empty<ZaloSocialHistoryMessage>()),
            settings,
            minBotIntervalMinutes: 60);

        Assert.NotNull(plan);
        Assert.Equal(ZaloDailyGreetingKind.Night, plan!.Kind);
        Assert.True(plan.UseImage);
        Assert.True(plan.RequiresImage);
        Assert.Equal(new DateOnly(2026, 8, 19), plan.ServiceDate);
        Assert.Contains(plan.Mood, new[]
        {
            ZaloDailyGreetingMood.TenderRomantic,
            ZaloDailyGreetingMood.LonelyComfort,
            ZaloDailyGreetingMood.CozyGroupLove,
            ZaloDailyGreetingMood.LightPlayfulSweet
        });
    }

    [Fact]
    public void Night_phrase_catalog_is_large_and_non_operational()
    {
        var phrases = ZaloDailyGreetingPhraseCatalog.All(ZaloDailyGreetingKind.Night);

        Assert.True(phrases.Count >= 45);
        foreach (var phrase in phrases)
        {
            var normalized = $" {ZaloBotIntelligence.Normalize(phrase)} ";
            Assert.DoesNotContain(" draft ", normalized);
            Assert.DoesNotContain(" slot ", normalized);
            Assert.DoesNotContain(" waitlist ", normalized);
            Assert.DoesNotContain(" thanh toan ", normalized);
        }
    }

    [Fact]
    public void Night_copy_guard_rejects_operational_copy()
    {
        var safe = new ZaloSocialCardCopy(
            "Đêm nay dịu lại nha",
            "Mong ai đang mệt sẽ được nghỉ thật yên và thấy lòng ấm hơn một chút.",
            "Ngủ ngoan nhé 🌙");
        var operational = new ZaloSocialCardCopy(
            "Ngủ ngon nha",
            "Mai nhớ draft đội hình và kiểm tra slot trước khi lên sân nha.",
            "Hẹn mai");

        Assert.True(ZaloNightGreetingCardCopyGenerator.IsNightSafe(safe));
        Assert.False(ZaloNightGreetingCardCopyGenerator.IsNightSafe(operational));
    }

    [Fact]
    public void Night_background_catalog_contains_five_ids()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ZaloNightGreetingBackgroundCatalog.ActiveIds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Night_background_is_embedded_and_renderable(int id)
    {
        var resources = typeof(ZaloNightGreetingCardRenderer).Assembly
            .GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);
        var copy = new ZaloSocialCardCopy(
            "Đêm nay dịu lại nha",
            "Mong mỗi người có một giấc ngủ thật yên và một trái tim nhẹ hơn.",
            "Ngủ ngoan nhé 🌙");

        Assert.Contains(ZaloNightGreetingBackgroundCatalog.LogicalResourceName(id), resources);
        var jpeg = ZaloNightGreetingCardRenderer.Render(id, "Volley Friends", copy);
        Assert.True(jpeg.Length > 10_000);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpeg.Take(3).ToArray());
    }
}
