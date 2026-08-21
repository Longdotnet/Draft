using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationDecisionStoreTests
{
    [Fact]
    public async Task PlayCurrentRoster_BindsExactFingerprintAndSlotCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);

        var saved = await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            "fp-15",
            15,
            "leader-1",
            "Leader",
            "message-1");

        Assert.Equal(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, saved.Kind);
        Assert.Equal("fp-15", saved.RosterFingerprint);
        Assert.Equal(15, saved.EffectiveSlotCount);
        Assert.Equal("leader-1", saved.ActorZaloUserId);

        var same = Snapshot("fp-15", 15);
        var changedFingerprint = Snapshot("fp-16", 15);
        var changedSlots = Snapshot("fp-15", 16);
        Assert.True(saved.MatchesRoster(same));
        Assert.False(saved.MatchesRoster(changedFingerprint));
        Assert.False(saved.MatchesRoster(changedSlots));
    }

    [Theory]
    [InlineData(ZaloDraftPreparationDecisionKind.KeepRecruiting)]
    [InlineData(ZaloDraftPreparationDecisionKind.StopMatch)]
    public async Task NonRosterBoundDecisions_SurviveRosterFingerprintChanges(
        ZaloDraftPreparationDecisionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);

        var saved = await store.SetAsync(
            "session-1",
            kind,
            "should-be-cleared",
            15,
            "leader-1",
            "Leader",
            "message-1");

        Assert.Null(saved.RosterFingerprint);
        Assert.Null(saved.EffectiveSlotCount);
        Assert.True(saved.MatchesRoster(Snapshot("anything", 17)));
    }

    [Fact]
    public async Task LatestLeaderDecision_ReplacesPreviousAndCanBeCleared()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);

        await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.KeepRecruiting,
            null,
            null,
            "leader-1",
            "Leader",
            "m1");
        await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            "fp-15",
            15,
            "deputy-1",
            "Deputy",
            "m2");

        var current = await store.GetAsync("session-1");
        Assert.NotNull(current);
        Assert.Equal(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, current!.Kind);
        Assert.Equal("deputy-1", current.ActorZaloUserId);
        Assert.Equal("m2", current.SourceMessageId);

        await store.ClearAsync("session-1");
        Assert.Null(await store.GetAsync("session-1"));
    }

    private static ZaloDraftReadinessSnapshot Snapshot(string fingerprint, int slots) =>
        new(
            SessionId: "session-1",
            SessionName: "T4",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: DateTimeOffset.UtcNow.AddHours(4),
            PresentPlayerCount: slots,
            EffectiveSlotCount: slots,
            Capacity: 18,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: fingerprint,
            State: ZaloDraftReadinessState.RosterNotFull,
            ReasonCode: "draft_blocked_roster_not_full",
            IsRosterReady: false,
            CanEscalate: false);

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
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
