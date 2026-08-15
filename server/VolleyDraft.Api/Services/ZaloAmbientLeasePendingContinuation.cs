using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientLeasePendingContinuation(
    ZaloBotIntent PendingIntent,
    bool IsCancellation);

/// <summary>
/// Authorizes only a narrow no-mention continuation for a preview that is already
/// pending for the same Zalo connection, group and sender. The caller must separately
/// prove an active same-sender conversation lease. This policy never creates pending
/// state and never performs a domain mutation itself.
/// </summary>
public sealed class ZaloAmbientLeasePendingContinuationPolicy(VolleyDraftDbContext db)
{
    private static readonly HashSet<ZaloBotIntent> AllowedPendingIntents =
    [
        ZaloBotIntent.AutoDraftConfirm,
        ZaloBotIntent.RedraftConfirm,
        ZaloBotIntent.RebalanceTeamsConfirm
    ];

    public async Task<ZaloAmbientLeasePendingContinuation?> TryResolveAsync(
        string connectionId,
        string groupId,
        string senderId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId);
        groupId = Clean(groupId);
        senderId = Clean(senderId);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return null;

        var isCancellation = ZaloBotIntelligence.IsCancel(content ?? string.Empty);
        if (!isCancellation && !IsStrongConfirmation(content))
            return null;

        // Keep DateTimeOffset comparison in memory for SQLite/PostgreSQL parity.
        // The scope columns form a unique key, so at most one row is loaded.
        var state = await db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderZaloUserId == senderId,
                cancellationToken);
        if (state is null || state.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        if (!Enum.TryParse<ZaloBotIntent>(state.PendingIntent, out var pendingIntent) ||
            !AllowedPendingIntents.Contains(pendingIntent))
            return null;

        return new ZaloAmbientLeasePendingContinuation(pendingIntent, isCancellation);
    }

    public static bool IsStrongConfirmation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized is
            "xac nhan" or
            "xac nhan draft" or
            "xac nhan draft lai" or
            "xac nhan can bang" or
            "xac nhan can bang team";
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
