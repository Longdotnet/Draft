using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Carries the current Zalo connection through one async request flow so durable
/// ConversationState V2 rows cannot collide when provider group/user identifiers are
/// reused under another login connection.
/// </summary>
public static class ZaloConversationStateScope
{
    private static readonly AsyncLocal<string?> CurrentConnectionId = new();

    public static IDisposable Push(string? connectionId)
    {
        var previous = CurrentConnectionId.Value;
        CurrentConnectionId.Value = Clean(connectionId, 160);
        return new ScopeLease(previous);
    }

    public static string ScopeGroupId(string? groupId)
    {
        var logicalGroupId = Clean(groupId, 100);
        var connectionId = CurrentConnectionId.Value;
        if (logicalGroupId.Length == 0 || string.IsNullOrWhiteSpace(connectionId))
            return logicalGroupId;

        // Length framing prevents ambiguous concatenation before hashing. Keep the
        // physical key short enough for the existing GroupId persistence contract.
        var framed = $"{connectionId.Length}:{connectionId}{logicalGroupId.Length}:{logicalGroupId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(framed));
        return "scope:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private sealed class ScopeLease(string? previous) : IDisposable
    {
        private string? prior = previous;
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            CurrentConnectionId.Value = prior;
            prior = null;
            disposed = true;
        }
    }
}
