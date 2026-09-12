using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerHealthRecoveryStateFuzzTests
{
    [Fact]
    public async Task Restarted_cycle_must_recover_health_without_resurrecting_previous_failure_authority()
    {
        const int seedCount = 96;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 32771);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var failedOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"health-recovery-{seed}");
            var recoveredOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"health-recovery-{seed}");
            var startedAt = new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(30 + random.NextInt(91));
            var failedAttemptAt = startedAt.AddMilliseconds(1 + random.NextInt(50));
            var failedAt = failedAttemptAt.AddMilliseconds(1 + random.NextInt(50));
            var releasedAt = failedAt.AddMilliseconds(1 + random.NextInt(50));
            var recoveredAt = releasedAt.AddMilliseconds(1 + random.NextInt(250));
            var recoveredAttemptAt = recoveredAt.AddMilliseconds(1 + random.NextInt(50));
            var recoveredSuccessAt = recoveredAttemptAt.AddMilliseconds(1 + random.NextInt(250));
            var staleAfter = TimeSpan.FromMinutes(10);

            await using (var failedCycleDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(failedCycleDb);
                Assert.True(await store.TryAcquireAsync(failedOwner, startedAt, leaseDuration));
                await store.MarkAttemptAsync(failedOwner, failedAttemptAt);
                await store.MarkFailureAsync(failedOwner, failedAt, "exception:reminder");
                Assert.True(await store.ReleaseAsync(failedOwner, releasedAt));
            }

            // Simulate process restart and a fresh durable ownership epoch. Historical failure
            // evidence may remain persisted, but it must not remain authoritative once a newer
            // cycle is actively running.
            await using (var restartedDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(restartedDb);
                Assert.True(await store.TryAcquireAsync(recoveredOwner, recoveredAt, leaseDuration));
                await store.MarkAttemptAsync(recoveredOwner, recoveredAttemptAt);

                var runningSnapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
                var running = ZaloSchedulerHealth.Evaluate(
                    runningSnapshot,
                    recoveredAttemptAt.AddMilliseconds(1),
                    staleAfter);

                Assert.Equal(ZaloSchedulerHealthState.Running, running.State);
                Assert.True(running.IsHealthy);
                Assert.Null(running.FailureCode);
                Assert.Equal(failedAt, runningSnapshot.LastFailureAt);
                Assert.Equal("exception:reminder", runningSnapshot.LastFailureCode);

                await store.MarkSuccessAsync(recoveredOwner, recoveredSuccessAt);
            }

            // Re-open again so the oracle is exercising persisted restart state, not tracked EF
            // entities. The newer success must dominate the prior failure deterministically.
            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());
                var assessment = ZaloSchedulerHealth.Evaluate(
                    snapshot,
                    recoveredSuccessAt.AddMilliseconds(1 + random.NextInt(500)),
                    staleAfter);

                Assert.Equal(recoveredOwner, snapshot.OwnerId);
                Assert.Equal(recoveredAttemptAt, snapshot.LastAttemptAt);
                Assert.Equal(recoveredSuccessAt, snapshot.LastSuccessAt);
                Assert.Equal(failedAt, snapshot.LastFailureAt);
                Assert.Equal("exception:reminder", snapshot.LastFailureCode);
                Assert.Equal(ZaloSchedulerHealthState.Healthy, assessment.State);
                Assert.True(assessment.IsHealthy);
                Assert.Null(assessment.FailureCode);
            }
        }
    }
}
