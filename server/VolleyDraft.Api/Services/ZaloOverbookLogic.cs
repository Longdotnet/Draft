namespace VolleyDraft.Api.Services;

internal sealed record OverbookEffectiveUnit(string Key, IReadOnlyList<string> VoterIds);

internal sealed record OverbookCapacityEvaluation(
    int EffectiveSlotCount,
    int ExcessSlotCount,
    int ReservedSlotCount,
    bool CanResolveFromPoll,
    IReadOnlyList<OverbookEffectiveUnit> OrderedPollUnits,
    IReadOnlyList<string> SuggestedTargetIds);

internal static class ZaloOverbookLogic
{
    public static bool IsTrustedOrderTransition(
        IReadOnlyList<string> previous,
        IReadOnlyList<string> current,
        string? actorId)
    {
        if (previous.Count == 0) return false;
        if (previous.SequenceEqual(current, StringComparer.Ordinal)) return false;

        var previousSet = previous.ToHashSet(StringComparer.Ordinal);
        var currentSet = current.ToHashSet(StringComparer.Ordinal);
        var previousRetained = previous.Where(currentSet.Contains).ToList();
        var currentRetained = current.Where(previousSet.Contains).ToList();

        var normalizedActor = NormalizeId(actorId);
        if (normalizedActor.Length > 0 &&
            previousSet.Contains(normalizedActor) &&
            currentSet.Contains(normalizedActor) &&
            current.Count > 0 &&
            current[^1] == normalizedActor)
        {
            var previousWithoutActor = previousRetained.Where(id => id != normalizedActor).ToList();
            var currentWithoutActor = currentRetained.Where(id => id != normalizedActor).ToList();
            if (previousWithoutActor.SequenceEqual(currentWithoutActor, StringComparer.Ordinal))
                return true;
        }

        if (!previousRetained.SequenceEqual(currentRetained, StringComparer.Ordinal)) return false;

        var newIds = current.Where(id => !previousSet.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (newIds.Count == 0) return true;
        if (currentRetained.Count == 0) return false;

        var lastRetainedIndex = -1;
        for (var index = 0; index < current.Count; index += 1)
        {
            if (previousSet.Contains(current[index])) lastRetainedIndex = index;
        }
        if (lastRetainedIndex < 0) return false;
        return current.Skip(lastRetainedIndex + 1).All(newIds.Contains);
    }

    public static OverbookCapacityEvaluation EvaluateCapacity(
        IReadOnlyList<string> orderedVoters,
        int capacity,
        int reservedSlotCount,
        IReadOnlyDictionary<string, string> sharedSlotByVoter)
    {
        var unitMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var voterId in orderedVoters)
        {
            var key = sharedSlotByVoter.TryGetValue(voterId, out var slotId)
                ? $"shared:{slotId}"
                : $"voter:{voterId}";
            if (!unitMembers.TryGetValue(key, out var members))
            {
                members = [];
                unitMembers[key] = members;
            }
            if (!members.Contains(voterId, StringComparer.Ordinal)) members.Add(voterId);
        }

        var units = new List<OverbookEffectiveUnit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voterId in orderedVoters)
        {
            var key = sharedSlotByVoter.TryGetValue(voterId, out var slotId)
                ? $"shared:{slotId}"
                : $"voter:{voterId}";
            if (!seen.Add(key)) continue;
            units.Add(new OverbookEffectiveUnit(key, unitMembers[key]));
        }

        var effectiveSlotCount = Math.Max(0, reservedSlotCount) + units.Count;
        var excessSlotCount = Math.Max(0, effectiveSlotCount - Math.Max(0, capacity));
        var canResolveFromPoll = excessSlotCount <= units.Count;
        var pollExcessUnits = Math.Min(excessSlotCount, units.Count);
        var suggested = pollExcessUnits == 0
            ? []
            : units.Skip(units.Count - pollExcessUnits).SelectMany(unit => unit.VoterIds).ToList();

        return new OverbookCapacityEvaluation(
            effectiveSlotCount,
            excessSlotCount,
            Math.Max(0, reservedSlotCount),
            canResolveFromPoll,
            units,
            suggested);
    }

    public static string NormalizeId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}
