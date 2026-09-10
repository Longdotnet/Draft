using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzIsolatedTargetTests
{
    [Fact]
    public async Task Promotion_replays_can_use_fresh_target_instances_instead_of_shared_state()
    {
        var scenario = new StatefulFuzzCase<int>("isolated-promotion-sentinel", 20260910, []);

        await using var sharedTarget = new StatefulFuzzIsolatedTarget<StickyState, int>(
            static () => new StickyFailureTarget(),
            isolateEachRun: false);
        var shared = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            sharedTarget,
            confirmationRuns: 2);

        Assert.False(shared.IsPromotable);
        Assert.NotNull(shared.CandidateVerification.FirstDivergentReplay);

        await using var isolatedTarget = new StatefulFuzzIsolatedTarget<StickyState, int>(
            static () => new StickyFailureTarget());
        var isolated = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            isolatedTarget,
            confirmationRuns: 2);

        Assert.True(isolated.IsPromotable);
        Assert.Equal("fuzz-infrastructure:sticky-instance-sentinel", isolated.FailureFingerprint);
        Assert.Contains("isolated-promotion-sentinel", isolated.SerializePermanentReproducer());
        Assert.True(isolatedTarget.CreatedTargetCount >= 6);
    }

    [Fact]
    public async Task Real_team_preference_cross_feature_target_replays_from_fresh_domain_state()
    {
        var scenario = new StatefulFuzzCase<TeamPreferenceRosterCrossFeatureFuzzTests.RosterAction>(
            "team-preference-isolated-replay",
            20260910,
            [
                new(TeamPreferenceRosterCrossFeatureFuzzTests.RosterActionKind.SetPresence, 3, false),
                new(TeamPreferenceRosterCrossFeatureFuzzTests.RosterActionKind.Reconcile, 0, false),
                new(TeamPreferenceRosterCrossFeatureFuzzTests.RosterActionKind.Restart, 0, false),
                new(TeamPreferenceRosterCrossFeatureFuzzTests.RosterActionKind.SetPresence, 2, false),
                new(TeamPreferenceRosterCrossFeatureFuzzTests.RosterActionKind.Reconcile, 0, false)
            ]);

        await using var isolatedTarget = new StatefulFuzzIsolatedTarget<
            TeamPreferenceRosterCrossFeatureFuzzTests.RosterState,
            TeamPreferenceRosterCrossFeatureFuzzTests.RosterAction>(
                static () => new TeamPreferenceRosterCrossFeatureFuzzTests.TeamPreferenceRosterTarget());

        for (var replay = 0; replay < 3; replay += 1)
        {
            var result = await StatefulFuzzRunner.RunAsync(scenario, isolatedTarget);
            Assert.False(result.Failed, result.FailureFingerprint ?? result.Exception?.Message ?? "unexpected fuzz failure");
        }

        Assert.Equal(3, isolatedTarget.CreatedTargetCount);
    }

    private sealed class StickyState
    {
        public required bool ShouldFail { get; init; }
    }

    private sealed class StickyFailureTarget : IStatefulFuzzTarget<StickyState, int>
    {
        private bool hasCreatedState;

        public string Name => "sticky-failure-target";

        public StickyState CreateState(StatefulFuzzCase<int> scenario)
        {
            var state = new StickyState { ShouldFail = !hasCreatedState };
            hasCreatedState = true;
            return state;
        }

        public ValueTask ApplyAsync(
            StickyState state,
            int action,
            int actionIndex,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(StickyState state)
        {
            if (state.ShouldFail)
            {
                yield return new StatefulInvariantViolation(
                    "fuzz-infrastructure",
                    "sticky-instance-sentinel",
                    "A target-instance sentinel intentionally fails only on the first state created by one target instance.",
                    "fuzz-infrastructure:sticky-instance-sentinel");
            }
        }
    }
}

internal sealed class StatefulFuzzIsolatedTarget<TState, TAction> :
    IStatefulFuzzTarget<TState, TAction>,
    IAsyncDisposable
{
    private readonly Func<IStatefulFuzzTarget<TState, TAction>> targetFactory;
    private readonly bool isolateEachRun;
    private readonly List<IStatefulFuzzTarget<TState, TAction>> createdTargets = [];
    private IStatefulFuzzTarget<TState, TAction>? currentTarget;

    public StatefulFuzzIsolatedTarget(
        Func<IStatefulFuzzTarget<TState, TAction>> targetFactory,
        bool isolateEachRun = true)
    {
        ArgumentNullException.ThrowIfNull(targetFactory);
        this.targetFactory = targetFactory;
        this.isolateEachRun = isolateEachRun;
    }

    public string Name => currentTarget?.Name ?? "isolated-stateful-fuzz-target";

    public int CreatedTargetCount => createdTargets.Count;

    public TState CreateState(StatefulFuzzCase<TAction> scenario)
    {
        if (currentTarget is null || isolateEachRun)
        {
            currentTarget = targetFactory()
                ?? throw new InvalidOperationException("The stateful fuzz target factory returned null.");
            createdTargets.Add(currentTarget);
        }

        return currentTarget.CreateState(scenario);
    }

    public ValueTask ApplyAsync(
        TState state,
        TAction action,
        int actionIndex,
        CancellationToken cancellationToken) =>
        RequireCurrentTarget().ApplyAsync(state, action, actionIndex, cancellationToken);

    public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(TState state) =>
        RequireCurrentTarget().EvaluateInvariants(state);

    public async ValueTask DisposeAsync()
    {
        for (var index = createdTargets.Count - 1; index >= 0; index -= 1)
        {
            switch (createdTargets[index])
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        createdTargets.Clear();
        currentTarget = null;
    }

    private IStatefulFuzzTarget<TState, TAction> RequireCurrentTarget() =>
        currentTarget ?? throw new InvalidOperationException(
            "CreateState must be called before applying actions or evaluating invariants.");
}
