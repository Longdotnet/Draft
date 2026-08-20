using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingCardFallbackTests
{
    [Theory]
    [InlineData(ZaloDailyGreetingMood.Warm, false)]
    [InlineData(ZaloDailyGreetingMood.PlayfulRomantic, false)]
    [InlineData(ZaloDailyGreetingMood.MenlySupportive, false)]
    [InlineData(ZaloDailyGreetingMood.Warm, true)]
    public void Morning_fallback_is_always_renderer_safe(ZaloDailyGreetingMood mood, bool hasMatchToday)
    {
        var copy = ZaloSocialCardCopyGenerator.CreateFallback(
            ZaloDailyGreetingKind.Morning,
            mood,
            hasMatchToday);

        Assert.True(ZaloSocialCardCopyGenerator.IsValid(copy));
    }

    [Theory]
    [InlineData(ZaloDailyGreetingMood.TenderRomantic)]
    [InlineData(ZaloDailyGreetingMood.LonelyComfort)]
    [InlineData(ZaloDailyGreetingMood.CozyGroupLove)]
    [InlineData(ZaloDailyGreetingMood.LightPlayfulSweet)]
    public void Night_fallback_is_always_renderer_safe(ZaloDailyGreetingMood mood)
    {
        var copy = ZaloNightGreetingCardCopyGenerator.CreateFallback(mood);

        Assert.True(ZaloNightGreetingCardCopyGenerator.IsNightSafe(copy));
    }
}
