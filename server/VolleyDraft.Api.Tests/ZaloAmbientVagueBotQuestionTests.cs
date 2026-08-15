using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientVagueBotQuestionTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 60,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 2,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public async Task Vague_feasibility_question_addressed_to_bot_asks_for_clarification_instead_of_silence()
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "vague-1",
            senderId: "user-long",
            senderName: "Thanh Long",
            content: "Làm được không Bot",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var situation = new ZaloAmbientGroupSituation(
            RecentMessageCount: 1,
            RecentTwoMinuteMessageCount: 1,
            DistinctParticipantCount: 1,
            RecentBotMessageCount: 0,
            LastBotMessageAt: null,
            RecentMessageIds: ["vague-1"]);
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            situation,
            Settings,
            DateTimeOffset.UtcNow);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.Help.ToString(), decision.Intent);
        Assert.Contains("bot_feasibility_clarification", decision.Signals);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var reply = await new ZaloAmbientFactResponder(db).TryBuildAsync(
            "bot-account",
            "g1",
            incoming,
            decision,
            minimumScore: 60);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.Help, reply!.Intent);
        Assert.Contains("được gì", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xếp team", reply.Text, StringComparison.OrdinalIgnoreCase);
    }
}
