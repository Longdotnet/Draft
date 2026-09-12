using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLeaseClockSkewFuzzTests
{
    [Fact]
    public async Task Fast_successor_clock_cannot_take_over_before_database_authority_lease_expires()
    {
        const int seedCount = 128;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 130363);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var predecessorLocalNow = new DateTimeOffset(2026, 9, 12, 15, 30, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(40 + random.NextInt(81));
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-predecessor-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-successor-{seed}");

            await using (var predecessorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(predecessorDb, useDatabaseAuthorityClock: true);
                Assert.True(await store.TryAcquireAsync(predecessorOwner, predecessorLocalNow, leaseDuration));
            }

            await using var baselineDb = new VolleyDraftDbContext(options);
            var baseline = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                await new ZaloSchedulerLeaseStore(baselineDb).GetAsync());
            Assert.Equal(predecessorOwner, baseline.OwnerId);
            Assert.Equal(predecessorLocalNow, baseline.LastAttemptAt);

            // Mutate only the competing API instance's wall clock. The caller-visible timestamp is
            // deliberately well beyond the predecessor's observable LeaseUntil, but no comparable
            // amount of database-authority time has elapsed. Local skew must therefore have zero
            // power to advance the distributed ownership epoch.
            var fastClockSkew = leaseDuration
                .Add(TimeSpan.FromSeconds(1 + random.NextInt(600)));
            var successorLocalNow = predecessorLocalNow.Add(fastClockSkew);
            Assert.True(successorLocalNow > baseline.LeaseUntil);

            bool acquired;
            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(successorDb, useDatabaseAuthorityClock: true);
                acquired = await store.TryAcquireAsync(successorOwner, successorLocalNow, leaseDuration);
            }

            Assert.False(
                acquired,
                $"seed={seed} fingerprint=scheduler-lease:fast-clock-early-takeover " +
                $"predecessorLocalNow={predecessorLocalNow:O} successorLocalNow={successorLocalNow:O} " +
                $"skewMs={fastClockSkew.TotalMilliseconds:0.###}");

            await using var verifyDb = new VolleyDraftDbContext(options);
            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());
            Assert.Equal(predecessorOwner, snapshot.OwnerId);
            Assert.Equal(predecessorLocalNow, snapshot.LastAttemptAt);
            Assert.Equal(baseline.LeaseUntil, snapshot.LeaseUntil);
        }
    }

    [Fact]
    public async Task Slow_or_fast_owner_clock_cannot_revoke_its_live_database_authority_renewal()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 170141);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var acquiredLocalNow = new DateTimeOffset(2026, 9, 12, 16, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(45 + random.NextInt(76));
            var owner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-renew-{seed}");

            await using (var acquireDb = new VolleyDraftDbContext(options))
            {
                Assert.True(await new ZaloSchedulerLeaseStore(acquireDb, useDatabaseAuthorityClock: true)
                    .TryAcquireAsync(owner, acquiredLocalNow, leaseDuration));
            }

            var signedSkewSeconds = random.NextBool()
                ? 3600 + random.NextInt(6 * 3600)
                : -(3600 + random.NextInt(6 * 3600));
            var renewalLocalNow = acquiredLocalNow.AddSeconds(signedSkewSeconds);

            await using (var renewDb = new VolleyDraftDbContext(options))
            {
                Assert.True(
                    await new ZaloSchedulerLeaseStore(renewDb, useDatabaseAuthorityClock: true)
                        .TryRenewAsync(owner, renewalLocalNow, leaseDuration),
                    $"seed={seed} fingerprint=scheduler-lease:local-clock-renewal-authority " +
                    $"signedSkewSeconds={signedSkewSeconds}");
            }

            await using var verifyDb = new VolleyDraftDbContext(options);
            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());
            Assert.Equal(owner, snapshot.OwnerId);
            Assert.Equal(acquiredLocalNow, snapshot.LastAttemptAt);
            Assert.Equal(renewalLocalNow.Add(leaseDuration), snapshot.LeaseUntil);
        }
    }
}
