using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Continues a narrow allow-list of already-pending legacy workflows without
    /// requiring another textual @Npc mention. This is deterministic addressing
    /// context, not ambient participation: the existing bot router still owns pending
    /// resolution, authorization, authoritative revalidation, mutation and idempotency.
    /// </summary>
    public async Task<bool> TryHandleLegacyPendingContinuationPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (botService is null || incoming.MentionedBot)
            return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || botId.Length == 0)
            return false;

        // A reply to another member is explicit human-to-human context. Never steal it
        // merely because this sender also has a recent bot conversation.
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        if (quote.HasQuote && !quote.RepliesToBot)
            return false;

        // Resolve the same live account/group ownership used by the legacy bot lane.
        // DateTimeOffset ordering stays in memory for SQLite/PostgreSQL parity.
        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session =>
                               session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new { item.Id, item.UpdatedAt })
            .ToListAsync(cancellationToken);
        var connectionId = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;

        // A pending row alone is not enough addressing authority. Require a recent
        // successful reply to this same sender/group, then let the narrow policy prove
        // that the current text really continues that pending workflow.
        var hasLease = await new ZaloAmbientConversationLeaseResolver(db)
            .IsActiveAsync(connectionId, groupId, senderId, 180, cancellationToken);
        if (!hasLease)
            return false;

        var continuation = await new ZaloAmbientLeasePendingContinuationPolicy(db)
            .TryResolveAsync(connectionId, groupId, senderId, incoming.Content, cancellationToken);
        if (continuation is null)
            return false;

        // Promote address metadata only. Preserve the original sender/group/message
        // identity so the legacy router applies its normal authorization and durable
        // duplicate-delivery protections.
        var promoted = incoming with
        {
            MentionedBot = true,
            Mentions = [new ZaloBridgeMention(botId, 0, 0)]
        };

        logger.LogInformation(
            "Continued trusted pending Zalo workflow without re-mention Group={GroupId} Sender={SenderId} Message={MessageId} PendingIntent={PendingIntent} Cancel={Cancel}",
            groupId,
            senderId,
            incoming.MessageId,
            continuation.PendingIntent,
            continuation.IsCancellation);

        await botService.HandleIncomingAsync(promoted, cancellationToken);
        return true;
    }
}
