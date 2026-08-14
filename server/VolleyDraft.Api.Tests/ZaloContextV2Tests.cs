using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloContextV2Tests
{
    [Fact]
    public void Quote_resolver_binds_ong_nay_to_quoted_sender_without_mutating_question()
    {
        var incoming = Incoming(
            "ông này có đăng ký T6 chưa?",
            new ZaloBridgeMessageQuote("m-parent", "u-tung", "Tùng", "Tui đánh T6", "chat", 1_786_000_000_000, null));

        var context = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);

        Assert.True(context.HasQuote);
        Assert.True(context.RefersToQuotedPerson);
        Assert.False(context.RepliesToBot);
        Assert.Equal("u-tung", context.SenderId);
        Assert.Equal("ông này có đăng ký T6 chưa?", incoming.Content);
    }

    [Fact]
    public void Quote_resolver_marks_cai_do_as_object_reference_and_reply_to_bot()
    {
        var incoming = Incoming(
            "cái đó đăng ký tui đi",
            new ZaloBridgeMessageQuote("bot-123", "bot-uid", "Volley Bot", "T6 còn 2 slot", "chat", 1_786_000_000_000, null));

        var context = ZaloQuotedContextResolver.Resolve(incoming);

        Assert.True(context.RefersToQuotedObject);
        Assert.True(context.RepliesToBot);
        Assert.Contains("QuotedMessageId=bot-123", ZaloQuotedContextResolver.BuildAiGrounding(context));
    }

    [Fact]
    public void Identity_resolver_prefers_quoted_person_for_deictic_reference()
    {
        var candidates = new[]
        {
            new ZaloIdentityCandidate("zalo:u-long", "u-long", "Long", "p-long", ["Tồ"]),
            new ZaloIdentityCandidate("zalo:u-tung", "u-tung", "Tùng", "p-tung", [])
        };
        var quote = new ZaloQuotedSemanticContext("m1", "u-tung", "Tùng", "hello", "chat", null, false, true, false);

        var result = ZaloIdentityResolver.ResolveCandidates("ông này", candidates, quotedContext: quote);

        Assert.Equal(ZaloIdentityResolutionStatus.Resolved, result.Status);
        Assert.Equal("u-tung", result.ZaloUserId);
        Assert.Equal("quoted_sender_uid", result.Source);
    }

    [Fact]
    public void Identity_resolver_returns_ambiguity_instead_of_guessing_duplicate_alias()
    {
        var candidates = new[]
        {
            new ZaloIdentityCandidate("zalo:u-1", "u-1", "Long Nguyễn", "p1", ["Long"]),
            new ZaloIdentityCandidate("zalo:u-2", "u-2", "Long Trần", "p2", ["Long"])
        };

        var result = ZaloIdentityResolver.ResolveCandidates("Long", candidates);

        Assert.Equal(ZaloIdentityResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData("xác nhận", "MissingSlots", 1.0, ZaloTopicSwitchDecision.ContinuePending)]
    [InlineData("huỷ", "MissingSlots", 1.0, ZaloTopicSwitchDecision.CancelPending)]
    [InlineData("T6 còn slot không?", "MissingSlots", .96, ZaloTopicSwitchDecision.SwitchToNewIntent)]
    [InlineData("ờ cái đó", null, 0.0, ZaloTopicSwitchDecision.ContinuePending)]
    public void Conversation_state_v2_has_central_topic_switch_rule(
        string question,
        string? freshIntent,
        double confidence,
        ZaloTopicSwitchDecision expected)
    {
        Assert.Equal(expected, ZaloConversationStateV2Store.DecideTopicSwitch(
            "AutoDraftConfirm", question, freshIntent, confidence));
    }

    [Fact]
    public async Task Conversation_state_v2_persists_structured_arguments_and_versions_updates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloConversationStateV2Store(db);

        var first = await store.SaveActiveAsync(
            "g1", "u1", "Register", "{\"session\":null}", "[\"session\"]", "[]", "m1", "m1",
            DateTimeOffset.UtcNow.AddMinutes(10));
        var second = await store.SaveActiveAsync(
            "g1", "u1", "Register", "{\"session\":\"T6\"}", "[]", "[]", "m1", "m2",
            DateTimeOffset.UtcNow.AddMinutes(10));
        var loaded = await store.LoadActiveAsync("g1", "u1");

        Assert.NotNull(loaded);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.StateVersion);
        Assert.Contains("T6", loaded!.CollectedArgumentsJson);
        Assert.Equal("m2", loaded.LastMessageId);
    }

    [Fact]
    public async Task Memory_v2_ingests_explicit_self_fact_before_routing_and_supports_forget()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new ZaloMemoryV2Service(db);

        var learned = await service.ProcessAsync("g1", Incoming("tui hay đánh T6"), "tui hay đánh T6");
        Assert.False(learned.Handled);
        Assert.NotNull(learned.RememberedConcept);

        var listed = await service.ProcessAsync("g1", Incoming("bot nhớ gì về tui?"), "bot nhớ gì về tui?");
        Assert.True(listed.Handled);
        Assert.Contains("T6", listed.Response);

        var forgotten = await service.ProcessAsync("g1", Incoming("bot quên lịch chơi của tui"), "bot quên lịch chơi của tui");
        Assert.True(forgotten.Handled);
        Assert.Contains("Đã quên", forgotten.Response);

        var empty = await service.ProcessAsync("g1", Incoming("bot nhớ gì về tui?"), "bot nhớ gì về tui?");
        Assert.Contains("không lưu", empty.Response);
    }

    [Fact]
    public async Task Message_graph_persists_quote_edge_and_real_outbound_provider_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloMessageGraphStore(db);
        var incoming = Incoming("T6", new ZaloBridgeMessageQuote("bot-real-1", "bot-uid", "Bot", "trận nào?", "chat", null, null));

        var relation = await store.RememberIncomingQuoteAsync("conn-1", incoming);
        var outbound = await store.RememberOutboundAsync("conn-1", "group-1", "bot-real-2", incoming.MessageId);

        Assert.NotNull(relation);
        Assert.Equal("bot-real-1", relation!.ToMessageId);
        Assert.Equal("bot-real-2", outbound.ProviderOutboundMessageId);
        Assert.Equal(incoming.MessageId, outbound.ToMessageId);
    }

    [Fact]
    public async Task Trace_store_keeps_routing_ids_without_raw_prompt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloBotTraceStore(db);

        var id = await store.WriteAsync(new ZaloBotTraceEntry(
            "m1", "g1", "u1", "ReplyToBot", "Deterministic", "MissingSlots", .99,
            "[\"m0\"]", "m0", "[]", "[\"zalo:u1\"]", "s1", AiCalled: false,
            TotalLatencyMs: 18, ReplyMessageId: "bot-real-9"));

        Assert.NotEmpty(id);
        Assert.Equal(0, await store.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1)));
    }

    private static ZaloIncomingMessageEvent Incoming(string content, ZaloBridgeMessageQuote? quote = null) =>
        new(
            "account-1",
            "bot-uid",
            "group-1",
            Guid.NewGuid().ToString("n"),
            "u-long",
            "Long",
            content,
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote);
}
