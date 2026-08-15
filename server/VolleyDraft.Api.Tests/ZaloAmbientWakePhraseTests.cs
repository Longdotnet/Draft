using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientWakePhraseTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 60,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 2,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Theory]
    [InlineData("Bot ơi bot")]
    [InlineData("bot ơi")]
    [InlineData("ê bot")]
    [InlineData("alo bot")]
    [InlineData("npc ơi")]
    [InlineData("bot đâu")]
    [InlineData("bot còn sống không")]
    public void Short_plain_text_calls_are_wake_phrases(string content)
    {
        Assert.True(ZaloAmbientWakePhrase.IsMatch(content));
    }

    [Theory]
    [InlineData("Nam ơi")]
    [InlineData("con bot này sài sao vậy mn")]
    [InlineData("Nam nói bot đánh dở")]
    [InlineData("bot T6 còn bao nhiêu slot?")]
    public void Ordinary_conversation_or_real_questions_are_not_reduced_to_ping(string content)
    {
        Assert.False(ZaloAmbientWakePhrase.IsMatch(content));
    }

    [Fact]
    public void Bot_oi_bot_without_native_mention_is_a_high_confidence_read_only_fact()
    {
        var now = DateTimeOffset.UtcNow;
        var incoming = Incoming("wake-1", "Bot ơi bot");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            Situation(lastBotMessageAt: now.AddSeconds(-1)),
            Settings,
            now);

        Assert.False(incoming.MentionedBot);
        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.Help.ToString(), decision.Intent);
        Assert.True(decision.Score >= 90);
        Assert.Contains("bot_plain_text_wake", decision.Signals);
    }

    [Fact]
    public async Task Wake_fact_responder_answers_without_sessions_or_domain_write()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var incoming = Incoming("wake-2", "Bot ơi bot");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            Situation(),
            Settings,
            DateTimeOffset.UtcNow);

        var reply = await new ZaloAmbientFactResponder(db).TryBuildAsync(
            "bot-account",
            "g1",
            incoming,
            decision,
            minimumScore: 60);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.Help, reply!.Intent);
        Assert.Contains("tui đây", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Long", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    private static ZaloIncomingMessageEvent Incoming(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static ZaloAmbientGroupSituation Situation(DateTimeOffset? lastBotMessageAt = null) => new(
        RecentMessageCount: 1,
        RecentTwoMinuteMessageCount: 1,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: lastBotMessageAt is null ? 0 : 1,
        LastBotMessageAt: lastBotMessageAt,
        RecentMessageIds: ["context-1"]);
}
