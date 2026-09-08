using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSchedulerContentionRetryTests
{
    [Fact]
    public async Task RequeueAfterAsync_preserves_external_wake_after_transient_lease_contention()
    {
        var trigger = new ZaloSchedulerTrigger();

        await trigger.RequeueAfterAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        var reason = await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(ZaloSchedulerWakeReason.ExternalTrigger, reason);
    }

    [Fact]
    public async Task RequeueAfterAsync_coalesces_with_newer_tick_instead_of_creating_duplicate_cycles()
    {
        var trigger = new ZaloSchedulerTrigger();
        Assert.True(trigger.TryTrigger());

        await trigger.RequeueAfterAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Equal(
            ZaloSchedulerWakeReason.ExternalTrigger,
            await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(
            ZaloSchedulerWakeReason.Watchdog,
            await trigger.WaitAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None));
    }

    [Fact]
    public async Task RequeueAfterAsync_does_not_leave_retry_behind_after_host_shutdown()
    {
        var trigger = new ZaloSchedulerTrigger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await trigger.RequeueAfterAsync(TimeSpan.FromSeconds(1), cancellation.Token));

        Assert.Equal(
            ZaloSchedulerWakeReason.Watchdog,
            await trigger.WaitAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None));
    }
}
