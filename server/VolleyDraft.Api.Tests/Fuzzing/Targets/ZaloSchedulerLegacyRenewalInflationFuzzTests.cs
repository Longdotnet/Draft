using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyRenewalInflationFuzzTests
{
    [Fact]
    public async Task Legacy_same_owner_renewals_cannot_inflate_database_authority_beyond_the_lease_duration()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 655373);
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
            var legacyAttempt = databaseNow.Add(skew);
            var legacyLeaseUntil = legacyAttempt.Add(leaseDuration);
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-renew-inflation-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyAttempt, legacyLeaseUntil);

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();

            // A pre-authority worker renews with its own wall clock and does not update LastAttemptAt.
            // Mutate that clock forward while keeping the same logical lease duration. A shared-clock
            // bridge may move the authority deadline forward, but it must never reinterpret elapsed
            // time since the original attempt as a longer lease duration.
            var renewalCount = 2 + random.NextInt(4);
            var legacyNow = legacyAttempt;
            for (var renewal = 0; renewal < renewalCount; renewal++)
            {
                var stepMinutes = 1 + random.NextInt(Math.Max(1, (int)leaseDuration.TotalMinutes - 1));
                legacyNow = legacyNow.AddMinutes(stepMinutes);
                var renewedLeaseUntil = legacyNow.Add(leaseDuration);
                var affected = await ExecuteLegacyRenewAsync(db, owner, legacyNow, renewedLeaseUntil);
                Assert.Equal(1, affected);
            }

            var authorityAfter = await ReadAuthorityLeaseUntilAsync(db);
            var observedAt = await ReadDatabaseNowAsync(db);
            Assert.True(
                authorityAfter <= observedAt.Add(leaseDuration).AddSeconds(2),
                $"seed={seed} fingerprint=scheduler-migration:legacy-renewal-authority-inflation " +
                $"skewMinutes={skew.TotalMinutes:0} leaseMinutes={leaseDuration.TotalMinutes:0} " +
                $"renewals={renewalCount} authorityAfter={authorityAfter:O} databaseNow={observedAt:O}");
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
