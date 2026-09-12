using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerStageTimeoutRenewalRaceFuzzTests
{
    [Fact]
    public async Task Stage_timeout_must_revoke_authority_while_renewal_is_stalled()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 262147);
            var leaseDuration = TimeSpan.FromMilliseconds(600 + random.NextInt(301));
            var renewalInterval = ZaloSchedulerWorker.ResolveLeaseRenewalInterval(leaseDuration);
            var stageTimeout = renewalInterval + TimeSpan.FromMilliseconds(35 + random.NextInt(16));
            using var stop = new CancellationTokenSource();
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
                stop.Token,
                stageTimeout);

            try
            {
                await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // The result type is the deterministic authority oracle. Before the production fix,
                // the in-flight renewal ignored the stage timeout until its later heartbeat deadline
                // and surfaced ZaloSchedulerLeaseLostException. The stage budget must win instead.
                await Assert.ThrowsAsync<TimeoutException>(async () =>
                    await run.WaitAsync(TimeSpan.FromSeconds(5)));

                // The wrapper must revoke both authorities before it reports that timeout. These are
                // event-order assertions rather than a sub-100ms wall-clock deadline so loaded CI
                // runners cannot manufacture a false fuzz finding from scheduler jitter.
                Assert.True(stageCancelled.Task.IsCompletedSuccessfully);
                Assert.True(renewalCancelled.Task.IsCompletedSuccessfully);
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
                    // The timeout decision owns the result; cleanup only prevents leaked work.
                }
            }
        }
    }
}
