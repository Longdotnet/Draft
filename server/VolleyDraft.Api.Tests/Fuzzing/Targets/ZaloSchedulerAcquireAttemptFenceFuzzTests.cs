using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerAcquireAttemptFenceFuzzTests
{
    [Fact]
    public async Task Successor_acquire_must_not_inherit_predecessor_unterminated_attempt_authority()
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
            Assert.Equal(predecessorAttemptAt, snapshot.LastAttemptAt);

            // This is the critical acquire -> MarkAttempt race. Durable ownership already belongs
            // to the successor, but the only persisted attempt evidence still belongs to the dead
            // predecessor. Health must not combine those two different ownership epochs and report
            // the successor as a live running attempt.
            var observedAt = successorAcquireAt.AddMilliseconds(1 + random.NextInt(250));
            var assessment = ZaloSchedulerHealth.Evaluate(
                snapshot,
                observedAt,
                TimeSpan.FromMinutes(45));

            Assert.False(
                assessment.State == ZaloSchedulerHealthState.Running,
                $"seed={seed} fingerprint=scheduler-acquire:successor-inherits-predecessor-attempt-authority " +
                $"predecessorAttempt={predecessorAttemptAt:O} successorAcquire={successorAcquireAt:O} " +
                $"leaseUntil={snapshot.LeaseUntil:O}");
        }
    }
}
