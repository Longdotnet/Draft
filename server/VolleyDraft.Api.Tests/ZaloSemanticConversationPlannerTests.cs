using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSemanticConversationPlannerTests
{
    [Fact]
    public void Quoted_claim_keeps_owner_and_source_message_as_grounding_only()
    {
        var incoming = Message(
            "claim-1",
            "user-nam",
            "Nam",
            "tui nhận",
            new ZaloBridgeMessageQuote(
                "pass-1",
                "user-long",
                "Long",
                "tui pass slot",
                "chat",
                DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
                null));

        var plan = ZaloSemanticConversationPlanner.Build(
            incoming,
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.ClaimOpenSlot,
                .96,
                "quoted_open_offer"));

        Assert.Equal(ZaloAmbientDomainIntentKind.ClaimOpenSlot, plan.Kind);
        Assert.Equal("user-nam", plan.ActorSenderId);
        Assert.Equal("user-long", plan.ReferencedMemberId);
        Assert.Equal("Long", plan.ReferencedMemberName);
        Assert.Equal("pass-1", plan.SourceMessageId);
        Assert.False(plan.NeedsClarification);
        Assert.True(plan.RequiresAuthoritativeValidation);
        Assert.True(plan.CanEnterDeterministicRouter);
    }

    [Fact]
    public void Bare_claim_does_not_invent_slot_owner()
    {
        var plan = ZaloSemanticConversationPlanner.Build(
            Message("claim-2", "user-nam", "Nam", "tui nhận"),
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.ClaimOpenSlot,
                .94,
                "context_claim"));

        Assert.Null(plan.ReferencedMemberId);
        Assert.Null(plan.ReferencedMemberName);
        Assert.Null(plan.SourceMessageId);
        Assert.True(plan.NeedsClarification);
        Assert.False(plan.CanEnterDeterministicRouter);
    }

    [Fact]
    public void Self_pass_keeps_sender_as_actor_but_never_marks_state_as_changed()
    {
        var plan = ZaloSemanticConversationPlanner.Build(
            Message("pass-2", "user-long", "Long", "chắc nghỉ, tui pass nha"),
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.PassOwnSlot,
                .91,
                "self_pass"));

        Assert.Equal("user-long", plan.ActorSenderId);
        Assert.Null(plan.ReferencedMemberId);
        Assert.True(plan.RequiresAuthoritativeValidation);
        Assert.True(plan.CanEnterDeterministicRouter);
    }

    [Fact]
    public void None_plan_never_enters_domain_router()
    {
        var plan = ZaloSemanticConversationPlanner.Build(
            Message("chat-1", "user-long", "Long", "nay vui ghê"),
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.None,
                .20,
                "general_chat"));

        Assert.False(plan.RequiresAuthoritativeValidation);
        Assert.False(plan.CanEnterDeterministicRouter);
    }

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content,
        ZaloBridgeMessageQuote? quote = null) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: messageId,
            senderId: senderId,
            senderName: senderName,
            content: content,
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: quote);
}
