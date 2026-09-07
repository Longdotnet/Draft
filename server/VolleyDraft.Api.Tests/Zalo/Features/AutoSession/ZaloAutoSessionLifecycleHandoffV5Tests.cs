using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;

namespace VolleyDraft.Api.Tests.Zalo.Features.AutoSession;

public sealed class ZaloAutoSessionLifecycleHandoffV5Tests
{
    [Theory]
    [InlineData(MatchLifecycleStage.Recruiting)]
    [InlineData(MatchLifecycleStage.ResolvingOverbook)]
    [InlineData(MatchLifecycleStage.ResolvingPassSlots)]
    [InlineData(MatchLifecycleStage.AwaitingProfiles)]
    [InlineData(MatchLifecycleStage.ReadyForDraft)]
    [InlineData(MatchLifecycleStage.Drafting)]
    [InlineData(MatchLifecycleStage.Drafted)]
    public void CanHandOff_AllowsAuthoritativeLifecycleStages(MatchLifecycleStage stage)
    {
        var lifecycle = Build(stage, "ready");
        Assert.True(ZaloAutoSessionLifecycleHandoffPolicyV5.CanHandOff(lifecycle));
    }

    [Theory]
    [InlineData(MatchLifecycleStage.NeedsSetup)]
    [InlineData(MatchLifecycleStage.NeedsAttention)]
    public void CanHandOff_FailsClosedWhenLifecycleCannotOwnSafely(MatchLifecycleStage stage)
    {
        var lifecycle = Build(stage, "unsafe");
        Assert.False(ZaloAutoSessionLifecycleHandoffPolicyV5.CanHandOff(lifecycle));
        Assert.Equal($"lifecycle_not_ready:{stage}:unsafe",
            ZaloAutoSessionLifecycleHandoffPolicyV5.BuildFailureReason(lifecycle));
    }

    private static MatchLifecycleResponse Build(MatchLifecycleStage stage, string reason) => new(
        "session-1",
        "T6 11/09 17:45",
        stage,
        stage.ToString(),
        "headline",
        "next",
        MatchLifecycleOwner.ZaloBot,
        false,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        18,
        0,
        [],
        0,
        null,
        reason,
        DateTimeOffset.UtcNow);
}
