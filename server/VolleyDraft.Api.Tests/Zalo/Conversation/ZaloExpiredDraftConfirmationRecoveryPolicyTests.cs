using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloExpiredDraftConfirmationRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_quoted_expired_confirmation_recovers_context_only_for_one_session()
    {
        var pending = Pending(Now.AddMinutes(-1));
        var quote = Quote("bot-confirm-1");
        var relation = Relation("bot-confirm-1", "draft-request-1");
        var source = PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1));

        var sessionId = ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending, quote, relation, source, Now);

        Assert.Equal("session-t4", sessionId);
    }

    [Fact]
    public void Unexpired_confirmation_never_enters_recovery_path()
    {
        var pending = Pending(Now.AddMinutes(1));

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1)),
            Now));
    }

    [Fact]
    public void Expired_context_older_than_bounded_recovery_window_is_not_revived()
    {
        var pending = Pending(
            Now.AddHours(-4).AddMinutes(-1),
            updatedAt: Now.AddHours(-4).AddSeconds(-1));

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1)),
            Now));
    }

    [Theory]
    [InlineData("ReplyTo", "bot-confirm-1", "draft-request-1")]
    [InlineData("BotReply", "other-bot", "draft-request-1")]
    [InlineData("BotReply", "bot-confirm-1", "other-parent")]
    public void Quote_must_be_exact_provider_bot_reply_provenance(
        string relationType,
        string relationFrom,
        string relationTo)
    {
        var pending = Pending(Now.AddMinutes(-1));

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation(relationFrom, relationTo, relationType),
            PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1)),
            Now));
    }

    [Fact]
    public void Different_sender_cannot_borrow_somebody_elses_expired_confirmation()
    {
        var pending = Pending(Now.AddMinutes(-1));
        var source = PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1));
        source.SenderId = "other-user";

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            source,
            Now));
    }

    [Fact]
    public void Unrelated_bot_reply_cannot_refresh_expired_draft_authority()
    {
        var pending = Pending(Now.AddMinutes(-1));
        var source = PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1));
        source.SelectedIntent = ZaloBotIntent.TeamImage.ToString();

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            source,
            Now));
    }

    [Fact]
    public void Ambiguous_multi_session_payload_fails_closed_instead_of_guessing()
    {
        var pending = Pending(Now.AddMinutes(-1));
        pending.PendingPayloadJson = "[\"session-t4\",\"session-t6\"]";

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1)),
            Now));
    }

    [Fact]
    public void Malformed_pending_payload_fails_closed()
    {
        var pending = Pending(Now.AddMinutes(-1));
        pending.PendingPayloadJson = "{not-json";

        Assert.Null(ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
            pending,
            Quote("bot-confirm-1"),
            Relation("bot-confirm-1", "draft-request-1"),
            PromptSource("draft-request-1", pending.UpdatedAt.AddSeconds(1)),
            Now));
    }

    private static ZaloBotConversationState Pending(
        DateTimeOffset expiresAt,
        DateTimeOffset? updatedAt = null) =>
        new()
        {
            Id = "pending-1",
            ZaloConnectionId = "connection-1",
            GroupId = "group-1",
            SenderZaloUserId = "user-1",
            PendingIntent = ZaloBotIntent.AutoDraftConfirm.ToString(),
            PendingPayloadJson = "[\"session-t4\"]",
            PreviousCommand = ZaloBotIntent.AutoDraft.ToString(),
            ExpiresAt = expiresAt,
            CreatedAt = Now.AddMinutes(-20),
            UpdatedAt = updatedAt ?? Now.AddMinutes(-10)
        };

    private static ZaloQuotedSemanticContext Quote(string messageId) =>
        new(
            messageId,
            "bot-1",
            "Npc",
            "Đội hình đã sẵn sàng, xác nhận draft để chạy.",
            "text",
            Now.AddMinutes(-10),
            true,
            false,
            true);

    private static ZaloMessageGraphRelation Relation(
        string fromMessageId,
        string toMessageId,
        string relationType = "BotReply") =>
        new(
            "relation-1",
            "connection-1",
            "group-1",
            fromMessageId,
            toMessageId,
            relationType,
            null,
            null,
            null,
            fromMessageId,
            Now.AddMinutes(-10));

    private static ZaloGroupMessage PromptSource(string messageId, DateTimeOffset repliedAt) =>
        new()
        {
            Id = "source-row-1",
            ZaloConnectionId = "connection-1",
            GroupId = "group-1",
            MessageId = messageId,
            SenderId = "user-1",
            SenderName = "User",
            Content = "@Npc 9 T4",
            IsFromBot = false,
            SelectedIntent = ZaloBotIntent.AutoDraft.ToString(),
            BotReplySentAt = repliedAt,
            SentAt = Now.AddMinutes(-11),
            ReceivedAt = Now.AddMinutes(-11)
        };
}
