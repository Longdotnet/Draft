using System.Collections.Concurrent;

namespace VolleyDraft.Api.Services;

public sealed class ZaloQrLoginRegistry
{
    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public void Register(string loginId, string adminUserId, DateTimeOffset expiresAt)
    {
        Cleanup();
        entries[loginId] = new Entry(adminUserId, expiresAt);
    }

    public bool IsOwnedBy(string loginId, string adminUserId)
    {
        Cleanup();
        return entries.TryGetValue(loginId, out var entry)
            && entry.AdminUserId == adminUserId
            && entry.ExpiresAt >= DateTimeOffset.UtcNow;
    }

    public void Complete(string loginId) => entries.TryRemove(loginId, out _);

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries.Where(item => item.Value.ExpiresAt < now))
        {
            entries.TryRemove(entry.Key, out _);
        }
    }

    private sealed record Entry(string AdminUserId, DateTimeOffset ExpiresAt);
}
