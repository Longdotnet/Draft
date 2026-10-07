using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionPreRouteOwnershipTests
{
    [Theory]
    [InlineData("@Npc 9")]
    [InlineData("@Npc 9 cn 6/9")]
    [InlineData("@Npc T6 còn thiếu bao nhiêu slot?")]
    [InlineData("@Npc help")]
    public void Explicit_deterministic_bot_intent_bypasses_auto_session(string content)
    {
        Assert.True(ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            Explicit(content),
            hasActiveLegacyPending: false));
    }

    [Theory]
    [InlineData("@Npc cn 6/9")]
    [InlineData("@Npc T6")]
    [InlineData("@Npc xác nhận")]
    [InlineData("@Npc xác nhận draft")]
    public void Active_legacy_pending_workflow_bypasses_auto_session_for_short_followup(string content)
    {
        Assert.True(ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            Explicit(content),
            hasActiveLegacyPending: true));
    }

    [Theory]
    [InlineData("@Npc cn 6/9")]
    [InlineData("@Npc T6 thôi")]
    [InlineData("@Npc tạo đi")]
    [InlineData("@Npc xác nhận")]
    public void Ambiguous_auto_session_language_remains_available_without_other_owner(string content)
    {
        Assert.False(ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            Explicit(content),
            hasActiveLegacyPending: false));
    }

    [Theory]
    [InlineData("@Npc tạo trận ngày mai")]
    [InlineData("@Npc tạo trận thứ 4 7/10")]
    [InlineData("@Npc tạo lịch T6")]
    public void Addressed_create_selection_can_prime_auto_session_without_active_conversation(string content)
    {
        var incoming = Explicit(content);
        var bypass = ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            incoming,
            hasActiveLegacyPending: false);

        Assert.False(bypass);
        Assert.True(ZaloAutoSessionPreRouteOwnership.ShouldPrimeCreateSelectionConversation(
            incoming,
            bypass));
    }

    [Fact]
    public void Create_selection_does_not_prime_when_another_route_owns_the_turn()
    {
        var incoming = Explicit("@Npc tạo trận ngày mai");
        var bypass = ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            incoming,
            hasActiveLegacyPending: true);

        Assert.True(bypass);
        Assert.False(ZaloAutoSessionPreRouteOwnership.ShouldPrimeCreateSelectionConversation(
            incoming,
            bypass));
    }

    [Theory]
    [InlineData("@Npc không tạo trận ngày mai")]
    [InlineData("@Npc tạo đi")]
    public void Non_create_selection_language_does_not_prime_auto_session(string content)
    {
        var incoming = Explicit(content);
        var bypass = ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            incoming,
            hasActiveLegacyPending: false);

        Assert.False(ZaloAutoSessionPreRouteOwnership.ShouldPrimeCreateSelectionConversation(
            incoming,
            bypass));
    }

    [Fact]
    public void Unaddressed_create_selection_does_not_open_a_new_conversation()
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m-unaddressed-create",
            senderId: "user-long",
            senderName: "Thanh Long",
            content: "tạo trận ngày mai",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(ZaloAutoSessionPreRouteOwnership.ShouldPrimeCreateSelectionConversation(
            incoming,
            bypassAutoSession: false));
    }

    [Fact]
    public void Unaddressed_turn_is_not_taken_away_from_auto_session_implicit_context()
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m-unaddressed",
            senderId: "user-long",
            senderName: "Thanh Long",
            content: "cn 6/9",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            incoming,
            hasActiveLegacyPending: true));
    }

    [Theory]
    [InlineData("draft đi")]
    [InlineData("draft di T7")]
    [InlineData("xác nhận draft")]
    public void Strong_natural_draft_confirmation_bypasses_auto_session_even_without_mention(string content)
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: $"m-{Guid.NewGuid():N}",
            senderId: "user-long",
            senderName: "Thanh Long",
            content: content,
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.True(ZaloAutoSessionPreRouteOwnership.ShouldBypassAutoSession(
            incoming,
            hasActiveLegacyPending: false));
    }

    private static ZaloIncomingMessageEvent Explicit(string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: $"m-{Guid.NewGuid():N}",
        senderId: "user-long",
        senderName: "Thanh Long",
        content: content,
        mentions: [],
        mentionedBot: true,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
