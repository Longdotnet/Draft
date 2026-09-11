using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerRenewalStallFuzzTests
{
    [Fact]
    public async Task Stalled_lease_renewal_must_cancel_stage_before_ownership_can_expire()
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
                        // cancellation. The stage must not keep authoritative mutation rights until
                        // this call eventually returns because the durable lease can expire first.
                        await releaseRenewal.Task;
                        return true;
                    },
                    leaseDuration,
                    CancellationToken.None);

                await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
                    await run.WaitAsync(leaseDuration + TimeSpan.FromMilliseconds(250)));
                await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
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
            await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
                await run.WaitAsync(leaseDuration + TimeSpan.FromMilliseconds(250)));
            await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // Once the heartbeat deadline is crossed, both the stage and the in-flight renewal lose
            // authority. Otherwise a cancellation-aware DB/provider operation can still commit a
            // late renewal after the wrapper has already returned lease loss.
            await renewalCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }
}
