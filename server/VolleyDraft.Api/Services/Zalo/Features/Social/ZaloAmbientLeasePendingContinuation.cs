using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientLeasePendingContinuation(
    ZaloBotIntent PendingIntent,
    bool IsCancellation);

/// <summary>
/// Authorizes only a narrow no-mention continuation for a workflow that is already
/// pending for the same Zalo connection, group and sender. The caller must separately
/// prove an active same-sender conversation lease. This policy never performs a
/// domain mutation itself.
///
/// Preview confirmations use an explicit allowlist and strong confirmation grammar.
/// Draft session-selection continuations are different: while AutoDraft/Redraft is
/// waiting for a session, only a cancellation or a selector that resolves against the
/// authoritative pending candidate sessions may promote the turn. This keeps short
/// follow-ups such as "cn" or "13/9" usable without making ordinary ambient chat an
/// implicit bot address.
/// </summary>
public sealed class ZaloAmbientLeasePendingContinuationPolicy(VolleyDraftDbContext db)
{
    private static readonly HashSet<ZaloBotIntent> AllowedConfirmationPendingIntents =
    [
        ZaloBotIntent.AutoDraftConfirm,
        ZaloBotIntent.RedraftConfirm,
        ZaloBotIntent.RebalanceTeamsConfirm
    ];

    private static readonly HashSet<ZaloBotIntent> AllowedSessionSelectionPendingIntents =
    [
        ZaloBotIntent.AutoDraft,
        ZaloBotIntent.Redraft
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

        var text = content ?? string.Empty;
        var isCancellation = ZaloBotIntelligence.IsCancel(text);
        var isStrongConfirmation = IsStrongConfirmation(text);

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
            Enum.TryParse<ZaloBotIntent>(state.PendingIntent, out var pendingIntent))
        {
            if (AllowedConfirmationPendingIntents.Contains(pendingIntent) &&
                (isCancellation || isStrongConfirmation))
            {
                return new ZaloAmbientLeasePendingContinuation(pendingIntent, isCancellation);
            }

            if (AllowedSessionSelectionPendingIntents.Contains(pendingIntent))
            {
                if (isCancellation)
                    return new ZaloAmbientLeasePendingContinuation(pendingIntent, IsCancellation: true);

                if (await ResolvesPendingSessionSelectorAsync(
                        state.PendingPayloadJson,
                        connectionId,
                        groupId,
                        text,
                        cancellationToken))
                {
                    return new ZaloAmbientLeasePendingContinuation(pendingIntent, IsCancellation: false);
                }
            }
        }

        // Cancellation of a V2 TeamPreference proposal remains a read-only advisor
        // operation. Only a strong affirmative phrase may be promoted toward the
        // existing TeamPreference confirmation handler.
        if (isCancellation || !isStrongConfirmation) return null;

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

    private async Task<bool> ResolvesPendingSessionSelectorAsync(
        string? pendingPayloadJson,
        string connectionId,
        string groupId,
        string content,
        CancellationToken cancellationToken)
    {
        List<string> candidateIds;
        try
        {
            candidateIds = JsonSerializer.Deserialize<List<string>>(pendingPayloadJson ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return false;
        }

        candidateIds = candidateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        if (candidateIds.Count == 0) return false;

        // Bind the selector only to sessions that still belong to this exact tracked
        // connection/group. The legacy pending payload contributes candidate IDs, but
        // it is not trusted as cross-group authority by itself.
        var candidates = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                candidateIds.Contains(session.Id) &&
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId)
            .Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return false;

        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(content, candidates);
        return matchedIds.Count > 0;
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
