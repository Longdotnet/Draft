using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerCancellationDrainFuzzTests
{
    [Fact]
    public async Task Cycle_cancellation_cannot_return_before_stage_cleanup_finishes()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            using var cycleCancellation = new CancellationTokenSource();
            var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
                async stageToken =>
                {
                    stageStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, stageToken);
                        return 1;
                    }
                    catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
                    {
                        cancellationObserved.TrySetResult();
                        // Model real provider/domain cleanup after cancellation. The scheduler
                        // must not dispose the owning DI scope while this cleanup is still running.
                        await releaseCleanup.Task;
                        throw;
                    }
                },
                _ => Task.FromResult(true),
                TimeSpan.FromMilliseconds(30 + random.NextInt(30)),
                cycleCancellation.Token);

            await stageStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cycleCancellation.Cancel();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // Give the heartbeat wrapper a deterministic opportunity to return incorrectly before
            // the cancelled stage has finished unwinding its scoped work.
            await Task.Delay(10 + random.NextInt(20));
            var returnedBeforeCleanup = run.IsCompleted;

            releaseCleanup.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.False(
                returnedBeforeCleanup,
                "scheduler-cancellation:wrapper-returned-before-stage-drain");
        }
    }
}
