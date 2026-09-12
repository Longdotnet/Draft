using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLegacyMigrationFuzzTests
{
    [Fact]
    public async Task Fast_legacy_clock_cannot_promote_its_absolute_deadline_into_new_authority()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 262147);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var skew = TimeSpan.FromHours(2 + random.NextInt(7));
            var legacyDuration = TimeSpan.FromMinutes(2 + random.NextInt(59));
            var legacyAttempt = databaseNow.Add(skew);
            var legacyLeaseUntil = legacyAttempt.Add(legacyDuration);
            await InsertLegacyRowAsync(db, $"legacy-fast-{seed}", legacyAttempt, legacyLeaseUntil);

            await new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true).EnsureAsync();

            var authorityLeaseUntil = await ReadAuthorityLeaseUntilAsync(db);
            var migrationObservedAt = await ReadDatabaseNowAsync(db);
            Assert.True(
                authorityLeaseUntil <= migrationObservedAt.AddHours(1).AddSeconds(2),
                $"seed={seed} fingerprint=scheduler-migration:legacy-fast-clock-promoted " +
                $"skewMinutes={skew.TotalMinutes:0} legacyDurationMinutes={legacyDuration.TotalMinutes:0} " +
                $"authorityLeaseUntil={authorityLeaseUntil:O} databaseNow={migrationObservedAt:O}");
            Assert.True(authorityLeaseUntil > migrationObservedAt);
        }
    }

    [Fact]
    public async Task Slow_legacy_clock_cannot_make_a_live_owner_immediately_stealable_during_upgrade()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 327673);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-slow-a-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"legacy-slow-b-{seed}");
            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var skew = TimeSpan.FromHours(-(2 + random.NextInt(7)));
            var legacyDuration = TimeSpan.FromMinutes(2 + random.NextInt(59));
            var legacyAttempt = databaseNow.Add(skew);
            var legacyLeaseUntil = legacyAttempt.Add(legacyDuration);
            Assert.True(legacyLeaseUntil < databaseNow);
            await InsertLegacyRowAsync(db, predecessorOwner, legacyAttempt, legacyLeaseUntil);

            var store = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
            await store.EnsureAsync();
            var acquired = await store.TryAcquireAsync(
                successorOwner,
                databaseNow.AddHours(8),
                TimeSpan.FromMinutes(15));

            Assert.False(
                acquired,
                $"seed={seed} fingerprint=scheduler-migration:legacy-slow-clock-early-takeover " +
                $"skewMinutes={skew.TotalMinutes:0} legacyDurationMinutes={legacyDuration.TotalMinutes:0}");
            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
            Assert.Equal(predecessorOwner, snapshot.OwnerId);
        }
    }

    [Fact]
    public async Task Missing_or_unusable_legacy_attempt_gets_bounded_conservative_migration_authority()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 393241);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var db = new VolleyDraftDbContext(options);
            await CreateLegacySchemaAsync(db);
            var databaseNow = await ReadDatabaseNowAsync(db);
            var legacyLeaseUntil = databaseNow.AddHours(random.NextBool() ? 12 : -12);
            await InsertLegacyRowAsync(db, $"legacy-missing-{seed}", null, legacyLeaseUntil);

            await new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true).EnsureAsync();

            var authorityLeaseUntil = await ReadAuthorityLeaseUntilAsync(db);
            var observedAt = await ReadDatabaseNowAsync(db);
            Assert.True(authorityLeaseUntil > observedAt);
            Assert.True(
                authorityLeaseUntil <= observedAt.AddHours(1).AddSeconds(2),
                $"seed={seed} fingerprint=scheduler-migration:legacy-missing-attempt-unbounded " +
                $"authorityLeaseUntil={authorityLeaseUntil:O} databaseNow={observedAt:O}");
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
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset leaseUntil)
    {
        var leaseUntilText = leaseUntil.ToUniversalTime().ToString("O");
        var lastAttemptAtText = lastAttemptAt?.ToUniversalTime().ToString("O");
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerLeases" ("Name", "OwnerId", "LeaseUntil", "LastAttemptAt")
            VALUES ("zalo-scheduler", {{ownerId}}, {{leaseUntilText}}, {{lastAttemptAtText}});
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
