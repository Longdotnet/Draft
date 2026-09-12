using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerStageStallFuzzTests
{
    [Fact]
    public async Task Successful_heartbeats_must_not_keep_a_stalled_stage_authoritative_forever()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 196613);
            var leaseDuration = TimeSpan.FromMilliseconds(75 + random.NextInt(45));
            var renewalsBeforeBudget = 4 + random.NextInt(4);
            using var stop = new CancellationTokenSource();
            var stageCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalBudgetReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalCount = 0;

            var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
                async stageToken =>
                {
                    try
                    {
                        // Model a provider/domain stage that never makes forward progress but still
                        // observes cancellation correctly. Production evidence on 2026-09-12 showed
                        // exactly this externally: LastAttemptAt advanced, the lease kept extending,
                        // but no terminal success/failure appeared during the verifier window.
                        await Task.Delay(Timeout.InfiniteTimeSpan, stageToken);
                        return seed;
                    }
                    catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
                    {
                        stageCancelled.TrySetResult();
                        throw;
                    }
                },
                _ =>
                {
                    var count = Interlocked.Increment(ref renewalCount);
                    if (count >= renewalsBeforeBudget)
                        renewalBudgetReached.TrySetResult();
                    return Task.FromResult(true);
                },
                leaseDuration,
                stop.Token);

            try
            {
                await renewalBudgetReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Invariant: lease heartbeat is an ownership fence, not permission for a dead stage
                // to run forever. Once a bounded stage budget is exhausted, stage authority must be
                // revoked even while lease renewal itself is healthy.
                Assert.True(
                    stageCancelled.Task.IsCompleted,
                    $"scheduler-stage-stall:successful-heartbeats-outlive-stage-budget seed={seed} renewals={renewalCount}");
            }
            finally
            {
                stop.Cancel();
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The reproducer assertion above owns the finding. Cleanup only proves the
                    // stalled stage is cancellation-aware and does not leak work after the test.
                }
            }
        }
    }
}
