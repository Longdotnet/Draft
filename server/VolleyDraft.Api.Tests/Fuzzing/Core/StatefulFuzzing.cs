using System.Text.Json;

namespace VolleyDraft.Api.Tests.Fuzzing;

internal sealed record StatefulFuzzCase<TAction>(
    string Name,
    int Seed,
    IReadOnlyList<TAction> Actions);

internal sealed record StatefulInvariantViolation(
    string Domain,
    string Id,
    string Message,
    string? Fingerprint = null)
{
    public string StableFingerprint =>
        string.IsNullOrWhiteSpace(Fingerprint) ? $"{Domain}:{Id}" : Fingerprint;
}

internal interface IStatefulFuzzTarget<TState, TAction>
{
    string Name { get; }

    TState CreateState(StatefulFuzzCase<TAction> scenario);

    ValueTask ApplyAsync(
        TState state,
        TAction action,
        int actionIndex,
        CancellationToken cancellationToken);

    IEnumerable<StatefulInvariantViolation> EvaluateInvariants(TState state);
}

internal sealed record StatefulFuzzRunResult<TAction>(
    string TargetName,
    StatefulFuzzCase<TAction> Scenario,
    IReadOnlyList<TAction> ExecutedActions,
    StatefulInvariantViolation? Violation,
    Exception? Exception,
    int? FailureActionIndex)
{
    public bool Failed => Violation is not null || Exception is not null;

    public string? FailureFingerprint => Violation?.StableFingerprint ??
        (Exception is null ? null : $"exception:{TargetName}:{Exception.GetType().FullName}");
}

internal static class StatefulFuzzRunner
{
    public static async ValueTask<StatefulFuzzRunResult<TAction>> RunAsync<TState, TAction>(
        StatefulFuzzCase<TAction> scenario,
        IStatefulFuzzTarget<TState, TAction> target,
        CancellationToken cancellationToken = default)
    {
        var state = target.CreateState(scenario);
        var executed = new List<TAction>(scenario.Actions.Count);
        var initialViolation = target.EvaluateInvariants(state).FirstOrDefault();
        if (initialViolation is not null)
        {
            return new StatefulFuzzRunResult<TAction>(
                target.Name,
                scenario,
                executed,
                initialViolation,
                null,
                null);
        }

        for (var index = 0; index < scenario.Actions.Count; index += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = scenario.Actions[index];
            executed.Add(action);

            try
            {
                await target.ApplyAsync(state, action, index, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new StatefulFuzzRunResult<TAction>(
                    target.Name,
                    scenario,
                    executed,
                    null,
                    exception,
                    index);
            }

            var violation = target.EvaluateInvariants(state).FirstOrDefault();
            if (violation is not null)
            {
                return new StatefulFuzzRunResult<TAction>(
                    target.Name,
                    scenario,
                    executed,
                    violation,
                    null,
                    index);
            }
        }

        return new StatefulFuzzRunResult<TAction>(
            target.Name,
            scenario,
            executed,
            null,
            null,
            null);
    }
}

internal sealed record StatefulFuzzMinimizationResult<TAction>(
    StatefulFuzzCase<TAction> Scenario,
    int ReplayCount,
    int OriginalActionCount)
{
    public int MinimizedActionCount => Scenario.Actions.Count;
}

internal static class StatefulFuzzMinimizer
{
    public static async ValueTask<StatefulFuzzCase<TAction>> MinimizeAsync<TState, TAction>(
        StatefulFuzzCase<TAction> failingScenario,
        IStatefulFuzzTarget<TState, TAction> target,
        string failureFingerprint,
        CancellationToken cancellationToken = default)
    {
        var result = await MinimizeWithReportAsync(
            failingScenario,
            target,
            failureFingerprint,
            shrinkAction: null,
            cancellationToken);
        return result.Scenario;
    }

    public static async ValueTask<StatefulFuzzMinimizationResult<TAction>> MinimizeWithReportAsync<TState, TAction>(
        StatefulFuzzCase<TAction> failingScenario,
        IStatefulFuzzTarget<TState, TAction> target,
        string failureFingerprint,
        Func<TAction, IEnumerable<TAction>>? shrinkAction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureFingerprint);
        var actions = failingScenario.Actions.ToList();
        var originalActionCount = actions.Count;
        var replayCount = 0;

        async ValueTask<bool> PreservesFailureAsync(IReadOnlyList<TAction> candidateActions)
        {
            var candidate = failingScenario with { Actions = candidateActions.ToArray() };
            var result = await StatefulFuzzRunner.RunAsync(candidate, target, cancellationToken);
            replayCount += 1;
            return string.Equals(
                result.FailureFingerprint,
                failureFingerprint,
                StringComparison.Ordinal);
        }

