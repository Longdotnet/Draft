using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloIncomingIdentityEnricherTests
{
    [Fact]
    public void Deictic_person_quote_adds_metadata_only_uid_mention()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m2", "u-long", "Long",
            "@bot ông này có vote gần đây không?",
            [new ZaloBridgeMention("bot", 0, 4)],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("m1", "u-tung", "Tùng", "hello", "chat", null, null));

        var added = ZaloIncomingIdentityEnricher.TryAddQuotedPersonMention(incoming);

        Assert.True(added);
        var quotedMention = Assert.Single(incoming.Mentions, item => item.Uid == "u-tung");
        Assert.Equal(0, quotedMention.Len);
        Assert.Equal(-1, quotedMention.Pos);
    }

    [Fact]
    public void Object_quote_does_not_impersonate_quoted_sender_as_target_person()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m2", "u-long", "Long",
            "@bot cái đó đăng ký tui đi",
            [new ZaloBridgeMention("bot", 0, 4)],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("m1", "u-tung", "Tùng", "T6 còn 2 slot", "chat", null, null));

        Assert.False(ZaloIncomingIdentityEnricher.TryAddQuotedPersonMention(incoming));
        Assert.DoesNotContain(incoming.Mentions, item => item.Uid == "u-tung");
    }

    [Fact]
    public void Quoting_bot_never_adds_bot_as_target_person()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m2", "u-long", "Long",
            "ông này nói gì vậy?", [], false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("m1", "bot", "Bot", "T6 còn slot", "chat", null, null));

        Assert.False(ZaloIncomingIdentityEnricher.TryAddQuotedPersonMention(incoming));
        Assert.Single(incoming.Mentions); // constructor adds only the metadata bot-address marker
        Assert.Equal("bot", incoming.Mentions[0].Uid);
    }
}
