using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloRecruitmentGuestSingleLaneGateTests
{
    [Theory]
    [InlineData("nay tui đi chung với 1 bạn ở ngoài gr")]
    [InlineData("tui đi với 1 bạn ngoài group")]
    [InlineData("có 2 bạn ngoài nhóm đi chung")]
    [InlineData("+1")]
    [InlineData("+2 bạn tui")]
    public void LooksLikeAddRequest_CatchesDirectOutsideGuestLanguage(string text)
    {
        Assert.True(ZaloRecruitmentGuestPolicy.LooksLikeAddRequest(text));
    }

    [Theory]
    [InlineData("danh sách đội hình hnay")]
    [InlineData("Minh nam nha")]
    [InlineData("1 bạn tui nghỉ")]
    [InlineData("nay tui đi chung với Tấn Chí")]
    public void LooksLikeAddRequest_DoesNotCaptureUnrelatedTurns(string text)
    {
        Assert.False(ZaloRecruitmentGuestPolicy.LooksLikeAddRequest(text));
    }

    [Fact]
    public void MentionedDirectAdd_RequiresRecruitmentReply()
    {
        var decision = ZaloRecruitmentGuestMentionGatePolicy.Decide(
            mentionedBot: true,
            looksLikeAddRequest: true,
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.None);

        Assert.Equal(ZaloRecruitmentGuestMentionGateDecision.RequireRecruitmentReply, decision);
    }

    [Fact]
    public void MentionedAddReplyingRecruitment_IsQueuedForSingleMutationLane()
    {
        var decision = ZaloRecruitmentGuestMentionGatePolicy.Decide(
            mentionedBot: true,
            looksLikeAddRequest: true,
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast);

        Assert.Equal(ZaloRecruitmentGuestMentionGateDecision.QueueReplyGatedMutation, decision);
    }

    [Fact]
    public void MentionedAddReplyingGuestConversation_CannotAddAnotherGuest()
    {
        var decision = ZaloRecruitmentGuestMentionGatePolicy.Decide(
            mentionedBot: true,
            looksLikeAddRequest: true,
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.GuestConversation);

        Assert.Equal(ZaloRecruitmentGuestMentionGateDecision.RequireRecruitmentReply, decision);
    }

    [Fact]
    public void NonMentionedTurn_IsLeftForReplyGatedWorkerOrAmbientSuppression()
    {
        var decision = ZaloRecruitmentGuestMentionGatePolicy.Decide(
            mentionedBot: false,
            looksLikeAddRequest: true,
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast);

        Assert.Equal(ZaloRecruitmentGuestMentionGateDecision.NotApplicable, decision);
    }
}
