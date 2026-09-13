namespace VolleyDraft.Api.Tests.Fuzzing;

internal sealed record StatefulFuzzPromotionReport<TAction>(
    StatefulFuzzReproducibilityReport<TAction> CandidateVerification,
    StatefulFuzzMinimizationResult<TAction>? Minimization,
    StatefulFuzzReproducibilityReport<TAction>? MinimizedVerification)
{
    public string? FailureFingerprint => CandidateVerification.FailureFingerprint;

    public bool IsPromotable =>
        CandidateVerification.IsReproducible &&
        Minimization is not null &&
        MinimizedVerification is { IsReproducible: true } &&
        !string.IsNullOrWhiteSpace(FailureFingerprint) &&
        string.Equals(
            MinimizedVerification.FailureFingerprint,
            FailureFingerprint,
            StringComparison.Ordinal);

    public string SerializePermanentReproducer()
    {
        if (!IsPromotable || Minimization is null || string.IsNullOrWhiteSpace(FailureFingerprint))
        {
            throw new InvalidOperationException(
                "A stateful fuzz reproducer can be serialized for permanent corpus promotion only after the candidate and minimized scenario reproduce the same failure fingerprint.");
        }

        return StatefulFuzzReproducerSerializer.Serialize(
            Minimization.Scenario,
            FailureFingerprint);
    }
}

/// <summary>
/// Promotion gate for a candidate stateful fuzz finding.
///
/// A candidate must first reproduce deterministically, then survive action and scenario-state
/// minimization, and finally the minimized scenario must independently reproduce the exact same
/// fingerprint. This prevents one lucky minimizer replay from turning a flaky or behavior-drifted
/// scenario into permanent executable corpus knowledge.
/// </summary>
internal static class StatefulFuzzPromotion
{
    public static async ValueTask<StatefulFuzzPromotionReport<TAction>> PrepareAsync<TState, TAction>(
        StatefulFuzzCase<TAction> candidate,
        IStatefulFuzzTarget<TState, TAction> target,
        Func<TAction, IEnumerable<TAction>>? shrinkAction = null,
        int confirmationRuns = StatefulFuzzReproducibility.DefaultConfirmationRuns,
        CancellationToken cancellationToken = default,
        Func<StatefulFuzzCase<TAction>, IEnumerable<StatefulFuzzCase<TAction>>>? shrinkScenario = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmationRuns);

        var candidateVerification = await StatefulFuzzReproducibility.VerifyAsync(
            candidate,
            target,
            confirmationRuns,
            cancellationToken);

        if (!candidateVerification.IsReproducible ||
            string.IsNullOrWhiteSpace(candidateVerification.FailureFingerprint))
        {
            return new StatefulFuzzPromotionReport<TAction>(
                candidateVerification,
                null,
                null);
        }

        var minimization = await StatefulFuzzScenarioMinimizer.MinimizeWithReportAsync(
            candidate,
            target,
            candidateVerification.FailureFingerprint,
            shrinkAction,
            shrinkScenario,
            cancellationToken);

        var minimizedVerification = await StatefulFuzzReproducibility.VerifyAsync(
            minimization.Scenario,
            target,
            confirmationRuns,
            cancellationToken);

        return new StatefulFuzzPromotionReport<TAction>(
            candidateVerification,
            minimization,
            minimizedVerification);
    }
}
