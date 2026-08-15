using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientParticipationEngineTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: true,
        WouldReplyThreshold: 65,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 20,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public void Untagged_fact_question_is_a_shadow_reply_candidate()
    {
        var incoming = Incoming("m1", "T6 còn bao nhiêu slot?");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.True(decision.Score >= Settings.WouldReplyThreshold);
        Assert.Contains("fact_intent", decision.Signals);
    }

    [Fact]
    public void Emoji_or_acknowledgement_never_wakes_ambient_participant()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m2", "haha 😂"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.False(decision.WouldReply);
        Assert.Contains("ack_or_emoji_only", decision.Signals);
    }

    [Fact]
    public void Untagged_mutation_is_observed_but_never_authorized_as_ambient_action()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m3", "draft lại team T6 đi"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.Equal(ZaloAmbientParticipationKind.Action, decision.Kind);
        Assert.False(decision.WouldReply);
        Assert.Contains("action_requires_address", decision.Signals);
    }

    [Fact]
    public void Recent_bot_turn_hard_suppresses_even_a_strong_fact_candidate()
    {
        var now = DateTimeOffset.UtcNow;
        var quiet = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m4", "T6 còn bao nhiêu slot?"),
            QuietSituation(),
            Settings,
            now);
        var cooldown = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m4", "T6 còn bao nhiêu slot?"),
            QuietSituation(lastBotMessageAt: now.AddSeconds(-5)),
            Settings,
            now);

        Assert.True(quiet.WouldReply);
        Assert.False(cooldown.WouldReply);
        Assert.True(cooldown.Score < quiet.Score);
        Assert.Contains("bot_cooldown", cooldown.Signals);
    }

    [Fact]
    public void Reply_to_another_member_hard_suppresses_even_a_strong_fact_candidate()
    {
        var quote = new ZaloBridgeMessageQuote(
            "human-message-1",
            "user-2",
            "Nam",
            "T6 hình như đông rồi",
            "chat",
            DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
            null);
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m5", "T6 còn bao nhiêu slot?", quote: quote),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.False(decision.WouldReply);
        Assert.Contains("reply_to_member", decision.Signals);
    }

    [Fact]
    public void Explicit_address_never_uses_ambient_participation()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("m6", "T6 còn bao nhiêu slot?", mentionedBot: true),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.False(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.None, decision.Kind);
        Assert.Contains("explicit_address_uses_normal_router", decision.Signals);
    }

    [Fact]
    public async Task Observer_persists_one_message_and_one_idempotent_shadow_trace_without_reply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "ambient@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-uid",
            DisplayName = "Volley Bot",
            EncryptedCredentials = "test"
        });
        await db.SaveChangesAsync();

        var incoming = Incoming("ambient-1", "T6 còn bao nhiêu slot?");
        var observer = new ZaloAmbientObserver(db);
        var first = await observer.ObserveAsync("conn-1", incoming, Settings);
        var second = await observer.ObserveAsync("conn-1", incoming, Settings);

        Assert.True(first.WouldReply);
        Assert.Equal(first.Score, second.Score);
        var stored = Assert.Single(await db.ZaloGroupMessages
            .Where(item => item.ZaloConnectionId == "conn-1" && item.MessageId == "ambient-1")
            .ToListAsync());
        Assert.Equal("AmbientShadow", stored.ObservationSource);
        Assert.Null(stored.ReplyOutcome);
        Assert.Null(stored.BotReplySentAt);
        Assert.False(stored.AiCalled);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), MAX("AddressReason"), MAX("IntentSource"), MAX("ReplyMessageId"), MAX("FallbackReason")
            FROM "ZaloBotTraces"
            WHERE "MessageId"='ambient-1' AND "IntentSource"='AmbientShadow';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("AmbientShadowWouldReply", reader.GetString(1));
        Assert.Equal("AmbientShadow", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Contains("kind:Fact", reader.GetString(4));
    }

    private static ZaloIncomingMessageEvent Incoming(
        string messageId,
        string content,
        bool mentionedBot = false,
        ZaloBridgeMessageQuote? quote = null) => new(
        accountId: "bot-account",
        botId: "bot-uid",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-1",
        senderName: "Long",
        content: content,
        mentions: mentionedBot ? [new ZaloBridgeMention("bot-uid", 0, 4)] : [],
        mentionedBot: mentionedBot,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        quote: quote);

    private static ZaloAmbientGroupSituation QuietSituation(DateTimeOffset? lastBotMessageAt = null) => new(
        RecentMessageCount: 1,
        RecentTwoMinuteMessageCount: 1,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: lastBotMessageAt is null ? 0 : 1,
        LastBotMessageAt: lastBotMessageAt,
        RecentMessageIds: ["context-1"]);
}
