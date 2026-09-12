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
            var overrunGuard = TimeSpan.FromMilliseconds(120);
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

                var authorityDecision = await Task.WhenAny(
                        stageCancelled.Task,
                        Task.Delay(overrunGuard))
                    .WaitAsync(TimeSpan.FromSeconds(5));

                // The execution budget is authoritative even while a heartbeat renewal itself is
                // blocked. Before this regression, the inner renewal wait ignored the stage timeout,
                // keeping stage authority alive until the later renewal deadline.
                Assert.Same(stageCancelled.Task, authorityDecision);
                await renewalCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<TimeoutException>(async () =>
                    await run.WaitAsync(TimeSpan.FromSeconds(5)));
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
