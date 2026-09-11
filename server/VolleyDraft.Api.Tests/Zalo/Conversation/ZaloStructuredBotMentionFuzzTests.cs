using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloStructuredBotMentionFuzzTests
{
    [Fact]
    public void Valid_structured_multiword_bot_mentions_strip_exactly_across_mutated_labels()
    {
        var labels = new[]
        {
            "@Npc",
            "@B O T",
            "@Volley Draft",
            "@BOT TEST",
            "@B   O   T",
            "@NPC Việt Nam",
            "@🤖 Volley Bot",
            "@Bot-Đánh-Banh"
        };
        var payloads = new[]
        {
            "xác nhận",
            "xác nhận nha",
            "xác nhận draft",
            "huỷ",
            "T6",
            "trận CN",
            "9",
            "10"
        };

        for (var seed = 1; seed <= 128; seed++)
        {
            var label = labels[seed % labels.Length];
            var payload = payloads[(seed * 7) % payloads.Length];
            var spacing = seed % 3 == 0 ? "   " : seed % 3 == 1 ? " " : "\n";
            var content = label + spacing + payload;
            var incoming = Incoming(content, new ZaloBridgeMention("bot-id", 0, label.Length));

            Assert.Equal(payload, ZaloBotService.ExtractQuestion(incoming));
        }
    }

    [Fact]
    public void Malformed_structured_bot_span_fails_closed_without_rewriting_raw_content()
    {
        const string content = "@B O T xác nhận";
        var incoming = Incoming(
            content,
            new ZaloBridgeMention("bot-id", 0, content.Length + 20));

        var question = ZaloBotService.ExtractQuestion(incoming);
        var marker = Assert.Single(incoming.Mentions);

        Assert.Equal(content, incoming.Content);
        Assert.Equal(0, marker.Pos);
        Assert.Equal(content.Length, marker.Len);
        Assert.Equal(string.Empty, question);
    }

    [Fact]
    public void Malformed_structured_span_corpus_never_invents_partial_user_intent()
    {
        var labels = new[] { "@B O T", "@Volley Draft", "@NPC Việt Nam", "@B   O   T" };
        var badLengths = new[] { -1, 0, 999 };

        for (var seed = 1; seed <= 128; seed++)
        {
            var label = labels[seed % labels.Length];
            var content = label + " xác nhận";
            var len = badLengths[(seed * 11) % badLengths.Length];
            var pos = seed % 4 == 0 ? content.Length + 1 : 0;
            var incoming = Incoming(content, new ZaloBridgeMention("bot-id", pos, len));

            var question = ZaloBotService.ExtractQuestion(incoming);

            Assert.Equal(content, incoming.Content);
            Assert.Equal(string.Empty, question);
        }
    }

    private static ZaloIncomingMessageEvent Incoming(string content, ZaloBridgeMention mention) =>
        new(
            "account-id",
            "bot-id",
            "group-id",
            Guid.NewGuid().ToString("n"),
            "sender-id",
            "Long",
            content,
            [mention],
            true,
            0);
}
