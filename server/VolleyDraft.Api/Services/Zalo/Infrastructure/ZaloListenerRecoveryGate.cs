using System.Collections.Concurrent;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Process-local gate that turns a listener generation change into one durable recovery
/// queue request per tracked group. The gate intentionally starts empty after an API
/// restart, so the first successful listener reconciliation schedules recovery even when
/// the bridge listener itself survived. QueueGroupAsync persists the actual work in the
/// database; this gate only prevents the five-minute listener safety loop from turning
/// recovery into permanent provider polling.
/// </summary>
internal sealed class ZaloListenerRecoveryGate
{
    private readonly record struct ScopeKey(string AccountId, string ConnectionId, string GroupId);
    private readonly ConcurrentDictionary<ScopeKey, long> queuedGenerations = new();

    internal bool TryBegin(
        string accountIdValue,
        string connectionIdValue,
        string groupIdValue,
        long listenerStartedAt)
    {
        var key = new ScopeKey(
            Normalize(accountIdValue),
            Normalize(connectionIdValue),
            Normalize(groupIdValue));
        if (key.AccountId.Length == 0 || key.ConnectionId.Length == 0 || key.GroupId.Length == 0)
            return false;

        while (true)
        {
            if (queuedGenerations.TryGetValue(key, out var existing))
            {
                if (existing == listenerStartedAt) return false;
                if (queuedGenerations.TryUpdate(key, listenerStartedAt, existing)) return true;
                continue;
            }

            if (queuedGenerations.TryAdd(key, listenerStartedAt)) return true;
        }
    }

    internal void Release(
        string accountIdValue,
        string connectionIdValue,
        string groupIdValue,
        long listenerStartedAt)
    {
        var key = new ScopeKey(
            Normalize(accountIdValue),
            Normalize(connectionIdValue),
            Normalize(groupIdValue));
        if (queuedGenerations.TryGetValue(key, out var existing) && existing == listenerStartedAt)
            queuedGenerations.TryRemove(key, out _);
    }

    private static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}
