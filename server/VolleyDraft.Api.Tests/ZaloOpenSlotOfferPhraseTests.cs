using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotOfferPhraseTests
{
    [Theory]
    [InlineData("tui nhận")]
    [InlineData("tui lấy nha")]
    [InlineData("để tui")]
    [InlineData("em hốt luôn")]
    [InlineData("tui nhận slot T6")]
    [InlineData("tui nhận T6 nha")]
    [InlineData("tui lấy của Hoàng")]
    public void Natural_self_claim_phrases_are_detected(string text)
    {
        Assert.True(ZaloOpenSlotOfferService.IsClaimPhrase(text));
    }

    [Theory]
    [InlineData("tui nhận xét team này đẹp")]
    [InlineData("tui nhận bóng")]
    [InlineData("ai nhận giùm")]
    [InlineData("Nam nhận nha")]
    [InlineData("nhận được chưa")]
    [InlineData("tui giữ quan điểm")]
    public void Ordinary_chat_is_not_misclassified_as_slot_claim(string text)
    {
        Assert.False(ZaloOpenSlotOfferService.IsClaimPhrase(text));
    }
}
