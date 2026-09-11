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
            var renewalCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    async renewalToken =>
                    {
                        renewalStarted.TrySetResult();
                        using var registration = renewalToken.Register(
                            () => renewalCancellationObserved.TrySetResult());
                        // Model a DB/provider renewal call that stops making progress even after
                        // cancellation. The scheduler must revoke stage authority and signal the
                        // renewal before the previous durable lease can expire.
                        await releaseRenewal.Task;
                        return true;
                    },
                    leaseDuration,
                    CancellationToken.None);

                await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                await Assert.ThrowsAsync<ZaloSchedulerLeaseLostException>(async () =>
                    await run.WaitAsync(leaseDuration + TimeSpan.FromMilliseconds(250)));
                await stageCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await renewalCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally
            {
                releaseRenewal.TrySetResult();
            }
        }
    }
}
