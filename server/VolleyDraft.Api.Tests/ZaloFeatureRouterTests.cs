using VolleyDraft.Api.Services.Zalo.Routing;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloFeatureRouterTests
{
    [Fact]
    public async Task RouteAsync_DeterministicFeatureBeatsHigherScoringModelFeature()
    {
        var draft = new StubFeature(
            ZaloFeatureId.Draft,
            priority: 50,
            new ZaloFeatureMatch(80, true, "draft_command"));
        var social = new StubFeature(
            ZaloFeatureId.Social,
            priority: 90,
            new ZaloFeatureMatch(99, false, "model_social"));
        var router = new ZaloFeatureRouter([social, draft]);

        var result = await router.RouteAsync(Turn("@Npc 9 T6"));

        Assert.True(result.Handled);
        Assert.Equal(ZaloFeatureId.Draft, result.Feature);
        Assert.Equal(1, draft.HandleCount);
        Assert.Equal(0, social.HandleCount);
    }

    [Fact]
    public async Task RouteAsync_EqualTopCandidatesFailClosed()
    {
        var pass = new StubFeature(
            ZaloFeatureId.PassSlot,
            priority: 50,
            new ZaloFeatureMatch(95, true, "pass_query"));
        var waitlist = new StubFeature(
            ZaloFeatureId.Waitlist,
            priority: 50,
            new ZaloFeatureMatch(95, true, "waitlist_query"));
        var router = new ZaloFeatureRouter([pass, waitlist]);

        var result = await router.RouteAsync(Turn("CN này còn slot không"));

        Assert.False(result.Handled);
        Assert.True(result.Ambiguous);
        Assert.Null(result.Feature);
        Assert.Equal(0, pass.HandleCount);
        Assert.Equal(0, waitlist.HandleCount);
    }

    [Fact]
    public async Task RouteAsync_ExecutesOnlyOneWinningFeature()
    {
        var first = new StubFeature(
            ZaloFeatureId.TeamPreference,
            priority: 80,
            new ZaloFeatureMatch(90, true, "team_preference"));
        var second = new StubFeature(
            ZaloFeatureId.Social,
            priority: 10,
            new ZaloFeatureMatch(70, false, "chat"));
        var router = new ZaloFeatureRouter([first, second]);

        var result = await router.RouteAsync(Turn("@Npc cho A với B chung team CN"));

        Assert.True(result.Handled);
        Assert.Equal(ZaloFeatureId.TeamPreference, result.Feature);
        Assert.Equal(1, first.HandleCount);
        Assert.Equal(0, second.HandleCount);
    }

    private static ZaloFeatureTurn Turn(string content) => new(
        "account",
        "group",
        "sender",
        "Long",
        Guid.NewGuid().ToString("N"),
        content,
        MentionedBot: true,
        DateTimeOffset.UtcNow);

    private sealed class StubFeature(
        ZaloFeatureId feature,
        int priority,
        ZaloFeatureMatch? match) : IZaloFeatureModule
    {
        public ZaloFeatureId Feature { get; } = feature;
        public int Priority { get; } = priority;
        public int HandleCount { get; private set; }

        public ValueTask<ZaloFeatureMatch?> MatchAsync(
            ZaloFeatureTurn turn,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(match);

        public Task<ZaloFeatureExecutionResult> HandleAsync(
            ZaloFeatureTurn turn,
            CancellationToken cancellationToken = default)
        {
            HandleCount++;
            return Task.FromResult(new ZaloFeatureExecutionResult(true, $"handled:{Feature}"));
        }
    }
}
