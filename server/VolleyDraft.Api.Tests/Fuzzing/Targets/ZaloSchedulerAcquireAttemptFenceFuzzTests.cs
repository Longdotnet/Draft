using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerAcquireAttemptFenceFuzzTests
{
    [Fact]
    public async Task Successor_acquire_atomically_rebinds_attempt_authority_to_new_owner_epoch()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 104729);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var startedAt = new DateTimeOffset(2026, 9, 12, 13, 30, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var firstLease = TimeSpan.FromSeconds(20 + random.NextInt(41));
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"acquire-predecessor-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"acquire-successor-{seed}");
            var predecessorAttemptAt = startedAt.AddMilliseconds(1 + random.NextInt(500));

            await using (var predecessorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(predecessorDb);
                Assert.True(await store.TryAcquireAsync(predecessorOwner, startedAt, firstLease));
                await store.MarkAttemptAsync(predecessorOwner, predecessorAttemptAt);
            }

            // Simulate process death: the predecessor never records success/failure/release.
            // A fresh API instance takes over only after the durable lease expires.
            var successorAcquireAt = startedAt
                .Add(firstLease)
                .AddMilliseconds(random.NextInt(250));
            var successorLease = TimeSpan.FromSeconds(30 + random.NextInt(91));

            ZaloSchedulerLeaseSnapshot snapshot;
            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(successorDb);
                Assert.True(await store.TryAcquireAsync(successorOwner, successorAcquireAt, successorLease));
                snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
            }

            Assert.Equal(successorOwner, snapshot.OwnerId);
            Assert.Equal(successorAcquireAt, snapshot.LastAttemptAt);
            Assert.NotEqual(predecessorAttemptAt, snapshot.LastAttemptAt);

            // Lease ownership and attempt authority must move in the same SQL statement. Health may
            // legitimately report the successor as Running immediately after acquisition, but the
            // attempt timestamp must belong to that successor epoch rather than the dead predecessor.
            var observedAt = successorAcquireAt.AddMilliseconds(1 + random.NextInt(250));
            var assessment = ZaloSchedulerHealth.Evaluate(
                snapshot,
                observedAt,
                TimeSpan.FromMinutes(45));

            Assert.Equal(ZaloSchedulerHealthState.Running, assessment.State);
            Assert.True(assessment.IsHealthy);
            Assert.Equal(successorAcquireAt, assessment.LastAttemptAt);

            // A duplicate acquire by the same cycle owner is an idempotent lease refresh, not a new
            // logical scheduler attempt. It must not advance the attempt marker or create a new epoch.
            var duplicateAcquireAt = observedAt.AddMilliseconds(1 + random.NextInt(250));
            await using (var duplicateDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(duplicateDb);
                Assert.True(await store.TryAcquireAsync(successorOwner, duplicateAcquireAt, successorLease));
                var duplicateSnapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
                Assert.Equal(successorAcquireAt, duplicateSnapshot.LastAttemptAt);
                Assert.Equal(successorOwner, duplicateSnapshot.OwnerId);
            }
        }
    }
}
