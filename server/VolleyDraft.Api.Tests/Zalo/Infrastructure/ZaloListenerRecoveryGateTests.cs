using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloListenerRecoveryGateTests
{
    [Fact]
    public void TryBegin_QueuesOnlyOnceForSameListenerGeneration()
    {
        var gate = new ZaloListenerRecoveryGate();

        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
        Assert.False(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
    }

    [Fact]
    public void TryBegin_QueuesAgainWhenBridgeReportsNewListenerGeneration()
    {
        var gate = new ZaloListenerRecoveryGate();

        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 2000));
        Assert.False(gate.TryBegin("account-a", "connection-a", "group-a", 2000));
    }

    [Fact]
    public void TryBegin_KeepsAccountConnectionAndGroupScopesIndependent()
    {
        var gate = new ZaloListenerRecoveryGate();

        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
        Assert.True(gate.TryBegin("account-a", "connection-a", "group-b", 1000));
        Assert.True(gate.TryBegin("account-a", "connection-b", "group-a", 1000));
        Assert.True(gate.TryBegin("account-b", "connection-a", "group-a", 1000));
    }

    [Fact]
    public void Release_AllowsSameGenerationToRetryAfterQueueFailure()
    {
        var gate = new ZaloListenerRecoveryGate();

        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
        gate.Release("account-a", "connection-a", "group-a", 1000);
        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
    }

    [Fact]
    public void Release_DoesNotEraseNewerGeneration()
    {
        var gate = new ZaloListenerRecoveryGate();

        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 1000));
        Assert.True(gate.TryBegin("account-a", "connection-a", "group-a", 2000));
        gate.Release("account-a", "connection-a", "group-a", 1000);
        Assert.False(gate.TryBegin("account-a", "connection-a", "group-a", 2000));
    }
}
