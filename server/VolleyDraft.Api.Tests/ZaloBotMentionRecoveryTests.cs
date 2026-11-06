using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotMentionRecoveryTests
{
    [Fact]
    public void Textual_bot_prefix_recovers_missing_provider_mention_metadata()
    {
        var incoming = Incoming("@Bott hôm nay khỏe không?", mentionedBot: false, mentions: []);

        var recovered = ZaloBotService.RecoverTextualBotMention(incoming, "Bott");

        Assert.True(recovered.MentionedBot);
        var mention = Assert.Single(recovered.Mentions);
        Assert.Equal("bot-1", mention.Uid);
        Assert.Equal(0, mention.Pos);
        Assert.Equal(5, mention.Len);
        Assert.Equal("hôm nay khỏe không?", ZaloBotService.ExtractQuestion(recovered));
    }

    [Fact]
    public void Textual_prefix_for_another_member_does_not_address_bot()
    {
        var incoming = Incoming("@Nam hôm nay đánh không?", mentionedBot: false, mentions: []);

        var recovered = ZaloBotService.RecoverTextualBotMention(incoming, "Bott");

        Assert.False(recovered.MentionedBot);
        Assert.Empty(recovered.Mentions);
    }

    [Fact]
    public void Bridge_addressed_flag_is_enough_when_provider_omits_raw_span()
    {
        var incoming = Incoming("alo bot", mentionedBot: true, mentions: []);

        var recovered = ZaloBotService.RecoverTextualBotMention(incoming, "Bott");

        Assert.True(recovered.MentionedBot);
        var marker = Assert.Single(recovered.Mentions);
        Assert.Equal("bot-1", marker.Uid);
        Assert.Equal(-1, marker.Pos);
        Assert.Equal(0, marker.Len);
        Assert.Equal("alo bot", ZaloBotService.ExtractQuestion(recovered));
    }

    [Fact]
    public void Multiword_bot_name_is_recovered_without_consuming_question_text()
    {
        var incoming = Incoming("  @Volley Draft: cho hỏi vui cái", mentionedBot: false, mentions: []);

        var recovered = ZaloBotService.RecoverTextualBotMention(incoming, "Volley Draft");

        Assert.True(recovered.MentionedBot);
        Assert.Equal("cho hỏi vui cái", ZaloBotService.ExtractQuestion(recovered).TrimStart(':', ' '));
    }

    private static ZaloIncomingMessageEvent Incoming(
        string content,
        bool mentionedBot,
        IReadOnlyList<ZaloBridgeMention> mentions) =>
        new(
            accountId: "account-1",
            botId: "bot-1",
            groupId: "group-1",
            messageId: Guid.NewGuid().ToString("n"),
            senderId: "member-1",
            senderName: "Member",
            content: content,
            mentions: mentions,
            mentionedBot: mentionedBot,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
