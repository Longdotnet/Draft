using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareNaturalUxTests
{
    [Theory]
    [InlineData("tui với @Nguyễn Minh Huy share slot", "tui", "Nguyễn Minh Huy")]
    [InlineData("tui share slot @Nguyễn Minh Huy", "tui", "Nguyễn Minh Huy")]
    [InlineData("tui share @Nguyễn Minh Huy", "tui", "Nguyễn Minh Huy")]
    [InlineData("em với @Nguyễn Minh Huy chung slot", "em", "Nguyễn Minh Huy")]
    public void Share_parser_accepts_natural_self_share_variants(string question, string anchor, string partner)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(question, out var command));
        Assert.Equal(anchor, command.Anchor);
        Assert.Equal([partner], command.Partners);
        Assert.Equal(1, command.RequestedPartnerCount);
        Assert.False(ZaloBotIntelligence.IsUnshareSlotRequest(question));
    }

    [Fact]
    public void One_explicit_partner_mention_defaults_anchor_to_sender_alias()
    {
        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("huy-id", "Nguyễn Minh Huy")],
            null);

        Assert.NotNull(command);
        Assert.Equal("tui", command!.Anchor);
        Assert.Equal(["Nguyễn Minh Huy"], command.Partners);
        Assert.Equal(["huy-id"], command.PartnerZaloUserIds);
    }

    [Theory]
    [InlineData("tui không share slot với Huy nữa")]
    [InlineData("tui huỷ share slot với Huy")]
    [InlineData("tách share slot của tui với Huy")]
    public void Explicit_unshare_language_still_routes_to_unshare(string question)
    {
        Assert.True(ZaloBotIntelligence.IsUnshareSlotRequest(question));
    }

    [Fact]
    public void Finished_session_allows_owner_self_service_without_current_poll_vote()
    {
        Assert.True(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Finished,
            senderIsCurrentPollVoter: false,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }

    [Fact]
    public void Drafting_session_does_not_allow_member_self_service()
    {
        Assert.False(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Drafting,
            senderIsCurrentPollVoter: true,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }

    [Fact]
    public void Predraft_self_service_still_requires_current_vote()
    {
        Assert.False(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Setup,
            senderIsCurrentPollVoter: false,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));

        Assert.True(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Setup,
            senderIsCurrentPollVoter: true,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }
}
