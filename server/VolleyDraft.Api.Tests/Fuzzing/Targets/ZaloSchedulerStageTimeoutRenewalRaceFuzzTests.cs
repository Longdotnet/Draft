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

                // Stage cleanup is drained before the wrapper reports timeout, while the renewal is
                // deliberately observed as a detached task after its authority token is revoked.
                // Await the cancellation event with a generous harness guard rather than assuming the
                // detached async continuation has already run on the exact thread that reports timeout.
                Assert.True(stageCancelled.Task.IsCompletedSuccessfully);
                await renewalCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
