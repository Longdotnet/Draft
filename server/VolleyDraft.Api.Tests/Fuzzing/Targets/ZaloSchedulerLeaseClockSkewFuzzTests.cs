using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLeaseClockSkewFuzzTests
{
    [Fact]
    public async Task Fast_successor_clock_cannot_take_over_before_predecessor_lease_expires_in_real_time()
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

            var realStartedAt = new DateTimeOffset(2026, 9, 12, 15, 30, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(40 + random.NextInt(81));
            var predecessorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-predecessor-{seed}");
            var successorOwner = ZaloSchedulerWorker.CreateCycleOwnerId($"clock-successor-{seed}");

            await using (var predecessorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(predecessorDb);
                Assert.True(await store.TryAcquireAsync(predecessorOwner, realStartedAt, leaseDuration));
            }

            // Real time is deliberately still inside the predecessor's lease. The competing API
            // instance has a fast wall clock, though, so its local `now` appears to be past the
            // durable LeaseUntil. A distributed lease must not allow that local skew to create two
            // simultaneously-authoritative scheduler owners.
            var realElapsed = TimeSpan.FromMilliseconds(
                leaseDuration.TotalMilliseconds * (0.55 + random.NextInt(31) / 100.0));
            var realNow = realStartedAt.Add(realElapsed);
            Assert.True(realNow < realStartedAt.Add(leaseDuration));

            var minimumSkewToCrossExpiry = realStartedAt.Add(leaseDuration) - realNow;
            var fastClockSkew = minimumSkewToCrossExpiry
                .Add(TimeSpan.FromMilliseconds(1 + random.NextInt(30_000)));
            var successorLocalNow = realNow.Add(fastClockSkew);

            bool acquired;
            await using (var successorDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(successorDb);
                acquired = await store.TryAcquireAsync(successorOwner, successorLocalNow, leaseDuration);
            }

            Assert.False(
                acquired,
                $"seed={seed} fingerprint=scheduler-lease:fast-clock-early-takeover " +
                $"realNow={realNow:O} successorLocalNow={successorLocalNow:O} " +
                $"skewMs={fastClockSkew.TotalMilliseconds:0.###}");

            await using var verifyDb = new VolleyDraftDbContext(options);
            var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());
            Assert.Equal(predecessorOwner, snapshot.OwnerId);
            Assert.Equal(realStartedAt, snapshot.LastAttemptAt);
        }
    }
}
