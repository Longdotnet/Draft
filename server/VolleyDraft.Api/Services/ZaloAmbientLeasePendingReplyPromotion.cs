using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Verifies a no-mention reply before it may enter an existing legacy pending-action
/// handler. Authorization is derived from stable sender/group identity plus an exact
/// provider message-id match to the bot preview that created the pending action.
/// Quoted text is never trusted as authority.
/// </summary>
public sealed class ZaloAmbientLeasePendingReplyPromotion(VolleyDraftDbContext db)
{
    private static readonly HashSet<string> AllowedPendingIntents = new(StringComparer.Ordinal)
    {
        ZaloBotIntent.AutoDraftConfirm.ToString(),
        ZaloBotIntent.RedraftConfirm.ToString(),
        ZaloBotIntent.RebalanceTeamsConfirm.ToString()
    };

    public async Task<ZaloIncomingMessageEvent?> TryPromoteAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (incoming.MentionedBot || !IsExplicitPendingReply(incoming.Content))
            return null;

        connectionId = Clean(connectionId, 100);
        groupId = Clean(groupId, 100);
        var senderId = Clean(incoming.SenderId, 100);
        var botId = Clean(incoming.BotId, 100);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || botId.Length == 0)
            return null;

        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        if (!quote.RepliesToBot || string.IsNullOrWhiteSpace(quote.MessageId))
            return null;

        var now = DateTimeOffset.UtcNow;
        var pendingRows = await db.ZaloBotConversationStates
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           item.SenderZaloUserId == senderId)
            .ToListAsync(cancellationToken);
        var pending = pendingRows
            .Where(item => item.ExpiresAt > now && AllowedPendingIntents.Contains(item.PendingIntent))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (pending is null) return null;

        var sourceIntents = SourceIntents(pending.PendingIntent);
        if (sourceIntents.Count == 0) return null;

        var sourceRows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           item.SenderId == senderId &&
                           !item.IsFromBot &&
                           item.BotReplySentAt != null)
            .Select(item => new
            {
                item.MessageId,
                item.SelectedIntent,
                item.BotReplySentAt
            })
            .ToListAsync(cancellationToken);

        // The pending state is saved immediately before the preview is sent. Bind to
        // the latest matching source message whose bot reply was emitted around that
        // state update. Temporal checks stay in memory for SQLite/PostgreSQL parity.
        var earliest = pending.UpdatedAt.AddSeconds(-10);
        var latest = pending.UpdatedAt.AddMinutes(2);
        var source = sourceRows
            .Where(item => item.BotReplySentAt is { } repliedAt &&
                           repliedAt >= earliest && repliedAt <= latest &&
                           item.SelectedIntent is not null &&
                           sourceIntents.Contains(item.SelectedIntent))
            .OrderByDescending(item => item.BotReplySentAt)
            .FirstOrDefault();
        if (source is null) return null;

        var providerReplyId = await new ZaloMessageGraphQuery(db)
            .LoadBotReplyMessageIdAsync(
                connectionId,
                groupId,
                source.MessageId,
                cancellationToken);
        if (string.IsNullOrWhiteSpace(providerReplyId) ||
            !string.Equals(providerReplyId, quote.MessageId, StringComparison.Ordinal))
            return null;

        return incoming with
        {
            MentionedBot = true,
            Mentions = [new ZaloBridgeMention(botId, 0, 0)]
        };
    }

    public static bool IsExplicitPendingReply(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized is
            "xac nhan" or
            "xac nhan draft" or
            "xac nhan draft lai" or
            "confirm" or
            "huy" or
            "huy di" or
            "bo qua";
    }

    private static HashSet<string> SourceIntents(string pendingIntent)
    {
        if (string.Equals(pendingIntent, ZaloBotIntent.AutoDraftConfirm.ToString(), StringComparison.Ordinal))
            return new HashSet<string>([ZaloBotIntent.AutoDraft.ToString()], StringComparer.Ordinal);
        if (string.Equals(pendingIntent, ZaloBotIntent.RedraftConfirm.ToString(), StringComparison.Ordinal))
            return new HashSet<string>([ZaloBotIntent.AutoDraft.ToString(), ZaloBotIntent.Redraft.ToString()], StringComparer.Ordinal);
        if (string.Equals(pendingIntent, ZaloBotIntent.RebalanceTeamsConfirm.ToString(), StringComparison.Ordinal))
            return new HashSet<string>([ZaloBotIntent.RebalanceTeams.ToString()], StringComparer.Ordinal);
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
