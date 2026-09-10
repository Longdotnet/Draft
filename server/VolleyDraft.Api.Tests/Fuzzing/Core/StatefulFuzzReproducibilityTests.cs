using System.Runtime.CompilerServices;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class StatefulFuzzReproducibilityTests
{
    [Fact]
    public async Task Stable_invariant_failure_is_confirmed_before_promotion()
    {
        var scenario = new StatefulFuzzCase<ProbeAction>(
            "stable-invariant",
            20260910,
            [new ProbeAction(ProbeActionKind.Increment, 11)]);

        var report = await StatefulFuzzReproducibility.VerifyAsync(
            scenario,
            new StableInvariantTarget(),
            confirmationRuns: 3);

        Assert.True(report.HasFailure);
        Assert.True(report.IsReproducible);
        Assert.Equal("repro:max-value", report.FailureFingerprint);
        Assert.Equal(3, report.Replays.Count);
        Assert.All(report.Replays, replay =>
            Assert.Equal(report.FailureFingerprint, replay.FailureFingerprint));
        Assert.Null(report.FirstDivergentReplay);
    }

    [Fact]
    public async Task Intermittent_failure_is_rejected_and_stops_on_first_divergence()
    {
        var scenario = new StatefulFuzzCase<ProbeAction>(
            "intermittent",
            20260910,
            [new ProbeAction(ProbeActionKind.Trigger)]);
        var target = new AlternatingTarget();

        var report = await StatefulFuzzReproducibility.VerifyAsync(
            scenario,
            target,
            confirmationRuns: 5);

        Assert.True(report.HasFailure);
        Assert.False(report.IsReproducible);
        Assert.Equal("repro:alternating", report.FailureFingerprint);
        Assert.Single(report.Replays);
        Assert.NotNull(report.FirstDivergentReplay);
        Assert.False(report.FirstDivergentReplay!.Failed);
    }

    [Fact]
    public async Task Same_exception_type_from_different_origins_is_not_treated_as_reproducible()
    {
        var scenario = new StatefulFuzzCase<ProbeAction>(
            "exception-origin-drift",
            20260910,
            [new ProbeAction(ProbeActionKind.Trigger)]);
        var target = new AlternatingExceptionOriginTarget();

        var report = await StatefulFuzzReproducibility.VerifyAsync(
            scenario,
            target,
            confirmationRuns: 4);

        Assert.True(report.HasFailure);
        Assert.False(report.IsReproducible);
        Assert.Single(report.Replays);
        Assert.IsType<InvalidOperationException>(report.Baseline.Exception);
        Assert.IsType<InvalidOperationException>(report.Replays[0].Exception);
        Assert.NotEqual(report.FailureFingerprint, report.Replays[0].FailureFingerprint);
        Assert.Contains(nameof(AlternatingExceptionOriginTarget.ThrowPrimary), report.FailureFingerprint, StringComparison.Ordinal);
        Assert.Contains(nameof(AlternatingExceptionOriginTarget.ThrowSecondary), report.Replays[0].FailureFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passing_scenario_is_not_misreported_as_a_reproducible_failure()
    {
        var scenario = new StatefulFuzzCase<ProbeAction>(
            "passing",
            20260910,
            [new ProbeAction(ProbeActionKind.Increment, 1)]);

        var report = await StatefulFuzzReproducibility.VerifyAsync(
            scenario,
            new StableInvariantTarget());

        Assert.False(report.HasFailure);
        Assert.False(report.IsReproducible);
        Assert.Null(report.FailureFingerprint);
        Assert.Empty(report.Replays);
        Assert.Null(report.FirstDivergentReplay);
    }

    private enum ProbeActionKind
    {
        Increment,
        Trigger
    }

    private sealed record ProbeAction(ProbeActionKind Kind, int Amount = 0);

    private sealed class ProbeState
    {
        public int Value { get; set; }
    }

    private sealed class StableInvariantTarget : IStatefulFuzzTarget<ProbeState, ProbeAction>
    {
        public string Name => "stable-repro";

        public ProbeState CreateState(StatefulFuzzCase<ProbeAction> scenario) => new();

        public ValueTask ApplyAsync(
            ProbeState state,
            ProbeAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == ProbeActionKind.Increment)
                state.Value += action.Amount;

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ProbeState state)
        {
            if (state.Value > 10)
                yield return new StatefulInvariantViolation("repro", "max-value", "Value exceeded the grounded limit.");
        }
    }

    private sealed class AlternatingTarget : IStatefulFuzzTarget<ProbeState, ProbeAction>
    {
        private int runCount;

        public string Name => "alternating-repro";

        public ProbeState CreateState(StatefulFuzzCase<ProbeAction> scenario)
        {
            runCount += 1;
            return new ProbeState { Value = runCount % 2 == 1 ? 11 : 0 };
        }

        public ValueTask ApplyAsync(
            ProbeState state,
            ProbeAction action,
            int actionIndex,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ProbeState state)
        {
            if (state.Value > 10)
                yield return new StatefulInvariantViolation("repro", "alternating", "Synthetic intermittent failure.");
        }
    }

    private sealed class AlternatingExceptionOriginTarget : IStatefulFuzzTarget<ProbeState, ProbeAction>
    {
        private int runCount;

        public string Name => "exception-origin-repro";

        public ProbeState CreateState(StatefulFuzzCase<ProbeAction> scenario)
        {
            runCount += 1;
            return new ProbeState { Value = runCount };
        }

        public ValueTask ApplyAsync(
            ProbeState state,
            ProbeAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (state.Value % 2 == 1)
                ThrowPrimary();
            else
                ThrowSecondary();

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ProbeState state) => [];

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowPrimary() => throw new InvalidOperationException("same type, primary origin");

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowSecondary() => throw new InvalidOperationException("same type, secondary origin");
    }
}
