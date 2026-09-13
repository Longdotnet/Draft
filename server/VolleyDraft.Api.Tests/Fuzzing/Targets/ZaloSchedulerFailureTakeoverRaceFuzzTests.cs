using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerFailureTakeoverRaceFuzzTests
{
    [Fact]
    public async Task Predecessor_failure_diagnostic_cannot_commit_after_successor_takeover()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 1009003);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var wallClockSkew = TimeSpan.FromHours(-(1 + random.NextInt(5)));
            var predecessorNow = databaseNow.Add(wallClockSkew);
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"failure-predecessor-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"failure-successor-{seed}");
            await InsertLegacyRowAsync(db, predecessorOwner, predecessorNow, predecessorNow.Add(leaseDuration));

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();

            var predecessorFailureAt = predecessorNow.AddSeconds(1 + random.NextInt(20));
            var predecessorFailureAtText = predecessorFailureAt.ToUniversalTime().ToString("O");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ZaloSchedulerLeases"
                SET "LastFailureAt" = {{predecessorFailureAtText}}
                WHERE "Name" = 'zalo-scheduler'
                  AND "OwnerId" = {{predecessorOwner}};
                """);

            // Model the real split terminal sequence: the predecessor has persisted LastFailureAt,
            // loses DB-clock authority, and a successor acquires before the predecessor reaches its
            // diagnostic INSERT. The stale predecessor must not poison the successor epoch.
            await ExpireAuthorityAsync(db);
            var successorNow = databaseNow.AddSeconds(30 + random.NextInt(30));
            Assert.True(await store.TryAcquireAsync(successorOwner, successorNow, leaseDuration));

            var affected = await ExecuteLegacyFailureDiagnosticAsync(
                db,
                predecessorOwner,
                predecessorFailureAt,
                seed % 2 == 0 ? "exception:reminder" : "exception:lifecycle");

            Assert.True(
                affected == 0,
                $"seed={seed} fingerprint=scheduler-failure:predecessor-diagnostic-after-takeover " +
                $"skewHours={wallClockSkew.TotalHours:0} leaseMinutes={leaseDuration.TotalMinutes:0}");
            Assert.Equal(0, await CountFailureDiagnosticsAsync(db));

            var snapshot = await store.GetAsync();
            Assert.NotNull(snapshot);
            Assert.Equal(successorOwner, snapshot!.OwnerId);
            Assert.NotEqual(predecessorFailureAt, snapshot.LastAttemptAt);
            Assert.Null(snapshot.LastFailureCode);
        }
    }

    [Fact]
    public async Task Stale_predecessor_terminal_calls_cannot_mutate_successor_epoch()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 1017011);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"terminal-predecessor-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"terminal-successor-{seed}");
            var callerNow = new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero)
                .AddSeconds(random.NextInt(120));

            Assert.True(await store.TryAcquireAsync(predecessorOwner, callerNow, leaseDuration));
            await ExpireAuthorityAsync(db);
            Assert.True(await store.TryAcquireAsync(successorOwner, callerNow.AddMinutes(1), leaseDuration));
            var before = await store.GetAsync();
            Assert.NotNull(before);

            var staleAt = callerNow.AddMinutes(2);
            await store.MarkSuccessAsync(predecessorOwner, staleAt);
            await store.MarkFailureAsync(predecessorOwner, staleAt, "exception:finalize");
            Assert.False(await store.ReleaseAsync(predecessorOwner, staleAt));

            var after = await store.GetAsync();
            Assert.Equal(before, after);
        }
    }

    private static async Task CreateLegacySchemaAsync(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "ZaloSchedulerLeases" (
                "Name" TEXT PRIMARY KEY,
                "OwnerId" TEXT NOT NULL,
                "LeaseUntil" TEXT NOT NULL,
                "LastAttemptAt" TEXT NULL,
                "LastSuccessAt" TEXT NULL,
                "LastFailureAt" TEXT NULL
            );
            """);
    }

    private static Task InsertLegacyRowAsync(
        VolleyDraftDbContext db,
        string ownerId,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset leaseUntil)
    {
        var leaseUntilText = leaseUntil.ToUniversalTime().ToString("O");
        var lastAttemptAtText = lastAttemptAt.ToUniversalTime().ToString("O");
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerLeases" ("Name", "OwnerId", "LeaseUntil", "LastAttemptAt")
            VALUES ('zalo-scheduler', {{ownerId}}, {{leaseUntilText}}, {{lastAttemptAtText}});
            """);
    }

    private static Task<int> ExecuteLegacyFailureDiagnosticAsync(
        VolleyDraftDbContext db,
        string ownerId,
        DateTimeOffset failureAt,
        string failureCode)
    {
        var failureAtText = failureAt.ToUniversalTime().ToString("O");
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerFailureDiagnostics" ("Name", "FailureAt", "FailureCode")
            SELECT 'zalo-scheduler', {{failureAtText}}, {{failureCode}}
            WHERE EXISTS (
                SELECT 1
                FROM "ZaloSchedulerLeases"
                WHERE "Name" = 'zalo-scheduler'
                  AND "OwnerId" = {{ownerId}}
                  AND "LeaseUntil" > {{failureAtText}}
                  AND "LastFailureAt" = {{failureAtText}}
            )
            ON CONFLICT ("Name") DO UPDATE SET
                "FailureAt" = excluded."FailureAt",
                "FailureCode" = excluded."FailureCode";
            """);
    }

    private static Task ExpireAuthorityAsync(VolleyDraftDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ZaloSchedulerLeases"
            SET "AuthorityLeaseUntil" = strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now', '-1 second')
            WHERE "Name" = 'zalo-scheduler';
            """);

    private static async Task<int> CountFailureDiagnosticsAsync(VolleyDraftDbContext db) =>
        await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS \"Value\" FROM \"ZaloSchedulerFailureDiagnostics\"")
            .SingleAsync();

    private static async Task<DateTimeOffset> ReadDatabaseNowAsync(VolleyDraftDbContext db)
    {
        var value = await db.Database.SqlQueryRaw<string>(
                "SELECT strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now') AS \"Value\"")
            .SingleAsync();
        return DateTimeOffset.Parse(value);
    }
}
