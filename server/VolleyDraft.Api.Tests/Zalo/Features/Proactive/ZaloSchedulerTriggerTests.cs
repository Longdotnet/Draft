using Microsoft.Extensions.Configuration;
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
}
