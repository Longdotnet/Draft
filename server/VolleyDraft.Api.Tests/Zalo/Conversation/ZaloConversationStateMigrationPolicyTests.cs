using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationStateMigrationPolicyTests
{
    [Fact]
    public void Same_intent_family_does_not_escape_confirmation()
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            "draft lại team này");

        Assert.NotEqual(ZaloTopicSwitchDecision.SwitchToNewIntent, result.Decision);
    }

    [Fact]
    public void High_confidence_new_operational_intent_escapes_stale_pending()
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            "T6 còn thiếu bao nhiêu slot?");

        Assert.Equal(ZaloTopicSwitchDecision.SwitchToNewIntent, result.Decision);
        Assert.Equal("MissingSlots", result.FreshIntent);
        Assert.Equal("high_confidence_new_operational_intent", result.Reason);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("100+200")]
    [InlineData("hello npc")]
    public void Unrelated_free_chat_escapes_confirmation_boundary(string text)
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            text);

        Assert.Equal(ZaloTopicSwitchDecision.SwitchToNewIntent, result.Decision);
        Assert.Null(result.FreshIntent);
        Assert.Equal("confirmation_pending_unrelated_turn", result.Reason);
    }

    [Theory]
    [InlineData("xác nhận")]
    [InlineData("xác nhận draft")]
    [InlineData("ok")]
    public void Bare_confirmation_still_belongs_to_pending_preview(string text)
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            text);

        Assert.Equal(ZaloTopicSwitchDecision.ContinuePending, result.Decision);
    }

    [Fact]
    public void Domain_qualified_confirmation_is_not_stolen_by_unrelated_pending()
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            "chốt slot");

        Assert.Equal(ZaloTopicSwitchDecision.SwitchToNewIntent, result.Decision);
        Assert.Equal("WaitlistAccept", result.FreshIntent);
        Assert.Equal("high_confidence_new_operational_intent", result.Reason);
    }

    [Fact]
    public void Domain_qualified_cancel_is_not_stolen_by_unrelated_pending()
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "AutoDraftConfirm",
            "hủy lịch nhắc");

        Assert.Equal(ZaloTopicSwitchDecision.SwitchToNewIntent, result.Decision);
        Assert.Equal("CancelReminder", result.FreshIntent);
        Assert.Equal("high_confidence_new_operational_intent", result.Reason);
    }

    [Fact]
    public void Explicit_cancel_is_left_for_existing_cancel_handler()
    {
        var result = ZaloConversationStateMigrationPolicy.Evaluate(
            "SlotTransferConfirm",
            "thôi");

        Assert.Equal(ZaloTopicSwitchDecision.CancelPending, result.Decision);
        Assert.Equal("explicit_cancel", result.Reason);
    }

    [Theory]
    [InlineData("AutoDraftConfirm", "AutoDraft")]
    [InlineData("auto_draft_confirmation", "AutoDraft")]
    [InlineData("slot-transfer-confirm", "SlotTransfer")]
    public void Intent_family_normalization_handles_legacy_naming(string pending, string fresh)
    {
        Assert.True(ZaloConversationStateMigrationPolicy.SameIntentFamily(pending, fresh));
    }
}
