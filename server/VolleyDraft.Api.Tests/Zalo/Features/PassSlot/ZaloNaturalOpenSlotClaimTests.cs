using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalOpenSlotClaimTests
{
    [Theory]
    [InlineData("tui vô", "tui nhan")]
    [InlineData("mình vào", "tui nhan")]
    [InlineData("em vô T6", "tui nhan t6")]
    [InlineData("cho tui vào CN", "tui nhan cn")]
    [InlineData("để mình vô thứ 6", "tui nhan thu 6")]
    public void Natural_claims_promote_to_existing_open_slot_grammar(string input, string expected)
    {
        var promoted = ZaloOverbookService.TryPromoteNaturalOpenSlotClaim(input, out var canonical);

        Assert.True(promoted);
        Assert.Equal(expected, canonical);
        Assert.True(ZaloOpenSlotOfferService.IsClaimPhrase(canonical));
    }

    [Theory]
    [InlineData("tui vô ăn cơm")]
    [InlineData("cho tui vào danh sách chờ T6")]
    [InlineData("ai vô T6 vậy")]
    [InlineData("tui nghỉ T6")]
    [InlineData("vô không")]
    public void Ordinary_or_other_domain_chat_is_not_promoted(string input)
    {
        Assert.False(ZaloOverbookService.TryPromoteNaturalOpenSlotClaim(input, out _));
    }

    [Theory]
    [InlineData("lấy cái đó")]
    [InlineData("ừ lấy cái đó")]
    [InlineData("ok lấy cái đó")]
    [InlineData("chốt cái đó")]
    public void Natural_confirmation_is_only_a_pending_conversation_signal(string input)
    {
        Assert.True(ZaloOverbookService.IsNaturalPendingClaimConfirmation(input));
    }

    [Theory]
    [InlineData("lấy T6")]
    [InlineData("ok")]
    [InlineData("tui vô")]
    [InlineData("cho tui slot đó")]
    public void Broad_phrases_do_not_become_pending_confirmation(string input)
    {
        Assert.False(ZaloOverbookService.IsNaturalPendingClaimConfirmation(input));
    }
}
