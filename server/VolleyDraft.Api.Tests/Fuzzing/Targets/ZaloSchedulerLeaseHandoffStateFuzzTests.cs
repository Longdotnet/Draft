using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLeaseHandoffStateFuzzTests
{
    [Fact]
    public async Task Stale_owner_terminal_writes_must_not_cross_successor_handoff()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 65537);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var now = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(random.NextInt(300));
            var leaseDuration = TimeSpan.FromSeconds(30 + random.NextInt(91));
            var ownerA = $"owner-a-{seed}";
            var ownerB = $"owner-b-{seed}";
            var ownerCAttacker = $"owner-c-{seed}";

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var firstStore = new ZaloSchedulerLeaseStore(firstDb);
                Assert.True(await firstStore.TryAcquireAsync(ownerA, now, leaseDuration));
                await firstStore.MarkAttemptAsync(ownerA, now.AddSeconds(1));

                var beforeExpiry = now.Add(leaseDuration).AddTicks(-1);
                Assert.False(await firstStore.TryAcquireAsync(ownerB, beforeExpiry, leaseDuration));
            }

            var takeoverAt = now.Add(leaseDuration).AddMilliseconds(random.NextInt(250));
            var successorAttempt = takeoverAt.AddMilliseconds(1 + random.NextInt(20));
            var successorSuccess = successorAttempt.AddMilliseconds(1 + random.NextInt(20));
            var successorFailure = successorSuccess.AddMilliseconds(1 + random.NextInt(20));
            var successorFailureCode = random.NextBool()
                ? "degraded:lifecycle"
                : "exception:finalize";

            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                var successorStore = new ZaloSchedulerLeaseStore(successorDb);
                Assert.True(await successorStore.TryAcquireAsync(ownerB, takeoverAt, leaseDuration));
                await successorStore.MarkAttemptAsync(ownerB, successorAttempt);
                await successorStore.MarkSuccessAsync(ownerB, successorSuccess);
                await successorStore.MarkFailureAsync(ownerB, successorFailure, successorFailureCode);
            }

            // Recreate the store/DbContext before stale writes to model a restart-shaped delayed
            // completion from the previous scheduler instance rather than process-local state.
            await using (var staleDb = new VolleyDraftDbContext(options))
            {
                var staleStore = new ZaloSchedulerLeaseStore(staleDb);
                await staleStore.MarkAttemptAsync(ownerA, successorFailure.AddSeconds(1));
                await staleStore.MarkSuccessAsync(ownerA, successorFailure.AddSeconds(2));
                await staleStore.MarkFailureAsync(ownerA, successorFailure.AddSeconds(3), "degraded:reminder");
                Assert.False(await staleStore.ReleaseAsync(ownerA, successorFailure.AddSeconds(4)));

                // A third worker still cannot enter while the successor's durable lease is live.
                Assert.False(await staleStore.TryAcquireAsync(
                    ownerCAttacker,
                    takeoverAt.Add(leaseDuration).AddTicks(-1),
                    leaseDuration));
            }

            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());

                Assert.Equal(ownerB, snapshot.OwnerId);
                Assert.Equal(takeoverAt.Add(leaseDuration), snapshot.LeaseUntil);
                Assert.Equal(successorAttempt, snapshot.LastAttemptAt);
                Assert.Equal(successorSuccess, snapshot.LastSuccessAt);
                Assert.Equal(successorFailure, snapshot.LastFailureAt);
                Assert.Equal(successorFailureCode, snapshot.LastFailureCode);
            }
        }
    }

    [Fact]
    public async Task Expired_owner_can_fail_over_once_and_old_diagnostics_must_not_resurrect()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 104729);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var now = new DateTimeOffset(2026, 9, 12, 1, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(20 + random.NextInt(80));
            var oldOwner = $"old-{seed}";
            var newOwner = $"new-{seed}";
            var oldFailureAt = now.AddSeconds(2);

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(firstDb);
                Assert.True(await store.TryAcquireAsync(oldOwner, now, leaseDuration));
                await store.MarkFailureAsync(oldOwner, oldFailureAt, "degraded:rescue");
            }

            var takeoverAt = now.Add(leaseDuration).AddMilliseconds(random.NextInt(100));
            var newFailureAt = takeoverAt.AddSeconds(1);

            await using (var secondDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(secondDb);
                Assert.True(await store.TryAcquireAsync(newOwner, takeoverAt, leaseDuration));
                await store.MarkFailureAsync(newOwner, newFailureAt, "degraded:lifecycle");

                // Delayed old-owner persistence must be fenced after handoff, even when it carries
                // a later timestamp that would otherwise look fresher to health diagnostics.
                await store.MarkFailureAsync(oldOwner, newFailureAt.AddMinutes(1), "exception:reminder");
            }

            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());

                Assert.Equal(newOwner, snapshot.OwnerId);
                Assert.Equal(newFailureAt, snapshot.LastFailureAt);
                Assert.Equal("degraded:lifecycle", snapshot.LastFailureCode);
                Assert.NotEqual(oldFailureAt, snapshot.LastFailureAt);
            }
        }
    }

    [Fact]
    public async Task Expired_heartbeat_must_not_resurrect_authority_before_successor_takeover()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 130363);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var acquiredAt = new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(15 + random.NextInt(90));
            var ownerA = $"renew-a-{seed}";
            var ownerB = $"renew-b-{seed}";
            var expiry = acquiredAt.Add(leaseDuration);
            var beforeExpiry = expiry.AddTicks(-(1 + random.NextInt(1000)));
            var afterExpiry = expiry.AddTicks(1 + random.NextInt(1000));

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(firstDb);
                Assert.True(await store.TryAcquireAsync(ownerA, acquiredAt, leaseDuration));

                // A heartbeat that still owns an unexpired lease may extend it. Reset to a fresh
                // lease afterwards so every seed also attacks the exact expiry boundary below.
                Assert.True(await store.TryRenewAsync(ownerA, beforeExpiry, leaseDuration));
                Assert.True(await store.ReleaseAsync(ownerA, acquiredAt));
                Assert.True(await store.TryAcquireAsync(ownerA, acquiredAt, leaseDuration));
            }

            await using (var lateHeartbeatDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(lateHeartbeatDb);

                // Reproducer for the previous bug: TryAcquireAsync doubled as renewal, so the same
                // owner could arrive after LeaseUntil and silently resurrect its authority before a
                // successor got a chance to claim the already-expired lease.
                Assert.False(await store.TryRenewAsync(ownerA, afterExpiry, leaseDuration));
            }

            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(successorDb);
                Assert.True(await store.TryAcquireAsync(ownerB, afterExpiry, leaseDuration));
                Assert.False(await store.TryRenewAsync(ownerA, afterExpiry.AddTicks(1), leaseDuration));
            }

            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());

                Assert.Equal(ownerB, snapshot.OwnerId);
                Assert.Equal(afterExpiry.Add(leaseDuration), snapshot.LeaseUntil);
            }
        }
    }
}
