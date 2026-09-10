using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloQuotedTaskRecoveryPolicyTests
{
    [Fact]
    public void Draft_session_choice_reply_matches_provider_bot_reply_parent()
    {
        var state = State(sourceMessageId: "user-question-1", lastMessageId: "user-question-1");
        var quote = Quote("bot-provider-42", repliesToBot: true);
        var relation = Relation("bot-provider-42", "user-question-1");

        Assert.True(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(state, quote, relation));
    }

    [Fact]
    public void Draft_session_choice_reply_matches_directly_persisted_outbound_anchor()
    {
        var state = State(sourceMessageId: "user-question-1", lastMessageId: "bot-provider-42");
        var quote = Quote("bot-provider-42", repliesToBot: true);

        Assert.True(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(state, quote, null));
    }

    [Theory]
    [InlineData("other-bot-message", "user-question-1", "BotReply", true)]
    [InlineData("bot-provider-42", "different-parent", "BotReply", true)]
    [InlineData("bot-provider-42", "user-question-1", "ReplyTo", true)]
    [InlineData("bot-provider-42", "user-question-1", "BotReply", false)]
    public void Does_not_resume_when_quote_is_not_the_exact_task_anchor(
        string relationFrom,
        string relationParent,
        string relationType,
        bool repliesToBot)
    {
        var state = State(sourceMessageId: "user-question-1", lastMessageId: "user-question-1");
        var quote = Quote("bot-provider-42", repliesToBot);
        var relation = Relation(relationFrom, relationParent, relationType);

        Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(state, quote, relation));
    }

    [Fact]
    public void Does_not_use_quote_anchor_for_other_conversation_intents()
    {
        var state = State(
            sourceMessageId: "user-question-1",
            lastMessageId: "user-question-1",
            intent: "AutoDraftConfirm");
        var quote = Quote("bot-provider-42", repliesToBot: true);
        var relation = Relation("bot-provider-42", "user-question-1");

        Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(state, quote, relation));
    }

    private static ZaloConversationStateV2Snapshot State(
        string sourceMessageId,
        string lastMessageId,
        string intent = "DraftReadinessSessionChoice") =>
        new(
            "state-1",
            "group-1",
            "user-1",
            intent,
            "{}",
            "[]",
            "[\"session-1\"]",
            sourceMessageId,
            lastMessageId,
            1,
            ZaloConversationStateV2Status.Active,
            DateTimeOffset.UtcNow.AddHours(4),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static ZaloQuotedSemanticContext Quote(string messageId, bool repliesToBot) =>
        new(
            messageId,
            repliesToBot ? "bot-1" : "member-2",
            repliesToBot ? "Npc" : "Member",
            "Ông hỏi đội hình trận nào?",
            "text",
            DateTimeOffset.UtcNow.AddMinutes(-30),
            repliesToBot,
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
            DateTimeOffset.UtcNow.AddMinutes(-30));
}
