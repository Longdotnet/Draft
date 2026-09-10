namespace VolleyDraft.Api.Tests.Fuzzing;

internal sealed record StatefulFuzzReproducibilityReport<TAction>(
    StatefulFuzzRunResult<TAction> Baseline,
    IReadOnlyList<StatefulFuzzRunResult<TAction>> Replays)
{
    public bool HasFailure => Baseline.Failed;

    public string? FailureFingerprint => Baseline.FailureFingerprint;

    public bool IsReproducible =>
        HasFailure &&
        !string.IsNullOrWhiteSpace(FailureFingerprint) &&
        Replays.Count > 0 &&
        Replays.All(replay =>
            replay.Failed &&
            string.Equals(
                replay.FailureFingerprint,
                FailureFingerprint,
                StringComparison.Ordinal));

    public StatefulFuzzRunResult<TAction>? FirstDivergentReplay =>
        !HasFailure || string.IsNullOrWhiteSpace(FailureFingerprint)
            ? null
            : Replays.FirstOrDefault(replay =>
                !replay.Failed ||
                !string.Equals(
                    replay.FailureFingerprint,
                    FailureFingerprint,
                    StringComparison.Ordinal));
}

/// <summary>
/// Proves that a candidate stateful fuzz failure is deterministic before it is minimized,
/// deduplicated or promoted into the permanent corpus. A single failing execution is evidence
/// of a candidate finding, not yet evidence of a stable reproducer.
/// </summary>
internal static class StatefulFuzzReproducibility
{
    public const int DefaultConfirmationRuns = 2;

    public static async ValueTask<StatefulFuzzReproducibilityReport<TAction>> VerifyAsync<TState, TAction>(
        StatefulFuzzCase<TAction> scenario,
        IStatefulFuzzTarget<TState, TAction> target,
        int confirmationRuns = DefaultConfirmationRuns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmationRuns);

        var baseline = await StatefulFuzzRunner.RunAsync(scenario, target, cancellationToken);
        if (!baseline.Failed)
        {
            return new StatefulFuzzReproducibilityReport<TAction>(
                baseline,
                Array.Empty<StatefulFuzzRunResult<TAction>>());
        }

        var expectedFingerprint = baseline.FailureFingerprint;
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return new StatefulFuzzReproducibilityReport<TAction>(
                baseline,
                Array.Empty<StatefulFuzzRunResult<TAction>>());
        }

        var replays = new List<StatefulFuzzRunResult<TAction>>(confirmationRuns);
        for (var attempt = 0; attempt < confirmationRuns; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replay = await StatefulFuzzRunner.RunAsync(scenario, target, cancellationToken);
            replays.Add(replay);

            if (!replay.Failed ||
                !string.Equals(replay.FailureFingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                break;
            }
        }

        return new StatefulFuzzReproducibilityReport<TAction>(baseline, replays);
    }
}
