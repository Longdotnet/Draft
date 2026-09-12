using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyRenewalInflationFuzzTests
{
    [Fact]
    public async Task Expired_database_authority_cannot_be_resurrected_by_a_late_legacy_same_owner_renewal()
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
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-late-renew-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyAttempt, legacyLeaseUntil);

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();

            // Deterministically advance only the authoritative lease epoch to an expired state.
            // This models a legacy worker that was paused past the DB-clock deadline while its skewed
            // local wall clock still considers the old LeaseUntil live. New workers are forbidden to
            // TryRenew after this boundary and legacy compatibility must preserve that rule.
            await ExpireAuthorityAsync(db);
            var expiredAuthority = await ReadAuthorityLeaseUntilAsync(db);
            var expirationObservedAt = await ReadDatabaseNowAsync(db);
            Assert.True(expiredAuthority <= expirationObservedAt);

            var legacyNow = legacyLeaseUntil.AddMinutes(-1);
            var renewedLeaseUntil = legacyNow.Add(leaseDuration);
            var affected = await ExecuteLegacyRenewAsync(db, owner, legacyNow, renewedLeaseUntil);

            var authorityAfter = await ReadAuthorityLeaseUntilAsync(db);
            var observedAt = await ReadDatabaseNowAsync(db);
            Assert.Equal(
                0,
                affected);
            Assert.True(
                authorityAfter <= observedAt,
                $"seed={seed} fingerprint=scheduler-migration:expired-legacy-renewal-resurrected-authority " +
                $"skewMinutes={skew.TotalMinutes:0} leaseMinutes={leaseDuration.TotalMinutes:0} " +
                $"expiredAuthority={expiredAuthority:O} authorityAfter={authorityAfter:O} databaseNow={observedAt:O}");

            var successor = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-late-renew-successor-{seed}");
            Assert.True(
                await store.TryAcquireAsync(successor, databaseNow, leaseDuration),
                $"seed={seed} fingerprint=scheduler-migration:expired-legacy-renewal-blocked-successor");
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

    private static Task ExpireAuthorityAsync(VolleyDraftDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ZaloSchedulerLeases"
            SET "AuthorityLeaseUntil" = strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now', '-1 second')
            WHERE "Name" = 'zalo-scheduler';
            """);

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
