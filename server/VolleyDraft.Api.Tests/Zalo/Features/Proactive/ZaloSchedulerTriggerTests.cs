using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSchedulerTriggerTests
{
    [Fact]
    public void Repeated_triggers_coalesce_without_blocking_callers()
    {
        var trigger = new ZaloSchedulerTrigger();

        Assert.True(trigger.TryTrigger());
        Assert.True(trigger.TryTrigger());
        Assert.True(trigger.TryTrigger());
    }

    [Fact]
    public async Task Triggered_signal_wakes_waiter_before_watchdog()
    {
        var trigger = new ZaloSchedulerTrigger();
        var wait = trigger.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();

        Assert.True(trigger.TryTrigger());
        Assert.Equal(ZaloSchedulerWakeReason.ExternalTrigger, await wait.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Watchdog_wakes_waiter_when_external_tick_is_missing()
    {
        var trigger = new ZaloSchedulerTrigger();

        var reason = await trigger.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Equal(ZaloSchedulerWakeReason.Watchdog, reason);
    }

    [Fact]
    public void Lease_duration_is_not_shorter_than_watchdog_by_default()
    {
        var configuration = new ConfigurationBuilder().Build();
        var watchdog = ZaloSchedulerWorker.ResolveWatchdogInterval(configuration);

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(configuration, watchdog);

        Assert.True(lease >= watchdog);
        Assert.True(lease >= TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData("0.25")]
    [InlineData("1")]
    [InlineData("1.999")]
    public void Explicit_lease_duration_cannot_bypass_minimum_safety_floor(string configuredMinutes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:LeaseDurationMinutes"] = configuredMinutes
            })
            .Build();

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(configuration, TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(2), lease);
    }

    [Fact]
    public void Explicit_lease_duration_above_floor_is_preserved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:LeaseDurationMinutes"] = "3.5"
            })
            .Build();

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(configuration, TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(3.5), lease);
    }

    [Fact]
    public void Renewal_interval_is_one_third_of_lease()
    {
        var lease = TimeSpan.FromMinutes(15);

        Assert.Equal(TimeSpan.FromMinutes(5), ZaloSchedulerWorker.ResolveLeaseRenewalInterval(lease));
    }

    [Fact]
    public async Task RunWithLeaseHeartbeat_renews_while_stage_is_running()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
            async cancellationToken =>
            {
                await finishStage.Task.WaitAsync(cancellationToken);
                return 42;
            },
            _ =>
            {
                renewalObserved.TrySetResult();
                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(60),
            CancellationToken.None);

        await renewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(run.IsCompleted);

        finishStage.TrySetResult();
        Assert.Equal(42, await run.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RunWithLeaseHeartbeat_cancels_stage_and_fails_closed_when_renewal_loses_ownership()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stageCancelled.TrySetResult();
                    throw;
                }
            },
            async cancellationToken =>
            {
                renewalObserved.TrySetResult();
                await releaseRenewal.Task.WaitAsync(cancellationToken);
                return false;
            },
            TimeSpan.FromMilliseconds(60),
            CancellationToken.None);

        // Synchronize on the actual heartbeat instead of assuming a 20 ms timer callback
        // will run within a one-second wall-clock window under a fully parallel CI suite.
        await renewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        releaseRenewal.TrySetResult();

        await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
            await run.WaitAsync(TimeSpan.FromSeconds(1)));
        await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Durable_lease_blocks_second_instance_until_expiry_then_allows_failover()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(15)));
        Assert.False(await store.TryAcquireAsync("instance-b", now.AddMinutes(14), TimeSpan.FromMinutes(15)));
        Assert.True(await store.TryAcquireAsync("instance-b", now.AddMinutes(15), TimeSpan.FromMinutes(15)));

        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", lease.OwnerId);
        Assert.Equal(now.AddMinutes(30), lease.LeaseUntil);
    }

    [Fact]
    public async Task Terminal_release_allows_restarted_instance_to_run_without_waiting_for_old_lease_expiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var startedAt = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", startedAt, TimeSpan.FromMinutes(15)));
        await store.MarkAttemptAsync("instance-a", startedAt);
        var completedAt = startedAt.AddSeconds(20);
        await store.MarkSuccessAsync("instance-a", completedAt);
        Assert.True(await store.ReleaseAsync("instance-a", completedAt));

        Assert.True(await store.TryAcquireAsync("instance-b", completedAt.AddSeconds(1), TimeSpan.FromMinutes(15)));
        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", lease.OwnerId);
    }

    [Fact]
    public async Task Stale_owner_cannot_release_successor_lease()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryAcquireAsync("instance-b", now.AddMinutes(2), TimeSpan.FromMinutes(2)));

        Assert.False(await store.ReleaseAsync("instance-a", now.AddMinutes(2).AddSeconds(1)));
        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", lease.OwnerId);
        Assert.Equal(now.AddMinutes(4), lease.LeaseUntil);
    }

    [Fact]
    public async Task Same_owner_can_renew_lease_before_expiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryAcquireAsync("instance-a", now.AddMinutes(1), TimeSpan.FromMinutes(2)));

        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-a", lease.OwnerId);
        Assert.Equal(now.AddMinutes(3), lease.LeaseUntil);
    }

    [Fact]
    public void Stage_failures_prevent_successful_cycle_classification()
    {
        Assert.False(ZaloSchedulerWorker.HasStageFailures(0, 0, 0));
        Assert.True(ZaloSchedulerWorker.HasStageFailures(1, 0, 0));
        Assert.True(ZaloSchedulerWorker.HasStageFailures(0, 1, 0));
        Assert.True(ZaloSchedulerWorker.HasStageFailures(0, 0, 1));
    }
}
