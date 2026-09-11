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
    public void Malformed_structured_bot_span_must_never_partially_strip_a_multiword_bot_label()
    {
        const string content = "@B O T xác nhận";
        var incoming = Incoming(
            content,
            new ZaloBridgeMention("bot-id", 0, content.Length + 20));

        var question = ZaloBotService.ExtractQuestion(incoming);

        Assert.True(
            question == content || question == "xác nhận",
            $"mention-normalization:partial-bot-label-strip; extracted='{question}'");
    }

    [Fact]
    public void Malformed_structured_span_corpus_never_leaves_a_suffix_of_the_bot_label_as_user_intent()
    {
        var labels = new[] { "@B O T", "@Volley Draft", "@NPC Việt Nam", "@B   O   T" };
        var badLengths = new[] { -1, 0, 1, 2, 999 };

        for (var seed = 1; seed <= 128; seed++)
        {
            var label = labels[seed % labels.Length];
            var content = label + " xác nhận";
            var len = badLengths[(seed * 11) % badLengths.Length];
            var pos = seed % 4 == 0 ? content.Length + 1 : 0;
            var incoming = Incoming(content, new ZaloBridgeMention("bot-id", pos, len));

            var question = ZaloBotService.ExtractQuestion(incoming);
            var normalized = ZaloBotIntelligence.Normalize(question);

            Assert.DoesNotContain("o t xac nhan", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("volley draft xac nhan", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("npc viet nam xac nhan", normalized, StringComparison.Ordinal);
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
