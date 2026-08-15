using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientFactResponderTests
{
    [Fact]
    public async Task Missing_slots_is_rendered_from_current_database_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SessionPlayers.AddRange(
            Player("p1", "session-t6", "Long"),
            Player("p2", "session-t6", "Nam"));
        await fixture.Db.SaveChangesAsync();

        var responder = new ZaloAmbientFactResponder(fixture.Db);
        var reply = await responder.TryBuildAsync(
            "bot-account",
            "g1",
            Incoming("T6 còn bao nhiêu slot?"),
            Decision(ZaloBotIntent.MissingSlots, 95),
            minimumScore: 85);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.MissingSlots, reply!.Intent);
        Assert.Equal("session-t6", reply.SessionId);
        Assert.Contains("2/6", reply.Text);
        Assert.Contains("còn thiếu 4 slot", reply.Text);
    }

    [Fact]
    public async Task Mutation_intent_can_never_enter_fact_responder_even_with_max_score()
    {
        await using var fixture = await Fixture.CreateAsync();
        var responder = new ZaloAmbientFactResponder(fixture.Db);

        var reply = await responder.TryBuildAsync(
            "bot-account",
            "g1",
            Incoming("draft lại T6 đi"),
            Decision(ZaloBotIntent.Redraft, 100),
            minimumScore: 85);

        Assert.Null(reply);
        Assert.False(ZaloAmbientFactResponder.IsAllowedIntent(ZaloBotIntent.Redraft));
        Assert.False(ZaloAmbientFactResponder.IsAllowedIntent(ZaloBotIntent.AutoDraft));
        Assert.False(ZaloAmbientFactResponder.IsAllowedIntent(ZaloBotIntent.WaitlistJoin));
    }

    [Fact]
    public async Task Ambiguous_session_reference_stays_silent_instead_of_guessing()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.MatchSessions.Add(Session(
            "session-t6-next",
            "T6",
            DateTimeOffset.UtcNow.AddDays(7)));
        await fixture.Db.SaveChangesAsync();

        var responder = new ZaloAmbientFactResponder(fixture.Db);
        var reply = await responder.TryBuildAsync(
            "bot-account",
            "g1",
            Incoming("T6 còn slot không?"),
            Decision(ZaloBotIntent.MissingSlots, 95),
            minimumScore: 85);

        Assert.Null(reply);
    }

    [Fact]
    public async Task Upcoming_sessions_lists_only_current_non_finished_sessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.MatchSessions.Add(Session(
            "session-cn",
            "CN",
            DateTimeOffset.UtcNow.AddDays(2),
            location: "Sân B"));
        fixture.Db.MatchSessions.Add(Session(
            "session-old",
            "Old",
            DateTimeOffset.UtcNow.AddDays(-3),
            status: SessionStatus.Finished));
        await fixture.Db.SaveChangesAsync();

        var responder = new ZaloAmbientFactResponder(fixture.Db);
        var reply = await responder.TryBuildAsync(
            "bot-account",
            "g1",
            Incoming("có kèo nào sắp tới?"),
            Decision(ZaloBotIntent.UpcomingSessions, 90),
            minimumScore: 85);

        Assert.NotNull(reply);
        Assert.Contains("T6", reply!.Text);
        Assert.Contains("CN", reply.Text);
        Assert.DoesNotContain("Old", reply.Text);
    }

    [Fact]
    public async Task Reply_below_pilot_minimum_score_stays_silent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var responder = new ZaloAmbientFactResponder(fixture.Db);

        var reply = await responder.TryBuildAsync(
            "bot-account",
            "g1",
            Incoming("T6 còn bao nhiêu slot?"),
            Decision(ZaloBotIntent.MissingSlots, 80),
            minimumScore: 85);

        Assert.Null(reply);
    }

    [Fact]
    public void Pilot_configuration_is_disabled_by_default_and_clamps_minimum_score()
    {
        var defaults = ZaloAmbientFactPilotSettings.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.False(defaults.Enabled);
        Assert.Equal(85, defaults.MinimumScore);

        var configured = ZaloAmbientFactPilotSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ZaloBot:Ambient:FactPilot:Enabled"] = "true",
                    ["ZaloBot:Ambient:FactPilot:MinimumScore"] = "10"
                })
                .Build());
        Assert.True(configured.Enabled);
        Assert.Equal(65, configured.MinimumScore);
    }

    private static MatchSession Session(
        string id,
        string name,
        DateTimeOffset? start,
        string? location = "Sân A",
        SessionStatus status = SessionStatus.Setup) => new()
    {
        Id = id,
        Name = name,
        AdminUserId = "admin-1",
        ZaloConnectionId = "conn-1",
        ZaloGroupId = "g1",
        BotEnabled = true,
        StartTime = start,
        Location = location,
        ParkingInstructions = "cổng bên phải",
        TeamCount = 1,
        TeamSize = 6,
        Status = status
    };

    private static SessionPlayer Player(string id, string sessionId, string name) => new()
    {
        Id = id,
        SessionId = sessionId,
        DisplayName = name,
        IsPresent = true
    };

    private static ZaloIncomingMessageEvent Incoming(string content) => new(
        accountId: "bot-account",
        botId: "bot-uid",
        groupId: "g1",
        messageId: Guid.NewGuid().ToString("n"),
        senderId: "u1",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static ZaloAmbientParticipationDecision Decision(ZaloBotIntent intent, int score) => new(
        WouldReply: true,
        Score: score,
        Kind: ZaloAmbientParticipationKind.Fact,
        Intent: intent.ToString(),
        IntentConfidence: .99,
        Signals: ["fact_intent", "question"],
        Situation: new ZaloAmbientGroupSituation(1, 1, 1, 0, null, ["m1"]));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"ambient-fact-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = "admin-1",
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            });
            db.MatchSessions.Add(Session(
                "session-t6",
                "T6",
                DateTimeOffset.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
