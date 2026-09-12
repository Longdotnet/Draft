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

    [Fact]
    public async Task Terminal_release_ends_database_authority_even_when_local_clocks_disagree()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 196613);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var acquiredLocalNow = new DateTimeOffset(2026, 9, 12, 17, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(60 + random.NextInt(121));
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-release-a-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-release-b-{seed}");

            await using (var acquireDb = new VolleyDraftDbContext(options))
            {
                Assert.True(await new ZaloSchedulerLeaseStore(acquireDb, useDatabaseAuthorityClock: true)
                    .TryAcquireAsync(predecessorOwner, acquiredLocalNow, leaseDuration));
            }

            // Make terminal timestamps hostile to the authority clock in both directions. A clean
            // terminal release is an explicit ownership handoff and must not leave a hidden future
            // authority deadline that blocks the next instance.
            var releaseSkewSeconds = random.NextBool()
                ? 3600 + random.NextInt(6 * 3600)
                : -(3600 + random.NextInt(6 * 3600));
            var releasedLocalAt = acquiredLocalNow.AddSeconds(releaseSkewSeconds);

            await using (var releaseDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(releaseDb, useDatabaseAuthorityClock: true);
                await store.MarkSuccessAsync(predecessorOwner, releasedLocalAt);
                Assert.True(
                    await store.ReleaseAsync(predecessorOwner, releasedLocalAt),
                    $"seed={seed} fingerprint=scheduler-lease:release-authority-stuck " +
                    $"releaseSkewSeconds={releaseSkewSeconds}");
            }

            var successorLocalNow = acquiredLocalNow.AddSeconds(
                random.NextBool() ? 8 * 3600 : -8 * 3600);
            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                Assert.True(
                    await new ZaloSchedulerLeaseStore(successorDb, useDatabaseAuthorityClock: true)
                        .TryAcquireAsync(successorOwner, successorLocalNow, leaseDuration),
                    $"seed={seed} fingerprint=scheduler-lease:release-authority-stuck successor");
            }

            await using var verifyDb = new VolleyDraftDbContext(options);
            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());
            Assert.Equal(successorOwner, snapshot.OwnerId);
            Assert.Equal(successorLocalNow, snapshot.LastAttemptAt);
        }
    }
}
