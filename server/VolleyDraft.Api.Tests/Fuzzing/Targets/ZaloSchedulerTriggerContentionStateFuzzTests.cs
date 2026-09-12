using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerTriggerContentionStateFuzzTests
{
    [Fact]
    public async Task External_wake_bursts_must_coalesce_across_lease_contention_and_reacquisition()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 65537);
            var trigger = new ZaloSchedulerTrigger();
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var startedAt = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(30 + random.NextInt(91));
            var currentOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"trigger-current-{seed}");
            var retryOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"trigger-retry-{seed}");

            await using (var db = new VolleyDraftDbContext(options))
            {
                var lease = new ZaloSchedulerLeaseStore(db);
                Assert.True(await lease.TryAcquireAsync(currentOwner, startedAt, leaseDuration));
            }

            // Mutate duplicate delivery pressure before the worker consumes the wake. The bounded
            // trigger is intentionally a coalescing signal: many equivalent ticks represent one
            // logical request to run the durable scheduler state.
            var firstBurst = 1 + random.NextInt(12);
            for (var i = 0; i < firstBurst; i++)
                trigger.TryTrigger();

            Assert.Equal(
                ZaloSchedulerWakeReason.ExternalTrigger,
                await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
            trigger.Drain();

            // The attempted cycle observes durable lease contention. While its delayed retry is
            // pending, mutate another burst of webhook/scheduler ticks. Retry + newer ticks must
            // still collapse to one future cycle rather than disappear or multiply side effects.
            await using (var contendedDb = new VolleyDraftDbContext(options))
            {
                var lease = new ZaloSchedulerLeaseStore(contendedDb);
                Assert.False(await lease.TryAcquireAsync(
                    retryOwner,
                    startedAt.AddMilliseconds(1 + random.NextInt(100)),
                    leaseDuration));
            }

            var retryDelay = TimeSpan.FromMilliseconds(5 + random.NextInt(6));
            var delayedRetry = trigger.RequeueAfterAsync(retryDelay, CancellationToken.None);
            var secondBurst = 1 + random.NextInt(12);
            for (var i = 0; i < secondBurst; i++)
                trigger.TryTrigger();
            await delayedRetry;

            // Complete the authoritative owner before consuming the coalesced retry. The retried
            // cycle must then be able to acquire immediately using a fresh cycle ownership epoch.
            var releasedAt = startedAt.AddMilliseconds(200 + random.NextInt(100));
            await using (var releaseDb = new VolleyDraftDbContext(options))
            {
                var lease = new ZaloSchedulerLeaseStore(releaseDb);
                Assert.True(await lease.ReleaseAsync(currentOwner, releasedAt));
            }

            Assert.Equal(
                ZaloSchedulerWakeReason.ExternalTrigger,
                await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
            trigger.Drain();

            var reacquiredAt = releasedAt.AddMilliseconds(1 + random.NextInt(50));
            await using (var retryDb = new VolleyDraftDbContext(options))
            {
                var lease = new ZaloSchedulerLeaseStore(retryDb);
                Assert.True(await lease.TryAcquireAsync(retryOwner, reacquiredAt, leaseDuration));
                await lease.MarkAttemptAsync(retryOwner, reacquiredAt.AddMilliseconds(1));

                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await lease.GetAsync());
                Assert.Equal(retryOwner, snapshot.OwnerId);
                Assert.Equal(reacquiredAt.AddMilliseconds(1), snapshot.LastAttemptAt);
            }

            // No duplicate wake may survive the coalesced epoch. Otherwise one burst of equivalent
            // delivery could cause a second logical scheduler cycle after the recovered cycle.
            Assert.Equal(
                ZaloSchedulerWakeReason.Watchdog,
                await trigger.WaitAsync(TimeSpan.FromMilliseconds(5), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Cancelled_contention_retry_must_not_resurrect_a_wake_after_shutdown()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 8191);
            var trigger = new ZaloSchedulerTrigger();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                trigger.RequeueAfterAsync(
                    TimeSpan.FromMilliseconds(1 + random.NextInt(10)),
                    cancellation.Token));

            Assert.Equal(
                ZaloSchedulerWakeReason.Watchdog,
                await trigger.WaitAsync(TimeSpan.FromMilliseconds(5), CancellationToken.None));
        }
    }
}
