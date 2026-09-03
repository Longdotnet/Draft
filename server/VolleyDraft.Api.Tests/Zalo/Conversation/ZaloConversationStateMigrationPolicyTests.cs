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
