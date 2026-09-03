using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftApprovalSafetyTests
{
    [Fact]
    public void Same_session_auto_draft_pending_can_be_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var state = Pending("AutoDraftConfirm", "[\"t6\"]", now.AddMinutes(10));

        Assert.True(ZaloDraftApprovalSafety.CanReservePending(state, "t6", now));
    }

    [Fact]
    public void Different_session_auto_draft_pending_cannot_be_overwritten()
    {
        var now = DateTimeOffset.UtcNow;
        var state = Pending("AutoDraftConfirm", "[\"t4\"]", now.AddMinutes(10));

        Assert.False(ZaloDraftApprovalSafety.CanReservePending(state, "t6", now));
    }

    [Fact]
    public void Different_active_pending_action_cannot_be_overwritten()
    {
        var now = DateTimeOffset.UtcNow;
        var state = Pending("ShareSlotConfirm", "{}", now.AddMinutes(10));

        Assert.False(ZaloDraftApprovalSafety.CanReservePending(state, "t6", now));
    }

    [Fact]
    public void Expired_pending_action_does_not_block_new_draft_confirmation()
    {
        var now = DateTimeOffset.UtcNow;
        var state = Pending("ShareSlotConfirm", "{}", now.AddSeconds(-1));

        Assert.True(ZaloDraftApprovalSafety.CanReservePending(state, "t6", now));
    }

    [Theory]
    [InlineData(SessionStatus.Setup, false)]
    [InlineData(SessionStatus.CaptainSelection, false)]
    [InlineData(SessionStatus.Drafting, false)]
    [InlineData(SessionStatus.Finished, true)]
    [InlineData(SessionStatus.Cancelled, false)]
    public void Only_finished_session_is_a_completed_draft(SessionStatus status, bool expected)
    {
        Assert.Equal(expected, ZaloDraftApprovalSafety.IsDraftCompleted(status));
    }

    private static ZaloBotConversationState Pending(string intent, string payload, DateTimeOffset expiresAt) => new()
    {
        ZaloConnectionId = "conn",
        GroupId = "group",
        SenderZaloUserId = "approver",
        PendingIntent = intent,
        PendingPayloadJson = payload,
        ExpiresAt = expiresAt
    };
}
