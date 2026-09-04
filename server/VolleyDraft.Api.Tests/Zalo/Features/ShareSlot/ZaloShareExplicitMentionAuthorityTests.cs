using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareExplicitMentionAuthorityTests
{
    [Theory]
    [InlineData("tui")]
    [InlineData("mình")]
    [InlineData("tôi")]
    public void Single_structured_partner_mention_overrides_stale_parsed_partner_label_for_self_service_anchor(string anchor)
    {
        var stale = new ZaloShareSlotCommand(
            anchor,
            ["Thanh Tuyền"],
            1,
            "T6");

        var result = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("uid-anh-tu", "Anh Tú")],
            stale,
            stale);

        Assert.NotNull(result);
        Assert.Equal(anchor, result.Anchor);
        Assert.Equal(["Anh Tú"], result.Partners);
        Assert.Equal(["uid-anh-tu"], result.PartnerZaloUserIds);
        Assert.Equal("T6", result.SessionReference);
    }

    [Fact]
    public void Single_anchor_mention_still_binds_anchor_instead_of_replacing_partner()
    {
        var command = new ZaloShareSlotCommand(
            "Hiệp Hoàng Phạm",
            ["Thanh Tuyền"],
            1);

        var result = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("uid-hiep", "Hiệp Hoàng Phạm")],
            command,
            command);

        Assert.NotNull(result);
        Assert.Equal("uid-hiep", result.AnchorZaloUserId);
        Assert.Equal(["Thanh Tuyền"], result.Partners);
        Assert.Null(result.PartnerZaloUserIds);
    }

    [Fact]
    public void One_unmatched_mention_does_not_guess_between_two_partners()
    {
        var command = new ZaloShareSlotCommand(
            "Hiệp Hoàng Phạm",
            ["An", "Bình"],
            2);

        var result = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("uid-anh-tu", "Anh Tú")],
            command,
            command);

        Assert.NotNull(result);
        Assert.Equal(["An", "Bình"], result.Partners);
        Assert.Null(result.PartnerZaloUserIds);
    }

    [Fact]
    public void One_unmatched_mention_does_not_guess_partner_for_named_anchor()
    {
        var command = new ZaloShareSlotCommand(
            "Hiệp Hoàng Phạm",
            ["Thanh Tuyền"],
            1,
            "T6");

        var result = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("uid-anh-tu", "Anh Tú")],
            command,
            command);

        Assert.Same(command, result);
        Assert.Equal(["Thanh Tuyền"], result!.Partners);
        Assert.Null(result.PartnerZaloUserIds);
    }
}
