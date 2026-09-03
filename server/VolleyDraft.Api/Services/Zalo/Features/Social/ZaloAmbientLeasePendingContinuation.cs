using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientLeasePendingContinuation(
    ZaloBotIntent PendingIntent,
    bool IsCancellation);

/// <summary>
/// Authorizes only a narrow no-mention continuation for a preview that is already
/// pending for the same Zalo connection, group and sender. The caller must separately
/// prove an active same-sender conversation lease. This policy never performs a
/// domain mutation itself. For an ambient TeamPreference proposal it may authorize
/// address promotion only when the proposal reply is also the sender's latest recent
/// successful bot turn; ZaloBotService still owns revalidation/mutation/idempotency.
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
        // The scope columns form a unique key, so at most one legacy row is loaded.
        var state = await db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderZaloUserId == senderId,
                cancellationToken);
        if (state is not null && state.ExpiresAt > DateTimeOffset.UtcNow &&
            Enum.TryParse<ZaloBotIntent>(state.PendingIntent, out var pendingIntent) &&
            AllowedPendingIntents.Contains(pendingIntent))
        {
            return new ZaloAmbientLeasePendingContinuation(pendingIntent, isCancellation);
        }

        // Cancellation of a V2 TeamPreference proposal remains a read-only advisor
        // operation. Only a strong affirmative phrase may be promoted toward the
        // existing TeamPreference confirmation handler.
        if (isCancellation) return null;

        var proposal = await new ZaloConversationStateV2Store(db)
            .LoadActiveAsync(groupId, senderId, cancellationToken);
        if (proposal is null ||
            !string.Equals(
                proposal.Intent,
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                StringComparison.Ordinal))
            return null;

        var proposalSourceMessageId = Clean(proposal.LastMessageId);
        if (proposalSourceMessageId.Length == 0) return null;

        // A generic lease is not enough mutation authority. The proposal source must
        // have actually received a bot reply, must still be recent, and that reply must
        // be the latest successful bot turn for this exact sender/group. This prevents
        // a later unrelated conversation from making a stale "xác nhận" actionable.
        var repliedRows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderId == senderId &&
                !item.IsFromBot &&
                item.BotReplySentAt != null)
            .Select(item => new
            {
                item.MessageId,
                item.BotReplySentAt
            })
            .ToListAsync(cancellationToken);
        var latest = repliedRows
            .Where(item => item.BotReplySentAt is not null)
            .OrderByDescending(item => item.BotReplySentAt!.Value)
            .FirstOrDefault();
        if (latest is null ||
            !string.Equals(latest.MessageId, proposalSourceMessageId, StringComparison.Ordinal) ||
            latest.BotReplySentAt!.Value < DateTimeOffset.UtcNow.AddSeconds(-180))
            return null;

        return new ZaloAmbientLeasePendingContinuation(
            ZaloBotIntent.TeamPreferenceConfirm,
            IsCancellation: false);
    }

    public static bool IsStrongConfirmation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        if (normalized is
            "xac nhan" or
            "xac nhan draft" or
            "xac nhan draft lai" or
            "xac nhan can bang" or
            "xac nhan can bang team")
            return true;

        // Natural politeness suffixes are still an explicit confirmation. Generic
        // acknowledgements such as "ok", "được", "chốt" remain intentionally out.
        return Regex.IsMatch(
            normalized,
            @"^xac\s+nhan(?:\s+(?:nha|nhe|nhen|luon|giup\s+tui|giup\s+toi))?$",
            RegexOptions.CultureInvariant);
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
