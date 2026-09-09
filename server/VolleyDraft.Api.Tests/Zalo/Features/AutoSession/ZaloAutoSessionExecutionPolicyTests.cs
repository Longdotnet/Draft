using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionExecutionPolicyTests
{
    [Fact]
    public void EnsureExecutionPolicyCurrent_AllowsEnabledTrackedGroup()
    {
        var tracked = new ZaloTrackedGroupData
        {
            Id = "tracked-1",
            AutoSessionEnabled = true
        };

        ZaloAutoSessionActionExecutor.EnsureExecutionPolicyCurrent(tracked);
    }

    [Fact]
    public void EnsureExecutionPolicyCurrent_FailsClosedWhenTrackedGroupWasRemoved()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ZaloAutoSessionActionExecutor.EnsureExecutionPolicyCurrent(null));

        Assert.Equal("auto_session_execution_policy_missing", exception.Message);
    }

    [Fact]
    public void EnsureExecutionPolicyCurrent_FailsClosedWhenAutoSessionWasDisabled()
    {
        var tracked = new ZaloTrackedGroupData
        {
            Id = "tracked-1",
            AutoSessionEnabled = false
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ZaloAutoSessionActionExecutor.EnsureExecutionPolicyCurrent(tracked));

        Assert.Equal("auto_session_execution_policy_disabled", exception.Message);
    }
}
