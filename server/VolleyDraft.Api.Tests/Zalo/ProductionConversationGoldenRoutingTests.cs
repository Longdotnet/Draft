using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo;

public sealed class ProductionConversationGoldenRoutingTests
{
    [Theory]
    [InlineData("tui nhận CN 13/09 17:45 - Chủ nhật 13/9", "tui nhan cn")]
    [InlineData("tui lấy T6 11/09 17:45 - Thứ 6 11/9", "tui nhan t6")]
    [InlineData("cho tui nhận Chủ nhật 13/09 17:45", "tui nhan chu nhat")]
    public void Verbose_open_slot_claims_from_real_chat_stay_deterministic(
        string raw,
        string expectedCanonical)
    {
        Assert.True(ZaloOverbookService.TryPromoteNaturalOpenSlotClaim(raw, out var canonical));
        Assert.Equal(expectedCanonical, canonical);
        Assert.True(ZaloOpenSlotOfferService.IsClaimPhrase(canonical));
    }

    [Theory]
    [InlineData("bot-account_0", "bot-account")]
    [InlineData(" user-long_0 ", "user-long")]
    public void Provider_identity_suffixes_are_canonicalized_before_authority_checks(
        string raw,
        string expected)
    {
        Assert.Equal(expected, ZaloOverbookLogic.NormalizeId(raw));
    }
}
