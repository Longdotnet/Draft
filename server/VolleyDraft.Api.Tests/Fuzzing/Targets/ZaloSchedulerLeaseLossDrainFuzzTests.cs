using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLeaseLossDrainFuzzTests
{
    private static readonly TimeSpan HarnessGuard = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Rejected_lease_renewal_must_drain_stage_cleanup_before_returning_lease_loss()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            var leaseDuration = TimeSpan.FromMilliseconds(75 + random.NextInt(60));
            var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
                async stageToken =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, stageToken);
                        return seed;
                    }
                    catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
                    {
                        cancellationObserved.TrySetResult();
                        await releaseCleanup.Task;
                        throw;
                    }
                },
                _ =>
                {
                    renewalObserved.TrySetResult();
                    return Task.FromResult(false);
                },
                leaseDuration,
                CancellationToken.None);

            // These waits are deadlock guards only. Correctness is asserted below from event ordering
            // and the exact propagated result, so runner scheduling latency must not become the oracle.
            await renewalObserved.Task.WaitAsync(HarnessGuard);
            await cancellationObserved.Task.WaitAsync(HarnessGuard);

            // The wrapper owns the scoped stage lifetime. Losing the durable lease must cancel
            // authority immediately, but it must not return while domain/provider cleanup is still
            // using scoped services that the caller will dispose after this task completes.
            Assert.False(run.IsCompleted);

            releaseCleanup.TrySetResult();
            await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () => await run);
        }
    }

    [Fact]
    public async Task Renewal_exception_must_drain_stage_cleanup_before_propagating_failure()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 7919);
            var leaseDuration = TimeSpan.FromMilliseconds(75 + random.NextInt(60));
            var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException($"renewal-failure-{seed}");

            var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
                async stageToken =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, stageToken);
                        return seed;
                    }
                    catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
                    {
                        cancellationObserved.TrySetResult();
                        await releaseCleanup.Task;
                        throw;
                    }
                },
                _ =>
                {
                    renewalObserved.TrySetResult();
                    return Task.FromException<bool>(failure);
                },
                leaseDuration,
                CancellationToken.None);

            // These waits are deadlock guards only. The test still requires cancellation before
            // releaseCleanup and the identical renewal exception after cleanup drains.
            await renewalObserved.Task.WaitAsync(HarnessGuard);
            await cancellationObserved.Task.WaitAsync(HarnessGuard);
            Assert.False(run.IsCompleted);

            releaseCleanup.TrySetResult();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await run);
            Assert.Same(failure, actual);
        }
    }
}
