namespace VolleyDraft.Api.Services;

/// <summary>
/// Process-local sliding-window budget shared by ambient AI features. The caller
/// supplies the global ZaloBot per-user/per-group limits so independent semantic
/// features cannot each consume a full duplicate budget.
/// </summary>
internal static class ZaloAiBudgetLimiter
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<DateTimeOffset>> UserCalls = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Queue<DateTimeOffset>> GroupCalls = new(StringComparer.Ordinal);

    public static bool TryAcquire(
        string connectionId,
        string groupId,
        string senderId,
        int maxUserCallsPerMinute,
        int maxGroupCallsPerMinute)
    {
        connectionId = Normalize(connectionId);
        groupId = Normalize(groupId);
        senderId = Normalize(senderId);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return false;

        maxUserCallsPerMinute = Math.Clamp(maxUserCallsPerMinute, 1, 100);
        maxGroupCallsPerMinute = Math.Clamp(maxGroupCallsPerMinute, 1, 500);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
        var groupKey = $"{connectionId}:{groupId}";
        var userKey = $"{groupKey}:{senderId}";

        lock (Gate)
        {
            var userQueue = GetQueue(UserCalls, userKey);
            var groupQueue = GetQueue(GroupCalls, groupKey);
            Prune(userQueue, cutoff);
            Prune(groupQueue, cutoff);
            if (userQueue.Count >= maxUserCallsPerMinute || groupQueue.Count >= maxGroupCallsPerMinute)
                return false;

            userQueue.Enqueue(now);
            groupQueue.Enqueue(now);
            return true;
        }
    }

    private static Queue<DateTimeOffset> GetQueue(
        IDictionary<string, Queue<DateTimeOffset>> map,
        string key)
    {
        if (map.TryGetValue(key, out var queue)) return queue;
        queue = new Queue<DateTimeOffset>();
        map[key] = queue;
        return queue;
    }

    private static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
    {
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
