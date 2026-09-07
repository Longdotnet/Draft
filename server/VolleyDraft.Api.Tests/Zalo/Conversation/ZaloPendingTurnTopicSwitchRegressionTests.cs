using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPendingTurnTopicSwitchRegressionTests
{
    [Theory]
    [InlineData("hủy reminder", "CancelReminder")]
    [InlineData("cancel reminder", "CancelReminder")]
    [InlineData("hủy share slot", "CancelShareSlot")]
    public void Fresh_deterministic_cancel_command_supersedes_unrelated_pending_session_choice(
        string text,
        string freshIntent)
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "TeamImage",
            text,
            mentionedBot: true,
            freshIntent,
            freshConfidence: 1);

        Assert.Equal(ZaloPendingTurnDisposition.SwitchToNewIntent, disposition);
    }

    [Theory]
    [InlineData("hủy")]
    [InlineData("cancel")]
    [InlineData("thôi khỏi")]
    public void Bare_cancel_still_cancels_pending_session_choice(string text)
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "TeamImage",
            text,
            mentionedBot: false);

        Assert.Equal(ZaloPendingTurnDisposition.CancelPending, disposition);
    }

    [Fact]
    public void Low_confidence_unrelated_guess_does_not_override_explicit_pending_cancel()
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "TeamImage",
            "hủy",
            mentionedBot: false,
            freshIntent: "GeneralChat",
            freshConfidence: .4);

        Assert.Equal(ZaloPendingTurnDisposition.CancelPending, disposition);
    }
}
