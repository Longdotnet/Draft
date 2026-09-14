using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceStaleUndoCrossFeatureFuzzTests
{
    private const string Fingerprint = "cross-feature:stale-team-preference-undo-resurrected-roster";

    [Fact]
    public async Task Roster_drift_and_restart_never_allow_stale_team_preference_undo_to_resurrect_member()
    {
        for (var seed = 1; seed <= 64; seed += 1)
            await RunSeedAsync(seed);
    }

    private static async Task RunSeedAsync(int seed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;

        VolleyDraftDbContext db = new(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User
            {
                Id = "admin",
                DisplayName = "Fuzz Admin",
                Email = $"team-pref-undo-{seed}@example.test",
                PasswordHash = "test"
            });
            await db.SaveChangesAsync();

            var service = new SessionDraftService(db);
            var createdSession = await service.CreateSessionAsync(
                "admin",
                new CreateSessionRequest("T6 stale preference undo", 3, 2));
            Assert.True(createdSession.IsSuccess, Describe(seed, createdSession.Error));
            var sessionId = createdSession.Value!.Id;

            var playerIds = new List<string>();
            foreach (var name in new[] { "Anchor", "Partner B", "Partner C" })
            {
                var added = await service.AddPlayerAsync(
                    "admin",
                    sessionId,
                    new AddPlayerRequest(
                        name,
                        PlayerRole.New,
                        PlayerLevel.New,
                        PlayerGender.Unknown));
                Assert.True(added.IsSuccess, Describe(seed, added.Error));
                playerIds.Add(added.Value!.Id);
            }

            var history = new ZaloBotActionHistoryService(
                db,
                NullLogger<ZaloBotActionHistoryService>.Instance);
            var before = await history.CaptureAsync(sessionId);
            var createdPreference = await service.CreateTeamPreferenceGroupAsync(
                "admin",
                sessionId,
                new CreateTeamPreferenceGroupRequest(playerIds));
            Assert.True(createdPreference.IsSuccess, Describe(seed, createdPreference.Error));

            var action = await history.RecordAsync(
                sessionId,
                "anchor-uid",
                "Anchor",
                "TeamPreference",
                "Anchor muốn chung team với Partner B / Partner C",
                before);
            Assert.NotNull(action);

            if ((seed & 1) != 0)
                db = await RestartAsync(db, options);

            var withdrawnId = playerIds[1 + seed % 2];
            var withdrawn = await db.SessionPlayers
                .AsNoTracking()
                .SingleAsync(player => player.Id == withdrawnId);
            service = new SessionDraftService(db);
            var updated = await service.UpdatePlayerAsync(
                "admin",
                sessionId,
                withdrawn.Id,
                new UpdatePlayerRequest(
                    withdrawn.DisplayName,
                    withdrawn.Role,
                    withdrawn.Level,
                    withdrawn.Gender,
                    false,
                    withdrawn.IsCaptainEligible));
            Assert.True(updated.IsSuccess, Describe(seed, updated.Error));

            if ((seed & 2) != 0)
                db = await RestartAsync(db, options);
            else
                db.ChangeTracker.Clear();

            await AssertRosterTruthAsync(db, sessionId, withdrawnId, seed, "before stale undo");

            history = new ZaloBotActionHistoryService(
                db,
                NullLogger<ZaloBotActionHistoryService>.Instance);
            var undone = await history.UndoAsync(
                "admin",
                sessionId,
                action!.Id,
                "anchor-uid");

            Assert.False(
                undone.IsSuccess,
                Describe(seed, $"{Fingerprint}: stale undo unexpectedly succeeded"));

            db.ChangeTracker.Clear();
            await AssertRosterTruthAsync(db, sessionId, withdrawnId, seed, "after stale undo");

            if (seed % 3 == 0)
            {
                var replay = await history.UndoAsync(
                    "admin",
                    sessionId,
                    action.Id,
                    "anchor-uid");
                Assert.False(
                    replay.IsSuccess,
                    Describe(seed, $"{Fingerprint}: duplicate stale undo unexpectedly succeeded"));
                db.ChangeTracker.Clear();
                await AssertRosterTruthAsync(db, sessionId, withdrawnId, seed, "after duplicate stale undo");
            }
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    private static async Task AssertRosterTruthAsync(
        VolleyDraftDbContext db,
        string sessionId,
        string withdrawnId,
        int seed,
        string phase)
    {
        var stored = await db.SessionPlayers
            .AsNoTracking()
            .SingleAsync(player => player.Id == withdrawnId);
        Assert.False(
            stored.IsPresent,
            Describe(seed, $"{Fingerprint}: withdrawn player resurrected {phase}"));

        var staleLink = await db.TeamPreferenceGroupPlayers
            .AsNoTracking()
            .AnyAsync(link =>
                link.SessionPlayerId == withdrawnId &&
                link.TeamPreferenceGroup.SessionId == sessionId);
        Assert.False(
            staleLink,
            Describe(seed, $"{Fingerprint}: withdrawn player remained in TeamPreference {phase}"));
    }

    private static async Task<VolleyDraftDbContext> RestartAsync(
        VolleyDraftDbContext current,
        DbContextOptions<VolleyDraftDbContext> options)
    {
        await current.DisposeAsync();
        return new VolleyDraftDbContext(options);
    }

    private static string Describe(int seed, string? detail) =>
        $"seed={seed}; fingerprint={Fingerprint}; detail={detail ?? "none"}";
}
