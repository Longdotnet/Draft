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
    public async Task WaitAsync_returns_external_trigger_when_tick_is_available()
    {
        var trigger = new ZaloSchedulerTrigger();
        Assert.True(trigger.TryTrigger());

        var reason = await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ZaloSchedulerWakeReason.ExternalTrigger, reason);
    }

    [Fact]
    public async Task WaitAsync_returns_watchdog_when_external_tick_is_missing()
    {
        var trigger = new ZaloSchedulerTrigger();

        var reason = await trigger.WaitAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None);

        Assert.Equal(ZaloSchedulerWakeReason.Watchdog, reason);
    }

    [Fact]
    public async Task WaitAsync_propagates_host_shutdown_instead_of_reporting_watchdog()
    {
        var trigger = new ZaloSchedulerTrigger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await trigger.WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("0", 15)]
    [InlineData("-1", 15)]
    [InlineData("61", 15)]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    public void ResolveWatchdogInterval_bounds_configuration(string? configured, double expectedMinutes)
    {
        var values = configured is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                ["Scheduler:WatchdogIntervalMinutes"] = configured
            };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var interval = ZaloSchedulerWorker.ResolveWatchdogInterval(configuration);

        Assert.Equal(expectedMinutes, interval.TotalMinutes);
    }

    [Theory]
    [InlineData(15, 5)]
    [InlineData(60, 20)]
    public void ResolveLeaseRenewalInterval_renews_well_before_expiry(double leaseMinutes, double expectedMinutes)
    {
        var interval = ZaloSchedulerWorker.ResolveLeaseRenewalInterval(TimeSpan.FromMinutes(leaseMinutes));

        Assert.Equal(expectedMinutes, interval.TotalMinutes);
    }

    [Theory]
    [InlineData(1, 0, 0, "degraded:reminder")]
    [InlineData(0, 2, 0, "degraded:rescue")]
    [InlineData(0, 0, 3, "degraded:lifecycle")]
    [InlineData(1, 2, 3, "degraded:reminder+rescue+lifecycle")]
    public void Failure_codes_classify_degraded_stages_without_error_payloads(
        int reminderFailed,
        int rescueFailed,
        int lifecycleFailed,
        string expected)
    {
        var code = ZaloSchedulerFailureCodes.BuildDegraded(reminderFailed, rescueFailed, lifecycleFailed);

        Assert.Equal(expected, code);
        Assert.True(ZaloSchedulerFailureCodes.IsValid(code));
    }

    [Fact]
    public void Failure_codes_classify_exception_stage_without_exception_text()
    {
        var cases = new[]
        {
            (ZaloSchedulerStage.Listener, "exception:listener"),
            (ZaloSchedulerStage.Reminder, "exception:reminder"),
            (ZaloSchedulerStage.Rescue, "exception:rescue"),
            (ZaloSchedulerStage.Lifecycle, "exception:lifecycle"),
            (ZaloSchedulerStage.Finalize, "exception:finalize")
        };

        foreach (var (stage, expected) in cases)
        {
            var code = ZaloSchedulerFailureCodes.ForException(stage);
            Assert.Equal(expected, code);
            Assert.True(ZaloSchedulerFailureCodes.IsValid(code));
        }
    }

    [Fact]
    public async Task RunWithLeaseHeartbeat_renews_while_long_stage_is_still_running()
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
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(15)));
        await store.MarkAttemptAsync("instance-a", now.AddSeconds(1));
        await store.MarkSuccessAsync("instance-a", now.AddSeconds(2));
        Assert.True(await store.ReleaseAsync("instance-a", now.AddSeconds(2)));

        Assert.True(await store.TryAcquireAsync("instance-b", now.AddSeconds(3), TimeSpan.FromMinutes(15)));
        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", lease.OwnerId);
        Assert.Equal(now.AddMinutes(15).AddSeconds(3), lease.LeaseUntil);
        Assert.Equal(now.AddSeconds(2), lease.LastSuccessAt);
    }

    [Fact]
    public async Task Terminal_release_is_owner_guarded_and_cannot_clear_a_successor_lease()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryAcquireAsync("instance-b", now.AddMinutes(1), TimeSpan.FromMinutes(15)));
        Assert.False(await store.ReleaseAsync("instance-a", now.AddMinutes(1).AddSeconds(1)));

        var lease = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", lease.OwnerId);
        Assert.Equal(now.AddMinutes(16), lease.LeaseUntil);
    }

    [Fact]
    public async Task Current_owner_can_renew_without_opening_a_second_instance_window()
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
        Assert.True(await store.TryAcquireAsync("instance-a", now.AddMinutes(10), TimeSpan.FromMinutes(15)));
        Assert.False(await store.TryAcquireAsync("instance-b", now.AddMinutes(16), TimeSpan.FromMinutes(15)));
        Assert.True(await store.TryAcquireAsync("instance-b", now.AddMinutes(25), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task Scheduler_failure_diagnosis_survives_store_restart_and_stays_bound_to_exact_failure_timestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);
        var store = new ZaloSchedulerLeaseStore(db);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(15)));
        await store.MarkAttemptAsync("instance-a", now.AddSeconds(1));
        await store.MarkSuccessAsync("instance-a", now.AddSeconds(2));
        await store.MarkFailureAsync("instance-a", now.AddMinutes(1), "degraded:reminder+rescue");

        var restartedStore = new ZaloSchedulerLeaseStore(db);
        var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await restartedStore.GetAsync());
        Assert.Equal(now.AddSeconds(1), snapshot.LastAttemptAt);
        Assert.Equal(now.AddSeconds(2), snapshot.LastSuccessAt);
        Assert.Equal(now.AddMinutes(1), snapshot.LastFailureAt);
        Assert.Equal("degraded:reminder+rescue", snapshot.LastFailureCode);
    }

    [Fact]
    public async Task Stale_owner_cannot_clobber_successor_failure_diagnosis_after_lease_handoff()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloSchedulerLeaseStore(db);
        var now = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryAcquireAsync("instance-a", now, TimeSpan.FromMinutes(1)));
        await store.MarkFailureAsync("instance-a", now.AddSeconds(30), "degraded:reminder");

        Assert.True(await store.TryAcquireAsync("instance-b", now.AddMinutes(1), TimeSpan.FromMinutes(15)));
        var successorFailureAt = now.AddMinutes(1).AddSeconds(10);
        await store.MarkFailureAsync("instance-b", successorFailureAt, "degraded:rescue");

        await store.MarkFailureAsync("instance-a", now.AddMinutes(2), "degraded:lifecycle");

        var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(await store.GetAsync());
        Assert.Equal("instance-b", snapshot.OwnerId);
        Assert.Equal(successorFailureAt, snapshot.LastFailureAt);
        Assert.Equal("degraded:rescue", snapshot.LastFailureCode);
    }

    [Fact]
    public async Task MarkFailure_rejects_unbounded_or_free_form_diagnostic_text()
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
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MarkFailureAsync("instance-a", now.AddSeconds(1), "provider said API key=secret"));
    }
}
