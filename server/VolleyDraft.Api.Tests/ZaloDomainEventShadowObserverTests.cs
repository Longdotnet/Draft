using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDomainEventShadowObserverTests
{
    [Fact]
    public async Task Filling_roster_after_poll_sync_emits_metadata_only_shadow_trace()
    {
        await using var fixture = await Fixture.CreateAsync("filled", teamSize: 2, presentPlayers: 1);
        var observer = new ZaloDomainEventShadowObserver(fixture.Db);
        var captured = await observer.CaptureAsync(fixture.SessionId);
        Assert.NotNull(captured);
        var before = captured!;

        fixture.Db.SessionPlayers.Add(Player("p2", fixture.SessionId, true));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await observer.ObserveAfterPollSyncAsync(
            before,
            actorZaloUserId: "actor-1",
            boardId: "poll-board-1",
            occurredAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.NotNull(result);
        Assert.Equal("RosterFilled", result.EventKind);
        Assert.Equal(1, result.BeforeCount);
        Assert.Equal(2, result.AfterCount);
        Assert.Equal(2, result.Capacity);

        var trace = await ReadSingleTraceAsync(fixture.Db);
        Assert.Equal("AmbientDomainEventShadow", trace.IntentSource);
        Assert.Equal("RosterFilled", trace.Intent);
        Assert.Equal(fixture.SessionId, trace.SessionId);
        Assert.Equal("actor-1", trace.SenderId);
        Assert.Equal("PollAuthoritativeStateChange", trace.AddressReason);
        Assert.False(trace.AiCalled);
        Assert.Equal("roster:1->2;capacity:2", trace.FallbackReason);
    }

    [Fact]
    public async Task Unchanged_roster_after_poll_sync_stays_silent_and_writes_no_trace()
    {
        await using var fixture = await Fixture.CreateAsync("unchanged", teamSize: 3, presentPlayers: 2);
        var observer = new ZaloDomainEventShadowObserver(fixture.Db);
        var captured = await observer.CaptureAsync(fixture.SessionId);
        Assert.NotNull(captured);
        var before = captured!;

        var result = await observer.ObserveAfterPollSyncAsync(
            before,
            actorZaloUserId: "actor-1",
            boardId: "poll-board-2",
            occurredAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(result);
        Assert.Equal(0, await TraceCountAsync(fixture.Db));
    }

    [Fact]
    public async Task Withdrawal_from_full_roster_is_classified_as_reopened()
    {
        await using var fixture = await Fixture.CreateAsync("reopened", teamSize: 2, presentPlayers: 2);
        var observer = new ZaloDomainEventShadowObserver(fixture.Db);
        var captured = await observer.CaptureAsync(fixture.SessionId);
        Assert.NotNull(captured);
        var before = captured!;

        var player = await fixture.Db.SessionPlayers.OrderBy(item => item.Id).FirstAsync();
        player.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await observer.ObserveAfterPollSyncAsync(
            before,
            actorZaloUserId: null,
            boardId: "poll-board-3",
            occurredAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.NotNull(result);
        Assert.Equal("RosterReopened", result.EventKind);
        Assert.Equal(2, result.BeforeCount);
        Assert.Equal(1, result.AfterCount);
    }

    private static SessionPlayer Player(string id, string sessionId, bool present) => new()
    {
        Id = id,
        SessionId = sessionId,
        DisplayName = id,
        IsPresent = present,
        IsCaptainEligible = true,
        Role = PlayerRole.Attack,
        Level = PlayerLevel.Average,
        Gender = PlayerGender.Unknown,
        Score = 2
    };

    private static async Task<int> TraceCountAsync(VolleyDraftDbContext db)
    {
        await new ZaloBotTraceStore(db).EnsureReadyAsync();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"ZaloBotTraces\";";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<(string IntentSource, string Intent, string SessionId, string SenderId, string AddressReason, bool AiCalled, string FallbackReason)> ReadSingleTraceAsync(VolleyDraftDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "IntentSource", "Intent", "ResolvedSessionId", "SenderZaloUserId",
                   "AddressReason", "AiCalled", "FallbackReason"
            FROM "ZaloBotTraces";
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetString(6));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db, string sessionId)
        {
            Connection = connection;
            Db = db;
            SessionId = sessionId;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public string SessionId { get; }

        public static async Task<Fixture> CreateAsync(string suffix, int teamSize, int presentPlayers)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var sessionId = $"domain-shadow-{suffix}";
            var session = new MatchSession
            {
                Id = sessionId,
                AdminUserId = "admin",
                Name = "T6",
                ZaloGroupId = "group-1",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                TeamCount = 1,
                TeamSize = teamSize
            };
            for (var index = 0; index < presentPlayers; index++)
                session.Players.Add(Player($"p{index + 1}", sessionId, true));
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db, sessionId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
