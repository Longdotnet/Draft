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
    [InlineData("KeepRecruiting")]
    [InlineData("StopMatch")]
    public async Task NonRosterBoundDecisions_SurviveRosterFingerprintChanges(string kindName)
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);
        var kind = Enum.Parse<ZaloDraftPreparationDecisionKind>(kindName);

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
    public async Task LatestLeaderDecision_ReplacesPreviousAndCanBeConditionallyCleared()
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

        Assert.True(await store.TryClearAsync("session-1", current));
        Assert.Null(await store.GetAsync("session-1"));
    }

    [Fact]
    public async Task ConditionalClear_DeletesOnlyTheDecisionThatWasObserved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);

        var observed = await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.KeepRecruiting,
            null,
            null,
            "leader-1",
            "Leader",
            "m1");

        var cleared = await store.TryClearAsync("session-1", observed);

        Assert.True(cleared);
        Assert.Null(await store.GetAsync("session-1"));
    }

    [Fact]
    public async Task ConditionalClear_DoesNotDeleteANewerConcurrentDecision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloDraftPreparationDecisionStore(fixture.Db);

        var staleObserved = await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.KeepRecruiting,
            null,
            null,
            "leader-1",
            "Leader",
            "m1");
        await Task.Delay(2);
        var newer = await store.SetAsync(
            "session-1",
            ZaloDraftPreparationDecisionKind.StopMatch,
            null,
            null,
            "deputy-1",
            "Deputy",
            "m2");

        var cleared = await store.TryClearAsync("session-1", staleObserved);
        var current = await store.GetAsync("session-1");

        Assert.False(cleared);
        Assert.NotNull(current);
        Assert.Equal(newer.SourceMessageId, current!.SourceMessageId);
        Assert.Equal(ZaloDraftPreparationDecisionKind.StopMatch, current.Kind);
    }

    [Fact]
    public void Store_DoesNotExposeUnconditionalClearApi()
    {
        Assert.DoesNotContain(
            typeof(ZaloDraftPreparationDecisionStore).GetMethods(),
            method => string.Equals(method.Name, "ClearAsync", StringComparison.Ordinal));
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
