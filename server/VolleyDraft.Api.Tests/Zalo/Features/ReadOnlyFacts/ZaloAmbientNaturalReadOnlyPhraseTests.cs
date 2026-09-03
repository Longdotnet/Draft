using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientNaturalReadOnlyPhraseTests
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
    [InlineData("team T6 hiện sao?", ZaloBotIntent.TeamLineup)]
    [InlineData("T6 chia đội ra sao rồi?", ZaloBotIntent.TeamLineup)]
    [InlineData("T6 còn ai chờ?", ZaloBotIntent.WaitlistStatus)]
    [InlineData("lịch nhắc T6 sao rồi?", ZaloBotIntent.ReminderStatus)]
    public void Natural_status_phrases_are_resolved_as_read_only_intents(
        string content,
        ZaloBotIntent expected)
    {
        Assert.True(ZaloAmbientReadOnlyNaturalIntentResolver.TryResolve(content, out var intent));
        Assert.Equal(expected, intent);

        var decision = Decide(Incoming(content), lastBotMessageAt: null);
        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(expected.ToString(), decision.Intent);
        Assert.True(decision.Score >= 60);
        Assert.Contains("natural_readonly_status", decision.Signals);
    }

    [Theory]
    [InlineData("chia team T6 đi?")]
    [InlineData("xếp team T6 được không?")]
    [InlineData("draft lại team T6?")]
    [InlineData("cân bằng team T6 đi?")]
    public void Mutation_language_never_downgrades_to_read_only_status(string content)
    {
        Assert.False(ZaloAmbientReadOnlyNaturalIntentResolver.TryResolve(content, out _));
    }

    [Fact]
    public void Natural_status_still_obeys_bot_cooldown_without_a_lease()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("team T6 hiện sao?"),
            Situation(now.AddMilliseconds(-500)),
            Settings,
            now,
            hasActiveLease: false);

        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.False(decision.WouldReply);
        Assert.Contains("bot_cooldown", decision.Signals);
    }

    [Fact]
    public void Active_lease_allows_same_sender_natural_status_followup_through_cooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("team T6 hiện sao?"),
            Situation(now.AddMilliseconds(-500)),
            Settings,
            now,
            hasActiveLease: true);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.TeamLineup.ToString(), decision.Intent);
        Assert.Contains("active_conversation_lease", decision.Signals);
    }

    [Fact]
    public async Task Natural_team_status_reaches_read_only_fact_responder_without_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"natural-readonly-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        };
        var zalo = new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc",
            EncryptedCredentials = "test"
        };
        var session = new MatchSession
        {
            Id = "session-t6",
            AdminUserId = admin.Id,
            ZaloConnectionId = zalo.Id,
            ZaloConnection = zalo,
            ZaloGroupId = "g1",
            Name = "T6",
            Status = SessionStatus.Setup,
            BotEnabled = true,
            StartTime = DateTimeOffset.UtcNow.AddDays(1),
            TeamCount = 3,
            TeamSize = 6
        };
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var incoming = Incoming("team T6 hiện sao?");
        var decision = Decide(incoming, lastBotMessageAt: null);
        var teamsBefore = await db.Teams.AsNoTracking().CountAsync();
        var slotsBefore = await db.DraftSlots.AsNoTracking().CountAsync();

        var reply = await new ZaloAmbientFactResponder(db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.TeamLineup, reply!.Intent);
        Assert.Contains("chưa có kết quả chia team", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(teamsBefore, await db.Teams.AsNoTracking().CountAsync());
        Assert.Equal(slotsBefore, await db.DraftSlots.AsNoTracking().CountAsync());
    }

    private static ZaloAmbientParticipationDecision Decide(
        ZaloIncomingMessageEvent incoming,
        DateTimeOffset? lastBotMessageAt) =>
        ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            Situation(lastBotMessageAt),
            Settings,
            DateTimeOffset.UtcNow);

    private static ZaloAmbientGroupSituation Situation(DateTimeOffset? lastBotMessageAt) => new(
        RecentMessageCount: 1,
        RecentTwoMinuteMessageCount: 1,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: lastBotMessageAt is null ? 0 : 1,
        LastBotMessageAt: lastBotMessageAt,
        RecentMessageIds: ["context-1"]);

    private static ZaloIncomingMessageEvent Incoming(string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: $"m-{Guid.NewGuid():n}",
        senderId: "user-long",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
