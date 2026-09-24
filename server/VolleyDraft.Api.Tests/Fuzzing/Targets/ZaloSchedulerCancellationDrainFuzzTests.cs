using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerCancellationDrainFuzzTests
{
    // Only a deadlock guard, never the correctness oracle: a loaded CI runner
    // can take longer than one second to schedule continuations for 2,000+ tests.
    private static readonly TimeSpan HarnessGuard = TimeSpan.FromSeconds(15);

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

            await stageStarted.Task.WaitAsync(HarnessGuard);
            cycleCancellation.Cancel();
            try
            {
                await cancellationObserved.Task.WaitAsync(HarnessGuard);

                // While cleanup is held behind a gate, even a heavily loaded runner must
                // never observe the wrapper returning. The short fuzz delay is only a
                // scheduling opportunity, not a timeout or a pass condition.
                await Task.Delay(10 + random.NextInt(20));
                Assert.False(run.IsCompleted,
                    "scheduler-cancellation:wrapper-returned-before-stage-drain");
            }
            finally
            {
                // Always release the stage, including when an assertion fails.
                releaseCleanup.TrySetResult();
            }

            // Await the real result with a generous deadlock guard. A one-second
            // WaitAsync can throw its own TimeoutException on busy Linux CI even
            // when the scheduler correctly propagates OperationCanceledException.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await run.WaitAsync(HarnessGuard));
        }
    }
}
