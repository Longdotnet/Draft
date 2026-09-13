using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzScenarioMinimizerTests
{
    [Fact]
    public async Task Scenario_state_shrinker_reaches_fixed_point_for_seed_derived_initial_state()
    {
        var scenario = new StatefulFuzzCase<SeedAction>(
            "seed-derived-initial-state",
            100,
            []);
        var target = new SeedStateTarget();
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.Equal("scenario-state:max-value", original.FailureFingerprint);

        var minimized = await StatefulFuzzScenarioMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!,
            shrinkScenario: current => current.Seed <= 1
                ? []
                : [current with { Seed = Math.Max(1, current.Seed / 2) }]);
        var replay = await StatefulFuzzRunner.RunAsync(minimized.Scenario, target);

        Assert.Equal("scenario-state:max-value", replay.FailureFingerprint);
        Assert.Equal(12, minimized.Scenario.Seed);
        Assert.Empty(minimized.Scenario.Actions);
        Assert.True(minimized.ReplayCount >= 4);
    }

    [Fact]
    public async Task Scenario_state_shrinker_cannot_change_failure_identity_or_reexpand_actions()
    {
        var scenario = new StatefulFuzzCase<SeedAction>(
            "scenario-state-fingerprint",
            100,
            [new SeedAction(SeedActionKind.Noise), new SeedAction(SeedActionKind.Increment, 1)]);
        var target = new SeedStateTarget();
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        var minimized = await StatefulFuzzScenarioMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!,
            shrinkScenario: current =>
            [
                current with
                {
                    Seed = 5,
                    Actions = [new SeedAction(SeedActionKind.Increment, 999)]
                },
                current with { Seed = 12 }
            ]);
        var replay = await StatefulFuzzRunner.RunAsync(minimized.Scenario, target);

        Assert.Equal(original.FailureFingerprint, replay.FailureFingerprint);
        Assert.Equal(12, minimized.Scenario.Seed);
        Assert.Empty(minimized.Scenario.Actions);
    }

    [Fact]
    public async Task Promotion_serializes_the_minimized_initial_state_not_the_original_seed()
    {
        var scenario = new StatefulFuzzCase<SeedAction>(
            "promotion-seed-state",
            100,
            []);

        var report = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            new SeedStateTarget(),
            confirmationRuns: 2,
            shrinkScenario: current => current.Seed <= 1
                ? []
                : [current with { Seed = Math.Max(1, current.Seed / 2) }]);

        Assert.True(report.IsPromotable);
        Assert.Equal(12, report.Minimization!.Scenario.Seed);
        Assert.Equal("scenario-state:max-value", report.FailureFingerprint);

        var serialized = report.SerializePermanentReproducer();
        var reproducer = StatefulFuzzReproducerSerializer.Deserialize<SeedAction>(serialized);

        Assert.Equal(12, reproducer.Seed);
        Assert.Equal(report.FailureFingerprint, reproducer.FailureFingerprint);
        Assert.Empty(reproducer.Actions);
    }

    private enum SeedActionKind
    {
        Noise,
        Increment
    }

    private sealed record SeedAction(SeedActionKind Kind, int Amount = 0);

    private sealed class SeedState
    {
        public int Value { get; set; }
    }

    private sealed class SeedStateTarget : IStatefulFuzzTarget<SeedState, SeedAction>
    {
        public string Name => "scenario-state";

        public SeedState CreateState(StatefulFuzzCase<SeedAction> scenario) =>
            new() { Value = scenario.Seed };

        public ValueTask ApplyAsync(
            SeedState state,
            SeedAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == SeedActionKind.Increment)
                state.Value += action.Amount;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(SeedState state)
        {
            if (state.Value > 10)
            {
                yield return new StatefulInvariantViolation(
                    "scenario-state",
                    "max-value",
                    $"Seed-derived initial state must stay <= 10 but was {state.Value}.");
            }
        }
    }
}
