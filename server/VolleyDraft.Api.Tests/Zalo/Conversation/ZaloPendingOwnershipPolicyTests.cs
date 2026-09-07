using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPendingOwnershipPolicyTests
{
    [Theory]
    [InlineData("hủy", ZaloPendingOwnershipPolicy.SharedDisposition.CancelPending)]
    [InlineData("cancel", ZaloPendingOwnershipPolicy.SharedDisposition.CancelPending)]
    [InlineData("thôi khỏi", ZaloPendingOwnershipPolicy.SharedDisposition.CancelPending)]
    [InlineData("xác nhận", ZaloPendingOwnershipPolicy.SharedDisposition.ConfirmPending)]
    public void Bare_controls_stay_with_pending_workflow(
        string question,
        ZaloPendingOwnershipPolicy.SharedDisposition expected)
    {
        var actual = ZaloPendingOwnershipPolicy.ClassifySharedControl(
            "AutoDraftConfirm",
            question,
            "CancelReminder",
            .99);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("hủy reminder", "CancelReminder")]
    [InlineData("hủy share slot", "UnshareSlot")]
    [InlineData("chốt slot", "SlotTransferConfirm")]
    public void Qualified_high_confidence_fresh_intent_escapes_unrelated_pending(
        string question,
        string freshIntent)
    {
        var actual = ZaloPendingOwnershipPolicy.ClassifySharedControl(
            "AutoDraftConfirm",
            question,
            freshIntent,
            .99);

        Assert.Equal(ZaloPendingOwnershipPolicy.SharedDisposition.SwitchToFreshIntent, actual);
    }

    [Fact]
    public void Same_intent_family_name_does_not_switch_at_shared_boundary()
    {
        var actual = ZaloPendingOwnershipPolicy.ClassifySharedControl(
            "CancelReminder",
            "hủy reminder",
            "CancelReminder",
            .99);

        Assert.Equal(ZaloPendingOwnershipPolicy.SharedDisposition.None, actual);
    }

    [Fact]
    public void Low_confidence_guess_cannot_steal_pending_control()
    {
        var actual = ZaloPendingOwnershipPolicy.ClassifySharedControl(
            "AutoDraftConfirm",
            "hủy reminder",
            "CancelReminder",
            .40);

        Assert.Equal(ZaloPendingOwnershipPolicy.SharedDisposition.None, actual);
    }

    [Theory]
    [InlineData("hủy reminder", "CancelReminder", .99, ZaloPendingTurnDisposition.SwitchToNewIntent, ZaloTopicSwitchDecision.SwitchToNewIntent)]
    [InlineData("hủy", "CancelReminder", .99, ZaloPendingTurnDisposition.CancelPending, ZaloTopicSwitchDecision.CancelPending)]
    public void Session_and_v2_policies_share_cross_domain_precedence(
        string question,
        string freshIntent,
        double confidence,
        ZaloPendingTurnDisposition expectedSession,
        ZaloTopicSwitchDecision expectedV2)
    {
        var session = ZaloPendingTurnPolicy.ClassifySessionTurn(
            "AutoDraftConfirm",
            question,
            mentionedBot: true,
            freshIntent,
            confidence);
        var v2 = ZaloConversationStateV2Store.DecideTopicSwitch(
            "AutoDraftConfirm",
            question,
            freshIntent,
            confidence);

        Assert.Equal(expectedSession, session);
        Assert.Equal(expectedV2, v2);
    }

    [Fact]
    public void Bare_confirmation_keeps_domain_specific_semantics_after_shared_classification()
    {
        var session = ZaloPendingTurnPolicy.ClassifySessionTurn(
            "AutoDraft",
            "xác nhận",
            mentionedBot: true,
            freshIntent: null,
            freshConfidence: 0);
        var v2 = ZaloConversationStateV2Store.DecideTopicSwitch(
            "AutoDraftConfirm",
            "xác nhận",
            freshIntent: null,
            freshConfidence: 0);

        Assert.Equal(ZaloPendingTurnDisposition.SwitchToNewIntent, session);
        Assert.Equal(ZaloTopicSwitchDecision.ContinuePending, v2);
    }
}
