using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzGlobalFixedPointMinimizerTests
{
    [Fact]
    public async Task Payload_minimization_revisits_earlier_actions_after_later_payload_shrinks()
    {
        var scenario = new StatefulFuzzCase<RelationalAction>(
            "cross-action-payload-fixed-point",
            1,
            [new RelationalAction(100), new RelationalAction(100)]);
        var target = new RelationalTarget();
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.Equal("minimizer:first-ge-second", original.FailureFingerprint);

        var minimized = await StatefulFuzzScenarioMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!,
            shrinkAction: Shrink);
        var replay = await StatefulFuzzRunner.RunAsync(minimized.Scenario, target);

        Assert.Equal(original.FailureFingerprint, replay.FailureFingerprint);
        Assert.Equal([new RelationalAction(1), new RelationalAction(1)], minimized.Scenario.Actions);
        Assert.True(minimized.ReplayCount > 0);
    }

    [Fact]
    public async Task State_reduction_reopens_action_reduction_until_cross_dimension_fixed_point()
    {
        var scenario = new StatefulFuzzCase<RelationalAction>(
            "state-action-fixed-point",
            100,
            [new RelationalAction(60), new RelationalAction(60)]);
        var target = new ThresholdTarget();
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.Equal("minimizer:threshold-total", original.FailureFingerprint);

        var minimized = await StatefulFuzzScenarioMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!,
            shrinkAction: Shrink,
            shrinkScenario: current => current.Seed <= 1
                ? []
                : [current with { Seed = Math.Max(1, current.Seed / 2) }]);
        var replay = await StatefulFuzzRunner.RunAsync(minimized.Scenario, target);

        Assert.Equal(original.FailureFingerprint, replay.FailureFingerprint);
        Assert.Equal(1, minimized.Scenario.Seed);
        Assert.Equal([new RelationalAction(1), new RelationalAction(1)], minimized.Scenario.Actions);
    }

    private static IEnumerable<RelationalAction> Shrink(RelationalAction action)
    {
        if (action.Value <= 1)
            yield break;

        yield return action with { Value = Math.Max(1, action.Value / 2) };
    }

    private sealed record RelationalAction(int Value);

    private sealed class RelationalState
    {
        public List<int> Values { get; } = [];
    }

    private sealed class RelationalTarget : IStatefulFuzzTarget<RelationalState, RelationalAction>
    {
        public string Name => "cross-action-minimizer";

        public RelationalState CreateState(StatefulFuzzCase<RelationalAction> scenario) => new();

        public ValueTask ApplyAsync(
            RelationalState state,
            RelationalAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Values.Add(action.Value);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(RelationalState state)
        {
            if (state.Values.Count == 2 && state.Values[0] >= state.Values[1])
                yield return new StatefulInvariantViolation("minimizer", "first-ge-second", "first >= second");
        }
    }

    private sealed class ThresholdState
    {
        public required int Threshold { get; init; }
        public List<int> Values { get; } = [];
    }

    private sealed class ThresholdTarget : IStatefulFuzzTarget<ThresholdState, RelationalAction>
    {
        public string Name => "state-action-minimizer";

        public ThresholdState CreateState(StatefulFuzzCase<RelationalAction> scenario) =>
            new() { Threshold = scenario.Seed };

        public ValueTask ApplyAsync(
            ThresholdState state,
            RelationalAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Values.Add(action.Value);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ThresholdState state)
        {
            if (state.Values.Count == 2 && state.Values.Sum() >= state.Threshold)
                yield return new StatefulInvariantViolation("minimizer", "threshold-total", "total >= threshold");
        }
    }
}
