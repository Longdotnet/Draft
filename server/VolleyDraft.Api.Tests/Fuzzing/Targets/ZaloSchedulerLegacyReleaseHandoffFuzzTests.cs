using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyReleaseHandoffFuzzTests
{
    [Fact]
    public async Task Legacy_owner_can_release_its_live_migrated_authority_without_waiting_for_expiry()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 589823);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var skewHours = random.NextBool() ? 2 + random.NextInt(7) : -(2 + random.NextInt(7));
            var legacyAttempt = databaseNow.AddHours(skewHours);
            var leaseDuration = TimeSpan.FromMinutes(5 + random.NextInt(26));
            var legacyLeaseUntil = legacyAttempt.Add(leaseDuration);
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-release-{seed}");
            await InsertLegacyRowAsync(db, owner, legacyAttempt, legacyLeaseUntil);

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();
            var authorityBefore = await ReadAuthorityLeaseUntilAsync(db);
            Assert.True(authorityBefore > await ReadDatabaseNowAsync(db));

            // Model the exact pre-authority ReleaseAsync shape. The old worker is still the
            // authoritative owner and finishes inside its own local lease window.
            var elapsedMinutes = 1 + random.NextInt(Math.Max(1, (int)leaseDuration.TotalMinutes - 1));
            var oldReleaseAt = legacyAttempt.AddMinutes(elapsedMinutes);
            var affected = await ExecuteLegacyReleaseAsync(db, owner, oldReleaseAt, seed);

            Assert.Equal(
                1,
                affected);

            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
            Assert.StartsWith("released:", snapshot.OwnerId, StringComparison.Ordinal);

            var authorityAfter = await ReadAuthorityLeaseUntilAsync(db);
            var observedAt = await ReadDatabaseNowAsync(db);
            Assert.True(
                authorityAfter <= observedAt.AddSeconds(2),
                $"seed={seed} fingerprint=scheduler-migration:legacy-release-authority-stuck " +
                $"skewHours={skewHours} authorityBefore={authorityBefore:O} authorityAfter={authorityAfter:O} databaseNow={observedAt:O}");

            var successor = ZaloSchedulerWorker.CreateCycleOwnerId($"post-release-{seed}");
            Assert.True(
                await store.TryAcquireAsync(successor, databaseNow.AddHours(-skewHours), TimeSpan.FromMinutes(15)),
                $"seed={seed} fingerprint=scheduler-migration:legacy-release-successor-blocked");
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

    private static Task<int> ExecuteLegacyReleaseAsync(
        VolleyDraftDbContext db,
        string ownerId,
        DateTimeOffset at,
        int seed)
    {
        var atText = at.ToUniversalTime().ToString("O");
        var releasedOwnerId = $"released:legacy-{seed}";
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "OwnerId" = {{releasedOwnerId}},
                "LeaseUntil" = {{atText}}
            WHERE "Name" = 'zalo-scheduler'
              AND "OwnerId" = {{ownerId}}
              AND "LeaseUntil" > {{atText}};
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
