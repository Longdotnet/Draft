using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzPromotionTests
{
    [Fact]
    public async Task Stable_candidate_is_minimized_reverified_and_serialized_for_permanent_corpus()
    {
        var scenario = new StatefulFuzzCase<PromotionAction>(
            "stable-promotion",
            20260910,
            [
                new PromotionAction(PromotionActionKind.Noise, 99),
                new PromotionAction(PromotionActionKind.Increment, 11),
                new PromotionAction(PromotionActionKind.Noise, 42)
            ]);

        var report = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            new StablePromotionTarget(),
            confirmationRuns: 2);

        Assert.True(report.IsPromotable);
        Assert.NotNull(report.Minimization);
        Assert.NotNull(report.MinimizedVerification);
        Assert.Equal("promotion:max-value", report.FailureFingerprint);
        Assert.Single(report.Minimization!.Scenario.Actions);
        Assert.Equal(PromotionActionKind.Increment, report.Minimization.Scenario.Actions[0].Kind);
        Assert.Equal(11, report.Minimization.Scenario.Actions[0].Amount);
        Assert.True(report.MinimizedVerification!.IsReproducible);
        Assert.Equal(report.FailureFingerprint, report.MinimizedVerification.FailureFingerprint);

        var serialized = report.SerializePermanentReproducer();
        var reproducer = StatefulFuzzReproducerSerializer.Deserialize<PromotionAction>(serialized);

        Assert.Equal(report.FailureFingerprint, reproducer.FailureFingerprint);
        Assert.Single(reproducer.Actions);
        Assert.Equal(PromotionActionKind.Increment, reproducer.Actions[0].Kind);
    }

    [Fact]
    public async Task Non_reproducible_candidate_is_rejected_before_minimization()
    {
        var scenario = new StatefulFuzzCase<PromotionAction>(
            "flaky-candidate",
            20260910,
            [new PromotionAction(PromotionActionKind.Trigger)]);

        var report = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            new AlternatingCandidateTarget(),
            confirmationRuns: 3);

        Assert.False(report.IsPromotable);
        Assert.False(report.CandidateVerification.IsReproducible);
        Assert.Null(report.Minimization);
        Assert.Null(report.MinimizedVerification);
        Assert.Throws<InvalidOperationException>(() => report.SerializePermanentReproducer());
    }

    [Fact]
    public async Task Minimizer_single_lucky_replay_cannot_promote_an_unstable_reproducer()
    {
        var scenario = new StatefulFuzzCase<PromotionAction>(
            "minimizer-induced-flake",
            20260910,
            [
                new PromotionAction(PromotionActionKind.Noise),
                new PromotionAction(PromotionActionKind.Trigger)
            ]);
        var target = new ShortScenarioAlternatesTarget();

        var report = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            target,
            confirmationRuns: 2);

        Assert.True(report.CandidateVerification.IsReproducible);
        Assert.NotNull(report.Minimization);
        Assert.Single(report.Minimization!.Scenario.Actions);
        Assert.Equal(PromotionActionKind.Trigger, report.Minimization.Scenario.Actions[0].Kind);

        Assert.NotNull(report.MinimizedVerification);
        Assert.False(report.MinimizedVerification!.IsReproducible);
        Assert.False(report.MinimizedVerification.HasFailure);
        Assert.False(report.IsPromotable);
        Assert.Throws<InvalidOperationException>(() => report.SerializePermanentReproducer());
    }

    [Fact]
    public async Task Minimized_failure_fingerprint_drift_is_rejected_from_promotion()
    {
        var scenario = new StatefulFuzzCase<PromotionAction>(
            "minimized-fingerprint-drift",
            20260910,
            [
                new PromotionAction(PromotionActionKind.Noise),
                new PromotionAction(PromotionActionKind.Trigger)
            ]);
        var target = new ShortScenarioFingerprintDriftTarget();

        var report = await StatefulFuzzPromotion.PrepareAsync(
            scenario,
            target,
            confirmationRuns: 2);

        Assert.True(report.CandidateVerification.IsReproducible);
        Assert.NotNull(report.Minimization);
        Assert.NotNull(report.MinimizedVerification);
        Assert.False(report.IsPromotable);
        Assert.NotEqual(report.FailureFingerprint, report.MinimizedVerification!.FailureFingerprint);
        Assert.Throws<InvalidOperationException>(() => report.SerializePermanentReproducer());
    }

    private enum PromotionActionKind
    {
        Noise,
        Increment,
        Trigger
    }

    private sealed record PromotionAction(PromotionActionKind Kind, int Amount = 0);

    private sealed class PromotionState
    {
        public int Value { get; set; }
        public bool Triggered { get; set; }
        public bool AlternateFailure { get; init; }
        public string Fingerprint { get; init; } = "promotion:triggered";
    }

    private sealed class StablePromotionTarget : IStatefulFuzzTarget<PromotionState, PromotionAction>
    {
        public string Name => "stable-promotion";

        public PromotionState CreateState(StatefulFuzzCase<PromotionAction> scenario) => new();

        public ValueTask ApplyAsync(
            PromotionState state,
            PromotionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PromotionActionKind.Increment)
                state.Value += action.Amount;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PromotionState state)
        {
            if (state.Value > 10)
                yield return new StatefulInvariantViolation("promotion", "max-value", "Value exceeded limit.");
        }
    }

    private sealed class AlternatingCandidateTarget : IStatefulFuzzTarget<PromotionState, PromotionAction>
    {
        private int runCount;

        public string Name => "alternating-candidate";

        public PromotionState CreateState(StatefulFuzzCase<PromotionAction> scenario)
        {
            runCount += 1;
            return new PromotionState { AlternateFailure = runCount % 2 == 1 };
        }

        public ValueTask ApplyAsync(
            PromotionState state,
            PromotionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PromotionActionKind.Trigger && state.AlternateFailure)
                state.Triggered = true;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PromotionState state)
        {
            if (state.Triggered)
                yield return new StatefulInvariantViolation("promotion", "alternating", "Intermittent candidate.");
        }
    }

    private sealed class ShortScenarioAlternatesTarget : IStatefulFuzzTarget<PromotionState, PromotionAction>
    {
        private int shortRunCount;

        public string Name => "short-scenario-alternates";

        public PromotionState CreateState(StatefulFuzzCase<PromotionAction> scenario)
        {
            if (scenario.Actions.Count > 1)
                return new PromotionState { AlternateFailure = true };

            shortRunCount += 1;
            return new PromotionState { AlternateFailure = shortRunCount % 2 == 1 };
        }

        public ValueTask ApplyAsync(
            PromotionState state,
            PromotionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PromotionActionKind.Trigger && state.AlternateFailure)
                state.Triggered = true;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PromotionState state)
        {
            if (state.Triggered)
                yield return new StatefulInvariantViolation("promotion", "triggered", "Triggered failure.");
        }
    }

    private sealed class ShortScenarioFingerprintDriftTarget : IStatefulFuzzTarget<PromotionState, PromotionAction>
    {
        private int shortRunCount;

        public string Name => "short-scenario-fingerprint-drift";

        public PromotionState CreateState(StatefulFuzzCase<PromotionAction> scenario)
        {
            if (scenario.Actions.Count > 1)
                return new PromotionState { AlternateFailure = true, Fingerprint = "promotion:original" };

            shortRunCount += 1;
            return new PromotionState
            {
                AlternateFailure = true,
                Fingerprint = shortRunCount == 1 ? "promotion:original" : "promotion:drifted"
            };
        }

        public ValueTask ApplyAsync(
            PromotionState state,
            PromotionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PromotionActionKind.Trigger && state.AlternateFailure)
                state.Triggered = true;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PromotionState state)
        {
            if (state.Triggered)
                yield return new StatefulInvariantViolation(
                    "promotion",
                    "triggered",
                    "Triggered failure.",
                    state.Fingerprint);
        }
    }
}
