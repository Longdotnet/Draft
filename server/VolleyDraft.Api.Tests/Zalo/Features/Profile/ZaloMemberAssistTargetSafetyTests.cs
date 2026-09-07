using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistTargetSafetyTests
{
    [Theory]
    [InlineData("share slot với To An")]
    [InlineData("share slot cho To An")]
    [InlineData("pass slot cho Nam")]
    [InlineData("nhường suất cho Huy")]
    [InlineData("trả slot cho Long")]
    [InlineData("chuyển slot cho Phước")]
    [InlineData("PASS SLOT CHO NAM")]
    [InlineData("ai share slot với tui")]
    [InlineData("share slot cho mình")]
    public void Targeted_transfer_language_must_not_open_a_public_self_pass_offer(string text)
    {
        Assert.False(ZaloMemberAssistService.IsPassSlotHelpOpportunity(text));
    }

    [Theory]
    [InlineData("pass slot T6 nha")]
    [InlineData("nhường suất CN nè")]
    [InlineData("share slot T6 nha")]
    [InlineData("ai lấy slot của tui không")]
    public void Untargeted_self_pass_language_remains_deterministic_without_ai(string text)
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity(text));
    }
}