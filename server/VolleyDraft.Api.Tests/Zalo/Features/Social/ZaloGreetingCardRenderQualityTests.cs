using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingCardRenderQualityTests
{
    [Theory]
    [InlineData("Chào ngày mới ☀️", "Chào ngày mới", 1)]
    [InlineData("Ngủ ngon nhé 🌙", "Ngủ ngon nhé", 2)]
    [InlineData("Nhẹ lòng nha 😌", "Nhẹ lòng nha", 5)]
    [InlineData("Cùng nhau nhé 🤝", "Cùng nhau nhé", 6)]
    public void PrepareText_removes_platform_emoji_and_returns_vector_icon(
        string input,
        string expectedText,
        int expectedIcon)
    {
        var text = ZaloGreetingCardRenderQuality.PrepareText(input, out var icon);

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedText.Length, text.Length);
        Assert.Equal(expectedIcon, (int)icon);
    }

    [Fact]
    public void Morning_renderer_outputs_lossless_png()
    {
        var copy = ZaloSocialCardCopyGenerator.CreateFallback(
            ZaloDailyGreetingKind.Morning,
            ZaloDailyGreetingMood.Warm,
            hasMatchToday: true);

        var bytes = ZaloSocialGreetingCardRenderer.Render(3, "CLB Tân bình-The First Spike", copy);

        Assert.True(IsPng(bytes));
        Assert.True(bytes.Length > 10_000);
    }

    [Fact]
    public void Night_renderer_outputs_lossless_png()
    {
        var copy = ZaloNightGreetingCardCopyGenerator.CreateFallback(ZaloDailyGreetingMood.TenderRomantic);

        var bytes = ZaloNightGreetingCardRenderer.Render(1, "CLB Tân bình-The First Spike", copy);

        Assert.True(IsPng(bytes));
        Assert.True(bytes.Length > 10_000);
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 &&
        bytes[0] == 0x89 &&
        bytes[1] == 0x50 &&
        bytes[2] == 0x4E &&
        bytes[3] == 0x47 &&
        bytes[4] == 0x0D &&
        bytes[5] == 0x0A &&
        bytes[6] == 0x1A &&
        bytes[7] == 0x0A;
}
