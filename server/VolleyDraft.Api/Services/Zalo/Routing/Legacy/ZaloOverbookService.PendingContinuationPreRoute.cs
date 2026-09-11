using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

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
        // merely because this sender also has a recent bot conversation. A verified
        // reply to the bot, on the other hand, is already explicit addressing and may
        // use richer natural selector wording than a purely ambient no-mention turn.
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

        // This pre-route executes before ZaloBotService enters its normal connection
        // scope. ConversationState V2 is physically scoped by connection, so every V2
        // lookup performed while proving continuation ownership must use the resolved
        // provider connection instead of falling back to the legacy unscoped key.
        using var conversationStateScope = ZaloConversationStateScope.Push(connectionId);

        // A destructive confirmation expiring must revoke mutation authority immediately,
        // but the exact provider reply may still be useful as bounded conversation context.
        // Convert that old confirmation into a fresh deterministic command 9 turn. The
        // current bot router then re-reads session/roster/pass/share state and can only issue
        // a NEW confirmation; this incoming message can never execute the expired authority.
        if (quote.RepliesToBot &&
            !string.IsNullOrWhiteSpace(quote.MessageId) &&
            ZaloAmbientLeasePendingContinuationPolicy.IsStrongConfirmation(incoming.Content))
        {
            var expiredPending = await db.ZaloBotConversationStates
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    item.SenderZaloUserId == senderId,
                    cancellationToken);

            if (expiredPending is not null && expiredPending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                var quotedBotRelation = await new ZaloMessageGraphStore(db)
                    .LoadRelationAsync(connectionId, groupId, quote.MessageId, cancellationToken);
                ZaloGroupMessage? promptSource = null;
                if (!string.IsNullOrWhiteSpace(quotedBotRelation?.ToMessageId))
                {
                    promptSource = await db.ZaloGroupMessages
                        .AsNoTracking()
                        .SingleOrDefaultAsync(item =>
                            item.ZaloConnectionId == connectionId &&
                            item.GroupId == groupId &&
                            item.MessageId == quotedBotRelation.ToMessageId,
                            cancellationToken);
                }

                var recoveredSessionId = ZaloExpiredDraftConfirmationRecoveryPolicy.ResolveSessionId(
                    expiredPending,
                    quote,
                    quotedBotRelation,
                    promptSource,
                    DateTimeOffset.UtcNow);
                if (!string.IsNullOrWhiteSpace(recoveredSessionId))
                {
                    var session = await db.MatchSessions
                        .AsNoTracking()
                        .Where(item =>
                            item.Id == recoveredSessionId &&
                            item.ZaloConnectionId == connectionId &&
                            item.ZaloGroupId == groupId &&
                            item.BotEnabled &&
                            item.Status != SessionStatus.Cancelled)
                        .Select(item => new { item.Id, item.Name })
                        .SingleOrDefaultAsync(cancellationToken);
                    if (session is not null)
                    {
                        var refresh = incoming with
                        {
                            Content = $"9 {session.Name}",
                            MentionedBot = true,
                            Mentions = [new ZaloBridgeMention(botId, 0, 0)]
                        };

                        logger.LogInformation(
                            "Recovered expired draft confirmation as fresh deterministic readiness turn Group={GroupId} Sender={SenderId} Message={MessageId} Session={SessionId} QuotedBotMessage={QuotedBotMessage}",
                            groupId,
                            senderId,
                            incoming.MessageId,
                            session.Id,
                            quote.MessageId);

                        await botService.HandleIncomingAsync(refresh, cancellationToken);
                        return true;
                    }
                }
            }
        }

        // A recent bot-conversation lease remains the ordinary no-mention addressing
        // signal. A direct quote/reply can outlive that lease only when the provider
        // message graph proves that the quoted bot reply came from this sender's still-
        // active durable DraftReadinessSessionChoice task. This is context recovery,
        // never mutation authority: the legacy router still revalidates all domain state.
        var quoteAnchoredTask = false;
        if (quote.RepliesToBot && !string.IsNullOrWhiteSpace(quote.MessageId))
        {
            quoteAnchoredTask = await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                db,
                connectionId,
                groupId,
                senderId,
                quote,
                cancellationToken);
        }

        // A pending row alone is not enough addressing authority. Require either a
        // recent successful reply lease or the exact provider-message task anchor above,
        // then let the narrow policy prove that the text really continues the workflow.
        var hasLease = await new ZaloAmbientConversationLeaseResolver(db)
            .IsActiveAsync(connectionId, groupId, senderId, 180, cancellationToken);
        if (!hasLease && !quoteAnchoredTask)
            return false;

        var continuation = await new ZaloAmbientLeasePendingContinuationPolicy(db)
            .TryResolveAsync(
                connectionId,
                groupId,
                senderId,
                incoming.Content,
                cancellationToken,
                explicitlyAddressedByReply: quote.RepliesToBot);
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
            "Continued trusted pending Zalo workflow without re-mention Group={GroupId} Sender={SenderId} Message={MessageId} PendingIntent={PendingIntent} Cancel={Cancel} QuoteAnchoredTask={QuoteAnchoredTask}",
            groupId,
            senderId,
            incoming.MessageId,
            continuation.PendingIntent,
            continuation.IsCancellation,
            quoteAnchoredTask);

        await botService.HandleIncomingAsync(promoted, cancellationToken);
        return true;
    }
}
