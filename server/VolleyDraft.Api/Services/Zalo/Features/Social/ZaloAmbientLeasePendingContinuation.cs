using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services.Zalo.Conversation;

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
/// Preview confirmations use an explicit allowlist, strong confirmation grammar and
/// successful-prompt provenance. Session-selection continuations are different: while
/// AutoDraft/Redraft/TeamImage is waiting for a session, only a cancellation or a
/// selector that resolves against the authoritative pending candidate sessions may
/// promote the turn. Unaddressed ambient text must be a standalone selector; a verified
/// reply to the bot is already explicit addressing and may use the richer canonical
/// selector grammar. TeamImage is read-only, but it still uses the same grounded
/// pending-session selector and provenance boundary as draft workflows.
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
        ZaloBotIntent.Redraft,
        ZaloBotIntent.TeamImage
    ];

    public async Task<ZaloAmbientLeasePendingContinuation?> TryResolveAsync(
        string connectionId,
        string groupId,
        string senderId,
        string? content,
        CancellationToken cancellationToken = default,
        bool explicitlyAddressedByReply = false)
    {
        connectionId = Clean(connectionId);
        groupId = Clean(groupId);
        senderId = Clean(senderId);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return null;

        var text = content ?? string.Empty;
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
            var isCancellation = IsNoMentionPendingCancellation(text);

            if (AllowedConfirmationPendingIntents.Contains(pendingIntent) &&
                (isCancellation || isStrongConfirmation) &&
                TryGetConfirmationPromptIntent(pendingIntent, out var promptIntent) &&
                await IsLatestPendingPromptReplyAsync(
                    state,
                    promptIntent,
                    connectionId,
                    groupId,
                    senderId,
                    cancellationToken))
            {
                return new ZaloAmbientLeasePendingContinuation(pendingIntent, isCancellation);
            }

            if (AllowedSessionSelectionPendingIntents.Contains(pendingIntent) &&
                await IsLatestPendingPromptReplyAsync(
                    state,
                    pendingIntent,
                    connectionId,
                    groupId,
                    senderId,
                    cancellationToken))
            {
                if (isCancellation)
                    return new ZaloAmbientLeasePendingContinuation(pendingIntent, IsCancellation: true);

                if (await ResolvesPendingSessionSelectorAsync(
                        state.PendingPayloadJson,
                        connectionId,
                        groupId,
                        text,
                        explicitlyAddressedByReply,
                        cancellationToken))
                {
                    return new ZaloAmbientLeasePendingContinuation(pendingIntent, IsCancellation: false);
                }
            }
        }

        // Cancellation of a V2 TeamPreference proposal remains a read-only advisor
        // operation. Only a strong affirmative phrase may be promoted toward the
        // existing TeamPreference confirmation handler.
        if (IsNoMentionPendingCancellation(text) || !isStrongConfirmation) return null;

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

    /// <summary>
    /// No-mention cancellation is deliberately narrower than the global natural-cancel
    /// grammar. A bare control can safely belong to the pending workflow, but text such
    /// as "huỷ reminder" or "huỷ pass" already carries another domain and must not be
    /// promoted merely because an old draft/session prompt is still active.
    /// </summary>
    public static bool IsNoMentionPendingCancellation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty)
            .Trim(' ', '.', '!', '?', ',', ';', ':');

        return normalized is
            "huy" or
            "cancel" or
            "thoi" or
            "bo qua" or
            "khong can nua" or
            "thoi khoi" or
            "thoi khoi di" or
            "khoi" or
            "khoi di" or
            "bo di" or
            "khong lam nua";
    }

    private static bool TryGetConfirmationPromptIntent(
        ZaloBotIntent pendingIntent,
        out ZaloBotIntent promptIntent)
    {
        promptIntent = pendingIntent switch
        {
            ZaloBotIntent.AutoDraftConfirm => ZaloBotIntent.AutoDraft,
            ZaloBotIntent.RedraftConfirm => ZaloBotIntent.Redraft,
            ZaloBotIntent.RebalanceTeamsConfirm => ZaloBotIntent.RebalanceTeams,
            _ => ZaloBotIntent.Unknown
        };
        return promptIntent != ZaloBotIntent.Unknown;
    }

    private async Task<bool> IsLatestPendingPromptReplyAsync(
        VolleyDraft.Api.Models.ZaloBotConversationState state,
        ZaloBotIntent expectedReplyIntent,
        string connectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        // A generic recent bot reply is not enough to resume an old selector or
        // confirmation. The latest successful reply for this sender/group must be the
        // prompt that created/refreshed this exact pending workflow. Otherwise a newer
        // unrelated conversation could hand "cn", "huỷ" or "xác nhận" back to stale
        // state and accidentally execute an old mutation.
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
                item.SelectedIntent,
                item.BotReplySentAt
            })
            .ToListAsync(cancellationToken);

        var latest = repliedRows
            .Where(item => item.BotReplySentAt is not null)
            .OrderByDescending(item => item.BotReplySentAt!.Value)
            .FirstOrDefault();
        if (latest is null ||
            !string.Equals(latest.SelectedIntent, expectedReplyIntent.ToString(), StringComparison.Ordinal))
            return false;

        // Pending state is saved before the clarification/confirmation prompt is sent.
        // Requiring the successful reply at or after that state update also prevents an
        // older same-intent reply from reviving newly-created pending state whose prompt
        // never reached Zalo.
        return latest.BotReplySentAt!.Value >= state.UpdatedAt;
    }

    private async Task<bool> ResolvesPendingSessionSelectorAsync(
        string? pendingPayloadJson,
        string connectionId,
        string groupId,
        string content,
        bool explicitlyAddressedByReply,
        CancellationToken cancellationToken)
    {
        // A verified reply to the bot is explicit addressing and can use the existing
        // natural resolver. Pure ambient text has no such ownership signal, so only a
        // standalone selector-shaped follow-up may wake the bot.
        if (!explicitlyAddressedByReply && !ZaloSessionResolver.LooksLikeStandaloneSelector(content))
            return false;

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
