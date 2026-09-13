using System.Runtime.CompilerServices;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzingFoundationTests
{
    [Fact]
    public void Stable_random_replays_the_same_sequence_for_the_same_seed()
    {
        var first = new StableFuzzRandom(20260909);
        var second = new StableFuzzRandom(20260909);

        var firstSequence = Enumerable.Range(0, 32).Select(_ => first.NextUInt32()).ToArray();
        var secondSequence = Enumerable.Range(0, 32).Select(_ => second.NextUInt32()).ToArray();

        Assert.Equal(firstSequence, secondSequence);
    }

    [Fact]
    public void Sequence_mutation_is_replayable_without_mutating_the_seed_corpus()
    {
        string[] seed = ["join", "pass", "leave"];

        var first = StatefulSequenceMutator.Mutate(
            seed,
            424242,
            random => $"generated-{random.NextInt(100)}",
            operationCount: 12);
        var second = StatefulSequenceMutator.Mutate(
            seed,
            424242,
            random => $"generated-{random.NextInt(100)}",
            operationCount: 12);

        Assert.Equal(first, second);
        Assert.Equal(["join", "pass", "leave"], seed);
    }

    [Fact]
    public async Task Runner_stops_at_the_first_grounded_invariant_violation()
    {
        var scenario = new StatefulFuzzCase<CounterAction>(
            "counter-overflow",
            17,
            [new CounterAction(CounterActionKind.Add, 11), new CounterAction(CounterActionKind.Reset)]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, new CounterTarget());

        Assert.True(result.Failed);
        Assert.Equal("counter:max-value", result.FailureFingerprint);
        Assert.Equal(0, result.FailureActionIndex);
        Assert.Single(result.ExecutedActions);
        Assert.Null(result.Exception);
        Assert.Equal("max-value", result.Violation?.Id);
    }

    [Fact]
    public async Task Minimizer_removes_irrelevant_actions_while_preserving_the_failure_fingerprint()
    {
        var target = new CounterTarget();
        var scenario = new StatefulFuzzCase<CounterAction>(
            "counter-minimize",
            91,
            [
                new CounterAction(CounterActionKind.Noise),
                new CounterAction(CounterActionKind.Add, 6),
                new CounterAction(CounterActionKind.Noise),
                new CounterAction(CounterActionKind.Add, 5),
                new CounterAction(CounterActionKind.Noise)
            ]);
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);
        Assert.Equal("counter:max-value", original.FailureFingerprint);

        var minimized = await StatefulFuzzMinimizer.MinimizeAsync(
            scenario,
            target,
            original.FailureFingerprint!);
        var replay = await StatefulFuzzRunner.RunAsync(minimized, target);

        Assert.Equal("counter:max-value", replay.FailureFingerprint);
        Assert.Equal(
            [
                new CounterAction(CounterActionKind.Add, 6),
                new CounterAction(CounterActionKind.Add, 5)
            ],
            minimized.Actions);
    }

    [Fact]
    public async Task Minimizer_delta_debugs_long_stateful_sequences_before_single_action_cleanup()
    {
        var actions = Enumerable.Repeat(new CounterAction(CounterActionKind.Noise), 32)
            .Append(new CounterAction(CounterActionKind.Add, 6))
            .Concat(Enumerable.Repeat(new CounterAction(CounterActionKind.Noise), 32))
            .Append(new CounterAction(CounterActionKind.Add, 5))
            .ToArray();
        var scenario = new StatefulFuzzCase<CounterAction>("counter-ddmin", 20260909, actions);
        var target = new CounterTarget();

        var result = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            "counter:max-value");
        var replay = await StatefulFuzzRunner.RunAsync(result.Scenario, target);

        Assert.Equal("counter:max-value", replay.FailureFingerprint);
        Assert.Equal(66, result.OriginalActionCount);
        Assert.Equal(2, result.MinimizedActionCount);
        Assert.True(result.ReplayCount < result.OriginalActionCount);
        Assert.Equal(
            [
                new CounterAction(CounterActionKind.Add, 6),
                new CounterAction(CounterActionKind.Add, 5)
            ],
            result.Scenario.Actions);
    }

    [Fact]
    public async Task Minimizer_can_shrink_action_payloads_without_domain_logic_in_the_core()
    {
        var scenario = new StatefulFuzzCase<CounterAction>(
            "counter-payload-shrink",
            77,
            [new CounterAction(CounterActionKind.Noise), new CounterAction(CounterActionKind.Add, 100)]);
        var target = new CounterTarget();

        var result = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            "counter:max-value",
            action => action.Kind == CounterActionKind.Add
                ? [action with { Amount = 1 }, action with { Amount = 11 }]
                : []);
        var replay = await StatefulFuzzRunner.RunAsync(result.Scenario, target);

        Assert.Equal("counter:max-value", replay.FailureFingerprint);
        Assert.Single(result.Scenario.Actions);
        Assert.Equal(new CounterAction(CounterActionKind.Add, 11), result.Scenario.Actions[0]);
    }

    [Fact]
    public async Task Minimizer_reapplies_payload_shrinker_until_reproducer_reaches_a_fixed_point()
    {
        var scenario = new StatefulFuzzCase<CounterAction>(
            "counter-payload-fixed-point",
            20260913,
            [new CounterAction(CounterActionKind.Add, 100)]);
        var target = new CounterTarget();

        var result = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            "counter:max-value",
            action => action.Kind != CounterActionKind.Add || action.Amount <= 1
                ? []
                : [action with { Amount = Math.Max(1, action.Amount / 2) }]);
        var replay = await StatefulFuzzRunner.RunAsync(result.Scenario, target);

        Assert.Equal("counter:max-value", replay.FailureFingerprint);
        Assert.Single(result.Scenario.Actions);
        Assert.Equal(new CounterAction(CounterActionKind.Add, 12), result.Scenario.Actions[0]);
        Assert.True(result.ReplayCount >= 4);
    }

    [Fact]
    public async Task Minimizer_does_not_accept_a_different_failure_fingerprint()
    {
        var scenario = new StatefulFuzzCase<CounterAction>(
            "counter-fingerprint",
            88,
            [new CounterAction(CounterActionKind.Throw), new CounterAction(CounterActionKind.Add, 11)]);
        var target = new CounterTarget();
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.StartsWith("exception:counter:", original.FailureFingerprint);
        var result = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!);

        Assert.Single(result.Scenario.Actions);
        Assert.Equal(CounterActionKind.Throw, result.Scenario.Actions[0].Kind);
    }

    [Fact]
    public async Task Runner_distinguishes_same_exception_type_from_different_failure_origins()
    {
        var target = new CounterTarget();
        var primary = await StatefulFuzzRunner.RunAsync(
            new StatefulFuzzCase<CounterAction>("primary-exception", 1, [new(CounterActionKind.Throw)]),
            target);
        var alternate = await StatefulFuzzRunner.RunAsync(
            new StatefulFuzzCase<CounterAction>("alternate-exception", 2, [new(CounterActionKind.ThrowAlternate)]),
            target);

        Assert.IsType<InvalidOperationException>(primary.Exception);
        Assert.IsType<InvalidOperationException>(alternate.Exception);
        Assert.NotEqual(primary.FailureFingerprint, alternate.FailureFingerprint);
        Assert.Contains(nameof(CounterTarget.ThrowPrimary), primary.FailureFingerprint, StringComparison.Ordinal);
        Assert.Contains(nameof(CounterTarget.ThrowSecondary), alternate.FailureFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minimizer_cannot_replace_the_original_exception_with_same_type_from_another_origin()
    {
        var target = new CounterTarget();
        var scenario = new StatefulFuzzCase<CounterAction>(
            "exception-origin-minimize",
            20260910,
            [
                new CounterAction(CounterActionKind.ThrowAlternate),
                new CounterAction(CounterActionKind.Throw)
            ]);
        var original = await StatefulFuzzRunner.RunAsync(scenario, target);

        var minimized = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            scenario,
            target,
            original.FailureFingerprint!);
        var replay = await StatefulFuzzRunner.RunAsync(minimized.Scenario, target);

        Assert.Single(minimized.Scenario.Actions);
        Assert.Equal(CounterActionKind.ThrowAlternate, minimized.Scenario.Actions[0].Kind);
        Assert.Equal(original.FailureFingerprint, replay.FailureFingerprint);
    }

    [Fact]
    public void Reproducer_round_trips_seed_actions_and_failure_identity()
    {
        var scenario = new StatefulFuzzCase<string>(
            "pass-share-replay",
            123456,
            ["join:a", "pass:a:b", "leave:b", "replay:pass"]);

        var json = StatefulFuzzReproducerSerializer.Serialize(scenario, "slot:duplicate-owner");
        var replay = StatefulFuzzReproducerSerializer.Deserialize<string>(json);

        Assert.Equal(scenario.Name, replay.Name);
        Assert.Equal(scenario.Seed, replay.Seed);
        Assert.Equal("slot:duplicate-owner", replay.FailureFingerprint);
        Assert.Equal(scenario.Actions, replay.Actions);
    }

    private sealed class CounterState
    {
        public int Value { get; set; }
    }

    private enum CounterActionKind
    {
        Add,
        Reset,
        Noise,
        Throw,
        ThrowAlternate
    }

    private sealed record CounterAction(CounterActionKind Kind, int Amount = 0);

    private sealed class CounterTarget : IStatefulFuzzTarget<CounterState, CounterAction>
    {
        public string Name => "counter";

        public CounterState CreateState(StatefulFuzzCase<CounterAction> scenario) => new();

        public ValueTask ApplyAsync(
            CounterState state,
            CounterAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case CounterActionKind.Add:
                    state.Value += action.Amount;
                    break;
                case CounterActionKind.Reset:
                    state.Value = 0;
                    break;
                case CounterActionKind.Noise:
                    break;
                case CounterActionKind.Throw:
                    ThrowPrimary();
                    break;
                case CounterActionKind.ThrowAlternate:
                    ThrowSecondary();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            return ValueTask.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowPrimary() =>
            throw new InvalidOperationException("synthetic target exception");

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowSecondary() =>
            throw new InvalidOperationException("synthetic alternate target exception");

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(CounterState state)
        {
            if (state.Value > 10)
            {
                yield return new StatefulInvariantViolation(
                    "counter",
                    "max-value",
                    $"Counter must stay <= 10 but was {state.Value}.");
            }
        }
    }
}
