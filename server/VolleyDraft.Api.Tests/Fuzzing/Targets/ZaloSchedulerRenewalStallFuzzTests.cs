using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerRenewalStallFuzzTests
{
    [Fact]
    public async Task Stalled_lease_renewal_must_cancel_stage_without_waiting_for_stalled_call()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            var leaseDuration = TimeSpan.FromMilliseconds(90 + random.NextInt(45));
            var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stageCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var run = ZaloSchedulerWorker.RunWithLeaseHeartbeatAsync(
                    async stageToken =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, stageToken);
                            return 1;
                        }
                        catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
                        {
                            stageCancelled.TrySetResult();
                            throw;
                        }
                    },
                    async _ =>
                    {
                        renewalStarted.TrySetResult();
                        // Model a DB/provider renewal call that stops making progress and ignores
                        // cancellation. The stage must lose authority without waiting for this call
                        // to return. Use event ordering as the oracle instead of a sub-second wall
                        // clock deadline so loaded CI runners cannot create false fuzz findings.
                        await releaseRenewal.Task;
                        return true;
                    },
                    leaseDuration,
                    CancellationToken.None);

                await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(releaseRenewal.Task.IsCompleted);

                await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
                    await run.WaitAsync(TimeSpan.FromSeconds(1)));
            }
            finally
            {
                releaseRenewal.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Timed_out_lease_renewal_must_revoke_the_inflight_renewal_token()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 104729);
            var leaseDuration = TimeSpan.FromMilliseconds(90 + random.NextInt(45));
            var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stageCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                        stageCancelled.TrySetResult();
                        throw;
                    }
                },
                async renewalToken =>
                {
                    renewalStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, renewalToken);
                        return true;
                    }
                    catch (OperationCanceledException) when (renewalToken.IsCancellationRequested)
                    {
                        renewalCancelled.TrySetResult();
                        throw;
                    }
                },
                leaseDuration,
                CancellationToken.None);

            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // The safety oracle is semantic: the renewal authority and stage authority must both be
            // revoked by the heartbeat timeout. A generous harness timeout detects hangs without
            // coupling correctness to GitHub runner scheduling jitter at ~100 ms resolution.
            await renewalCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(1)));
        }
    }
}
