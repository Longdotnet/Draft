using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyTerminalAuthorityFuzzTests
{
    [Fact]
    public async Task Expired_legacy_worker_cannot_write_scheduler_status_with_slow_wall_clock()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 913003);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var slowClockSkew = TimeSpan.FromHours(-(2 + random.NextInt(5)));
            var legacyNow = databaseNow.Add(slowClockSkew);
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-terminal-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyNow, legacyNow.Add(leaseDuration));

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();
            await ExpireAuthorityAsync(db);

            var statusKind = random.NextInt(3);
            var affected = statusKind switch
            {
                0 => await ExecuteLegacyStatusWriteAsync(db, owner, legacyNow, "LastAttemptAt"),
                1 => await ExecuteLegacyStatusWriteAsync(db, owner, legacyNow, "LastSuccessAt"),
                _ => await ExecuteLegacyStatusWriteAsync(db, owner, legacyNow, "LastFailureAt")
            };

            Assert.Equal(
                0,
                affected);

            var persisted = await ReadStatusAsync(db, statusKind);
            Assert.Null(persisted);
        }
    }

    [Fact]
    public async Task Expired_legacy_failure_diagnostic_cannot_commit_after_authority_is_lost()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 977011);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var slowClockSkew = TimeSpan.FromHours(-(2 + random.NextInt(5)));
            var legacyNow = databaseNow.Add(slowClockSkew);
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-diagnostic-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyNow, legacyNow.Add(leaseDuration));

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();

            var failureAt = legacyNow.AddSeconds(1 + random.NextInt(10));
            var failureAtText = failureAt.ToUniversalTime().ToString("O");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ZaloSchedulerLeases"
                SET "LastFailureAt" = {{failureAtText}}
                WHERE "Name" = 'zalo-scheduler';
                """);

            // Reproduce the split old MarkFailureAsync sequence: authority can expire after the
            // lease-row update but before the diagnostic INSERT executes.
            await ExpireAuthorityAsync(db);
            var affected = await ExecuteLegacyFailureDiagnosticAsync(
                db,
                owner,
                failureAt,
                $"exception:{(seed % 2 == 0 ? "reminder" : "lifecycle")}");

            Assert.Equal(0, affected);
            Assert.Equal(0, await CountFailureDiagnosticsAsync(db));
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

    private static Task<int> ExecuteLegacyStatusWriteAsync(
        VolleyDraftDbContext db,
        string ownerId,
        DateTimeOffset at,
        string column)
    {
        var atText = at.ToUniversalTime().ToString("O");
        var sql = $"""
            UPDATE "ZaloSchedulerLeases"
            SET "{column}" = $at
            WHERE "Name" = 'zalo-scheduler'
              AND "OwnerId" = $owner
              AND "LeaseUntil" > $at;
            """;
        return db.Database.ExecuteSqlRawAsync(
            sql,
            new SqliteParameter("$at", atText),
            new SqliteParameter("$owner", ownerId));
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

    private static async Task<string?> ReadStatusAsync(VolleyDraftDbContext db, int statusKind)
    {
        var column = statusKind switch
        {
            0 => "LastAttemptAt",
            1 => "LastSuccessAt",
            _ => "LastFailureAt"
        };
        return await db.Database.SqlQueryRaw<string?>($"SELECT \"{column}\" AS \"Value\" FROM \"ZaloSchedulerLeases\" WHERE \"Name\" = 'zalo-scheduler'")
            .SingleAsync();
    }

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
