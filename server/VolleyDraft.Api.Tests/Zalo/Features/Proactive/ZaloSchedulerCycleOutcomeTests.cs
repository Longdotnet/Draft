using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSchedulerCycleOutcomeTests
{
    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 1, true)]
    [InlineData(2, 3, 4, true)]
    public void Failed_stage_work_prevents_scheduler_success(
        int reminderFailedCount,
        int rescueFailedCount,
        int lifecycleFailedCount,
        bool expectedFailure)
    {
        Assert.Equal(
            expectedFailure,
            ZaloSchedulerWorker.HasStageFailures(
                reminderFailedCount,
                rescueFailedCount,
                lifecycleFailedCount));
    }
}
