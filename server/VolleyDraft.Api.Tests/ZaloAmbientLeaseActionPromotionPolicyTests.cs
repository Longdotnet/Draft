using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeaseActionPromotionPolicyTests
{
    [Theory]
    [InlineData("xếp team T6")]
    [InlineData("chia đội CN")]
    public void Natural_team_draft_shorthand_promotes_to_autodraft(string text)
    {
        var promotion = ZaloAmbientLeaseActionPromotionPolicy.TryCreate(text);

        Assert.NotNull(promotion);
        Assert.Equal(ZaloBotIntent.AutoDraft, promotion!.Intent);
        Assert.StartsWith("auto draft ", promotion.PromotedContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("T6 còn bao nhiêu slot")]
    [InlineData("CN mấy giờ")]
    [InlineData("haha")]
    public void Facts_and_chatter_are_not_action_promotions(string text)
    {
        Assert.Null(ZaloAmbientLeaseActionPromotionPolicy.TryCreate(text));
    }
}
