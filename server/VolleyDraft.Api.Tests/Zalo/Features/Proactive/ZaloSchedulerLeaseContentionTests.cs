using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSchedulerLeaseContentionTests
{
    [Fact]
    public async Task RunPendingCycle_preserves_wake_until_stale_restart_lease_expires()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var leaseStartedAt = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync(
            "old-instance",
            leaseStartedAt,
            TimeSpan.FromMinutes(15)));

        var attempts = 0;
        await ZaloSchedulerWorker.RunPendingCycleAsync(
            async cancellationToken =>
            {
                attempts++;
                var observedAt = attempts == 1
                    ? leaseStartedAt.AddMinutes(14)
                    : leaseStartedAt.AddMinutes(15);
                return await store.TryAcquireAsync(
                    "restarted-instance",
                    observedAt,
                    TimeSpan.FromMinutes(15),
                    cancellationToken);
            },
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(2, attempts);
        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("restarted-instance", lease.OwnerId);
        Assert.Equal(leaseStartedAt.AddMinutes(30), lease.LeaseUntil);
    }

    [Fact]
    public async Task RunPendingCycle_does_not_repeat_a_wake_after_cycle_acquires_ownership()
    {
        var attempts = 0;

        await ZaloSchedulerWorker.RunPendingCycleAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(1, attempts);
    }
}
