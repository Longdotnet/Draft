using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyRenewalDurationFuzzTests
{
    [Fact]
    public async Task Live_legacy_renewal_cannot_inflate_database_authority_by_cycle_age()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 720007);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var skew = TimeSpan.FromHours(random.NextBool() ? 4 : -4);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var elapsedMinutes = 1 + random.NextInt(Math.Max(1, (int)leaseDuration.TotalMinutes - 1));
            var elapsed = TimeSpan.FromMinutes(elapsedMinutes);

            // The old process started this cycle before the rolling upgrade. Its local clock has a
            // stable offset, but the cycle itself is already elapsed. Migration intentionally grants
            // one fresh bounded DB-clock lease window; an immediate legacy renewal must preserve that
            // configured window, not add the cycle's age to it.
            var legacyNow = databaseNow.Add(skew);
            var legacyAttempt = legacyNow.Subtract(elapsed);
            var legacyLeaseUntil = legacyAttempt.Add(leaseDuration);
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-duration-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyAttempt, legacyLeaseUntil);

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();
            var authorityBefore = await ReadAuthorityLeaseUntilAsync(db);
            var beforeObservedAt = await ReadDatabaseNowAsync(db);
            Assert.True(authorityBefore <= beforeObservedAt.Add(leaseDuration).AddSeconds(2));

            var renewedLeaseUntil = legacyNow.Add(leaseDuration);
            var affected = await ExecuteLegacyRenewAsync(db, owner, legacyNow, renewedLeaseUntil);
            Assert.Equal(1, affected);

            var authorityAfter = await ReadAuthorityLeaseUntilAsync(db);
            var observedAt = await ReadDatabaseNowAsync(db);
            Assert.True(
                authorityAfter <= observedAt.Add(leaseDuration).AddSeconds(2),
                $"seed={seed} fingerprint=scheduler-migration:legacy-renewal-cycle-age-inflates-authority " +
                $"skewMinutes={skew.TotalMinutes:0} leaseMinutes={leaseDuration.TotalMinutes:0} " +
                $"elapsedMinutes={elapsed.TotalMinutes:0} authorityBefore={authorityBefore:O} " +
                $"authorityAfter={authorityAfter:O} databaseNow={observedAt:O}");
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

    private static Task<int> ExecuteLegacyRenewAsync(
        VolleyDraftDbContext db,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil)
    {
        var nowText = now.ToUniversalTime().ToString("O");
        var leaseUntilText = leaseUntil.ToUniversalTime().ToString("O");
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LeaseUntil" = {{leaseUntilText}}
            WHERE "Name" = 'zalo-scheduler'
              AND "OwnerId" = {{ownerId}}
              AND "LeaseUntil" > {{nowText}};
            """);
    }

    private static async Task<DateTimeOffset> ReadDatabaseNowAsync(VolleyDraftDbContext db)
    {
        var value = await db.Database.SqlQueryRaw<string>(
                "SELECT strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now') AS \"Value\"")
            .SingleAsync();
        return DateTimeOffset.Parse(value);
    }

    private static async Task<DateTimeOffset> ReadAuthorityLeaseUntilAsync(VolleyDraftDbContext db)
    {
        var value = await db.Database.SqlQueryRaw<string>(
                "SELECT \"AuthorityLeaseUntil\" AS \"Value\" FROM \"ZaloSchedulerLeases\" WHERE \"Name\" = 'zalo-scheduler'")
            .SingleAsync();
        return DateTimeOffset.Parse(value);
    }
}