        // Delta-debug contiguous chunks first. Stateful failures commonly contain long stretches
        // of setup/noise, so removing blocks before individual actions substantially reduces replay cost.
        var granularity = 2;
        while (actions.Count >= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSize = (actions.Count + granularity - 1) / granularity;
            var reduced = false;

            for (var start = 0; start < actions.Count; start += chunkSize)
            {
                var end = Math.Min(start + chunkSize, actions.Count);
                var candidateActions = actions
                    .Take(start)
                    .Concat(actions.Skip(end))
                    .ToArray();

                if (!await PreservesFailureAsync(candidateActions))
                    continue;

                actions = candidateActions.ToList();
                granularity = Math.Max(2, granularity - 1);
                reduced = true;
                break;
            }

            if (reduced)
                continue;

            if (granularity >= actions.Count)
                break;

            granularity = Math.Min(actions.Count, granularity * 2);
        }

        // Once sequence shape is minimal, optionally shrink action payloads while preserving the
        // exact fingerprint. Targets can reduce IDs, counts, timestamps or text without teaching
        // the generic minimizer domain-specific semantics.
        if (shrinkAction is not null)
        {
            for (var index = 0; index < actions.Count; index += 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = actions[index];

                foreach (var variant in shrinkAction(current))
                {
                    if (EqualityComparer<TAction>.Default.Equals(current, variant))
                        continue;

                    var candidateActions = actions.ToArray();
                    candidateActions[index] = variant;
                    if (!await PreservesFailureAsync(candidateActions))
                        continue;

                    actions[index] = variant;
                    current = variant;
                }
            }
        }

        return new StatefulFuzzMinimizationResult<TAction>(
            failingScenario with { Actions = actions.ToArray() },
            replayCount,
            originalActionCount);
    }
}

/// <summary>
/// A tiny xorshift32 PRNG whose replay is stable across .NET runtime upgrades.
/// System.Random deliberately does not promise cross-version algorithm stability,
/// while fuzz reproducers need a seed to keep meaning the same thing later.
/// </summary>
internal sealed class StableFuzzRandom
{
    private uint state;

    public StableFuzzRandom(int seed)
    {
        state = unchecked((uint)seed);
        if (state == 0) state = 0x6D2B79F5u;
    }

    public uint NextUInt32()
    {
        var value = state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        state = value;
        return value;
    }

    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextUInt32() % (uint)maxExclusive);
    }

    public bool NextBool() => (NextUInt32() & 1u) == 1u;

    public T Pick<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("At least one value is required.", nameof(values));
        return values[NextInt(values.Count)];
    }
}

internal static class StatefulSequenceMutator
{
    public static IReadOnlyList<TAction> Mutate<TAction>(
        IReadOnlyList<TAction> seedActions,
        int mutationSeed,
        Func<StableFuzzRandom, TAction> createAction,
        int operationCount = 1)
    {
        ArgumentNullException.ThrowIfNull(seedActions);
        ArgumentNullException.ThrowIfNull(createAction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);

        var random = new StableFuzzRandom(mutationSeed);
        var actions = seedActions.ToList();

        for (var operation = 0; operation < operationCount; operation += 1)
        {
            if (actions.Count == 0)
            {
                actions.Add(createAction(random));
                continue;
            }

            switch (random.NextInt(5))
            {
                case 0:
                    actions.Insert(random.NextInt(actions.Count + 1), createAction(random));
                    break;
                case 1:
                    actions.RemoveAt(random.NextInt(actions.Count));
                    break;
                case 2:
                {
                    var index = random.NextInt(actions.Count);
                    actions.Insert(index, actions[index]);
                    break;
                }
                case 3 when actions.Count > 1:
                {
                    var first = random.NextInt(actions.Count);
                    var second = random.NextInt(actions.Count - 1);
                    if (second >= first) second += 1;
                    (actions[first], actions[second]) = (actions[second], actions[first]);
                    break;
                }
                default:
                    actions[random.NextInt(actions.Count)] = createAction(random);
                    break;
            }
        }

        return actions.ToArray();
    }
}

internal sealed record StatefulFuzzReproducer<TAction>(
    string Name,
    int Seed,
    string FailureFingerprint,
    TAction[] Actions);

internal static class StatefulFuzzReproducerSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize<TAction>(
        StatefulFuzzCase<TAction> scenario,
        string failureFingerprint) =>
        JsonSerializer.Serialize(
            new StatefulFuzzReproducer<TAction>(
                scenario.Name,
                scenario.Seed,
                failureFingerprint,
                scenario.Actions.ToArray()),
            JsonOptions);

    public static StatefulFuzzReproducer<TAction> Deserialize<TAction>(string json) =>
        JsonSerializer.Deserialize<StatefulFuzzReproducer<TAction>>(json, JsonOptions)
        ?? throw new InvalidOperationException("Invalid stateful fuzz reproducer JSON.");
}
