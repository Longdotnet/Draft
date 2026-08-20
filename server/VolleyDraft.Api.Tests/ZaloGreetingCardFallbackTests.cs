using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingCardFallbackTests
{
    [Theory]
    [InlineData((int)ZaloDailyGreetingMood.Warm, false)]
    [InlineData((int)ZaloDailyGreetingMood.PlayfulRomantic, false)]
    [InlineData((int)ZaloDailyGreetingMood.MenlySupportive, false)]
    [InlineData((int)ZaloDailyGreetingMood.Warm, true)]
    public void Morning_fallback_is_always_renderer_safe(int moodValue, bool hasMatchToday)
    {
        var mood = (ZaloDailyGreetingMood)moodValue;
        var copy = ZaloSocialCardCopyGenerator.CreateFallback(
            ZaloDailyGreetingKind.Morning,
            mood,
            hasMatchToday);

        Assert.True(ZaloSocialCardCopyGenerator.IsValid(copy));
    }

    [Theory]
    [InlineData((int)ZaloDailyGreetingMood.TenderRomantic)]
    [InlineData((int)ZaloDailyGreetingMood.LonelyComfort)]
    [InlineData((int)ZaloDailyGreetingMood.CozyGroupLove)]
    [InlineData((int)ZaloDailyGreetingMood.LightPlayfulSweet)]
    public void Night_fallback_is_always_renderer_safe(int moodValue)
    {
        var mood = (ZaloDailyGreetingMood)moodValue;
        var copy = ZaloNightGreetingCardCopyGenerator.CreateFallback(mood);

        Assert.True(ZaloNightGreetingCardCopyGenerator.IsNightSafe(copy));
    }
}
