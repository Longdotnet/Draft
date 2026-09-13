namespace VolleyDraft.Api.Tests.Fuzzing;

/// <summary>
/// Extends action-sequence/payload minimization with a second fixed-point pass over scenario-level
/// state. Targets commonly derive their initial clock/account/group fixture from the deterministic
/// scenario seed, so a reproducer is not truly minimal if only its actions can shrink.
///
/// Scenario shrinkers own domain semantics. The core preserves the already-minimized action list so
/// a scenario-state candidate cannot accidentally re-expand action noise while reducing initial state.
/// Every accepted candidate must reproduce the exact original failure fingerprint.
/// </summary>
internal static class StatefulFuzzScenarioMinimizer
{
    public static async ValueTask<StatefulFuzzMinimizationResult<TAction>> MinimizeWithReportAsync<TState, TAction>(
        StatefulFuzzCase<TAction> failingScenario,
        IStatefulFuzzTarget<TState, TAction> target,
        string failureFingerprint,
        Func<TAction, IEnumerable<TAction>>? shrinkAction = null,
        Func<StatefulFuzzCase<TAction>, IEnumerable<StatefulFuzzCase<TAction>>>? shrinkScenario = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failingScenario);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureFingerprint);

        var actionMinimization = await StatefulFuzzMinimizer.MinimizeWithReportAsync(
            failingScenario,
            target,
            failureFingerprint,
            shrinkAction,
            cancellationToken);

        if (shrinkScenario is null)
            return actionMinimization;

        var current = actionMinimization.Scenario;
        var replayCount = actionMinimization.ReplayCount;
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            StatefulFuzzReproducerSerializer.Serialize(current, failureFingerprint)
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reduced = false;

            foreach (var rawVariant in shrinkScenario(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rawVariant is null)
                    continue;

                // Scenario-level minimization owns initial-state metadata only. Keep the action list
                // produced by ddmin/payload shrinking so state reduction cannot reintroduce noise.
                var variant = rawVariant with { Actions = current.Actions.ToArray() };
                var identity = StatefulFuzzReproducerSerializer.Serialize(variant, failureFingerprint);
                if (!visited.Add(identity))
                    continue;

                var replay = await StatefulFuzzRunner.RunAsync(variant, target, cancellationToken);
                replayCount += 1;
                if (!string.Equals(replay.FailureFingerprint, failureFingerprint, StringComparison.Ordinal))
                    continue;

                current = variant;
                reduced = true;
                break;
            }

            if (!reduced)
                break;
        }

        return new StatefulFuzzMinimizationResult<TAction>(
            current,
            replayCount,
            actionMinimization.OriginalActionCount);
    }
}
