using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTurnQuoteContextTests
{
    [Fact]
    public async Task Pre_routing_quote_is_visible_to_context_assembler_after_await()
    {
        ZaloTurnQuoteContext.Clear();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var incoming = new ZaloIncomingMessageEvent(
            "bot-uid", "bot-uid", "g1", "m2", "u-long", "Long",
            "ông này có đăng ký T6 chưa?", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "m1", "u-tung", "Tùng", "Tui đánh T6", "chat",
                DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(), null));

        var preRoute = await new ZaloMemoryV2Service(db)
            .ProcessAsync("g1", incoming, incoming.Content);
        Assert.False(preRoute.Handled);

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("u-long", "Long"),
            incoming.Content,
            [],
            12);

        var quote = Assert.Single(assembled);
        Assert.Equal("context", quote.Role);
        Assert.Contains("[UNTRUSTED_ZALO_QUOTE]", quote.Content);
        Assert.Contains("QuotedSenderId=u-tung", quote.Content);
        Assert.Contains("RefersToQuotedPerson=yes", quote.Content);
        ZaloTurnQuoteContext.Clear();
    }

    [Fact]
    public void Quote_context_is_bound_to_sender_uid()
    {
        ZaloTurnQuoteContext.Clear();
        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m2", "u1", "Long", "cái đó", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("m1", "bot", "Bot", "T6 còn slot", "chat", null, null));
        ZaloTurnQuoteContext.Set(incoming);

        var otherUserContext = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("u2", "Tùng"), "cái đó", [], 12);

        Assert.Empty(otherUserContext);
        ZaloTurnQuoteContext.Clear();
    }

    [Fact]
    public void No_quote_replaces_previous_turn_context_with_empty_quote()
    {
        ZaloTurnQuoteContext.Clear();
        var quoted = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m1", "u1", "Long", "cái đó", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("q1", "bot", "Bot", "T6", "chat", null, null));
        ZaloTurnQuoteContext.Set(quoted);

        var plain = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m2", "u1", "Long", "hello", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ZaloTurnQuoteContext.Set(plain);

        Assert.Empty(ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("u1", "Long"), "hello", [], 12));
        ZaloTurnQuoteContext.Clear();
    }
}
