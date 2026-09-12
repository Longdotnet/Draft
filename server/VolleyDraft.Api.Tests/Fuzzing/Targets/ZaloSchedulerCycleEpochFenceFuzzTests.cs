using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerCycleEpochFenceFuzzTests
{
    [Fact]
    public async Task Reacquired_cycle_must_reject_late_writes_from_previous_cycle_of_same_instance()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 262147);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var instanceId = $"scheduler-instance-{seed}";
            var firstOwner = ZaloSchedulerWorker.CreateCycleOwnerId(instanceId);
            var secondOwner = ZaloSchedulerWorker.CreateCycleOwnerId(instanceId);
            Assert.NotEqual(firstOwner, secondOwner);
            Assert.StartsWith(instanceId + ":", firstOwner, StringComparison.Ordinal);
            Assert.StartsWith(instanceId + ":", secondOwner, StringComparison.Ordinal);

            var firstAcquiredAt = new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(30 + random.NextInt(91));
            var firstReleasedAt = firstAcquiredAt.AddSeconds(1 + random.NextInt(5));
            var secondAcquiredAt = firstReleasedAt.AddMilliseconds(1 + random.NextInt(500));
            var secondLeaseUntil = secondAcquiredAt.Add(leaseDuration);
            var staleEventAt = firstAcquiredAt.AddMilliseconds(1 + random.NextInt(500));

            await using (var firstCycleDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(firstCycleDb);
                Assert.True(await store.TryAcquireAsync(firstOwner, firstAcquiredAt, leaseDuration));
                await store.MarkAttemptAsync(firstOwner, firstAcquiredAt.AddMilliseconds(1));
                Assert.True(await store.ReleaseAsync(firstOwner, firstReleasedAt));
            }

            await using (var secondCycleDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(secondCycleDb);
                Assert.True(await store.TryAcquireAsync(secondOwner, secondAcquiredAt, leaseDuration));
                await store.MarkAttemptAsync(secondOwner, secondAcquiredAt.AddMilliseconds(1));
            }

            // Model provider/DB continuations from cycle 1 arriving after the same worker instance
            // has already started cycle 2. A process-scoped owner id lets these stale writes match
            // the new lease again; a cycle-scoped epoch must fence every one of them.
            await using (var staleDb = new VolleyDraftDbContext(options))
            {
                var stale = new ZaloSchedulerLeaseStore(staleDb);
                Assert.False(await stale.TryRenewAsync(firstOwner, staleEventAt, leaseDuration));
                await stale.MarkSuccessAsync(firstOwner, staleEventAt.AddMilliseconds(1));
                await stale.MarkFailureAsync(firstOwner, staleEventAt.AddMilliseconds(2), "exception:reminder");
                Assert.False(await stale.ReleaseAsync(firstOwner, staleEventAt.AddMilliseconds(3)));
            }

            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());

                Assert.Equal(secondOwner, snapshot.OwnerId);
                Assert.Equal(secondLeaseUntil, snapshot.LeaseUntil);
                Assert.Equal(secondAcquiredAt.AddMilliseconds(1), snapshot.LastAttemptAt);
                Assert.Null(snapshot.LastSuccessAt);
                Assert.Null(snapshot.LastFailureAt);
                Assert.Null(snapshot.LastFailureCode);
            }
        }
    }
}
