using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloUnshareIntentTests
{
    [Theory]
    [InlineData("tui không share slot với To An nữa")]
    [InlineData("tui với To An ko share nữa")]
    [InlineData("hủy share slot với To An")]
    [InlineData("tách slot, không thay phiên nữa")]
    public void Unshare_phrases_are_classified_before_normal_share(string text)
    {
        Assert.Equal(ZaloBotIntent.UnshareSlot, ZaloBotIntelligence.ClassifyDeterministically(text).Intent);
    }

    [Fact]
    public void Normal_share_is_still_share_slot()
    {
        Assert.Equal(
            ZaloBotIntent.ShareSlot,
            ZaloBotIntelligence.ClassifyDeterministically("tui muốn share slot với To An").Intent);
    }
}
