namespace VolleyDraft.Api.Tests.Fuzzing;

/// <summary>
/// Re-runs sequence/payload minimization until the whole scenario reaches a stable fixed point.
///
/// A single left-to-right payload pass is only locally minimal. Shrinking a later action can make
/// an earlier action removable or shrinkable while preserving the same failure fingerprint. The
/// promotion path must therefore revisit the complete scenario, just like ClusterFuzz-style
/// testcase reduction repeatedly applies reducers until no reducer makes further progress.
/// </summary>
internal static class StatefulFuzzFixedPointMinimizer
{
    public static async ValueTask<StatefulFuzzMinimizationResult<TAction>> MinimizeWithReportAsync<TState, TAction>(
        StatefulFuzzCase<TAction> failingScenario,
        IStatefulFuzzTarget<TState, TAction> target,
        string failureFingerprint,
        Func<TAction, IEnumerable<TAction>>? shrinkAction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failingScenario);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureFingerprint);

        var current = failingScenario;
        var originalActionCount = failingScenario.Actions.Count;
        var replayCount = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            StatefulFuzzReproducerSerializer.Serialize(current, failureFingerprint)
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pass = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
                current,
                target,
                failureFingerprint,
                shrinkAction,
                cancellationToken);
            replayCount += pass.ReplayCount;

            var identity = StatefulFuzzReproducerSerializer.Serialize(pass.Scenario, failureFingerprint);
            if (!visited.Add(identity))
                break;

            current = pass.Scenario;
        }

        return new StatefulFuzzMinimizationResult<TAction>(
            current,
            replayCount,
            originalActionCount);
    }
}
