using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloKeepRecruitingAuthorityPolicyTests
{
    [Fact]
    public void KeepRecruiting_SupersedesPendingDraft_EvenWhenRosterIsOtherwiseReady()
    {
        var supersede = ZaloDraftPreparationDecisionPolicy.ShouldSupersedeActiveDraftRequest(
            ZaloDraftPreparationDecisionKind.KeepRecruiting,
            canEscalate: true,
            activeSlotRisks: 0);

        Assert.True(supersede);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void NonRecruitingDecision_StillSupersedesWhenReadinessBecomesUnsafe(
        bool canEscalate,
        int activeSlotRisks)
    {
        var supersede = ZaloDraftPreparationDecisionPolicy.ShouldSupersedeActiveDraftRequest(
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            canEscalate,
            activeSlotRisks);

        Assert.True(supersede);
    }

    [Fact]
    public void PlayCurrentRoster_DoesNotSupersedeSolelyBecauseRosterIsReady()
    {
        var supersede = ZaloDraftPreparationDecisionPolicy.ShouldSupersedeActiveDraftRequest(
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            canEscalate: true,
            activeSlotRisks: 0);

        Assert.False(supersede);
    }

    [Fact]
    public async Task KeepRecruiting_RemainsDurableAcrossFullRosterSnapshotAndRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new VolleyDraftDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var store = new ZaloDraftPreparationDecisionStore(db);
            await store.SetAsync(
                "session-full",
                ZaloDraftPreparationDecisionKind.KeepRecruiting,
                null,
                null,
                "leader-1",
                "Leader",
                "message-full");
        }

        await using var reloadedDb = new VolleyDraftDbContext(options);
        var reloaded = await new ZaloDraftPreparationDecisionStore(reloadedDb)
            .GetAsync("session-full");

        Assert.NotNull(reloaded);
        Assert.Equal(ZaloDraftPreparationDecisionKind.KeepRecruiting, reloaded!.Kind);
        Assert.True(reloaded.MatchesRoster(FullSnapshot()));
    }

    private static ZaloDraftReadinessSnapshot FullSnapshot() =>
        new(
            SessionId: "session-full",
            SessionName: "T4",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: DateTimeOffset.UtcNow.AddHours(4),
            PresentPlayerCount: 18,
            EffectiveSlotCount: 18,
            Capacity: 18,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: "fp-full",
            State: ZaloDraftReadinessState.Ready,
            ReasonCode: "draft_ready",
            IsRosterReady: true,
            CanEscalate: true);
}
