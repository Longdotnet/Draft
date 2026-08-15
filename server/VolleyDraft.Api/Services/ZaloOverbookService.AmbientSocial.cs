using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleAmbientSocialAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        // Final mutation confirmation may continue without a native @mention only
        // when the user replies to the exact provider bot message that created an
        // allowed pending preview. The verifier checks sender/group/pending state and
        // message-graph identity before the legacy router sees a synthetic mention.
        if (!ambientSettings.ShadowMode && botService is not null)
        {
            var promotedPendingReply = await new ZaloAmbientLeasePendingReplyPromotion(db)
                .TryPromoteAsync(connectionId, groupId, incoming, cancellationToken);
            if (promotedPendingReply is not null)
            {
                logger.LogInformation(
                    "Ambient exact-reply promoted pending action Group={GroupId} Sender={SenderId} Message={MessageId}",
                    groupId,
                    senderId,
                    incoming.MessageId);
                await botService.HandleIncomingAsync(promotedPendingReply, cancellationToken);
                return true;
            }
        }

        // A live same-sender conversation lease can promote only preview-first draft
        // operations into the existing deterministic bot router. This happens before
        // Social AI and never treats the lease itself as confirmation authority.
        if (await TryHandleAmbientLeaseActionPreviewAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                ambientSettings,
                cancellationToken))
            return true;

        var socialSettings = ZaloAmbientSocialPilotSettings.FromConfiguration(configuration);
        if (!socialSettings.Enabled) return false;

        var social = await new ZaloAmbientSocialResponder(db, configuration, logger)
            .TryBuildAsync(
                connectionId,
                groupId,
                incoming,
                decision,
                socialSettings,
                cancellationToken);
        if (social is null) return false;

        try
        {
            await new ZaloBotTraceStore(db).WriteAsync(new ZaloBotTraceEntry(
                MessageId: ZaloOverbookLogic.NormalizeId(incoming.MessageId),
                GroupId: groupId,
                SenderZaloUserId: senderId,
                AddressReason: "AmbientSocialCandidate",
                IntentSource: "AmbientSocialShadow",
                Intent: ZaloBotIntent.GeneralChat.ToString(),
                Confidence: social.EffectiveScore / 100d,
                ContextMessageIdsJson: JsonSerializer.Serialize(decision.Situation.RecentMessageIds.Take(12)),
                AiCalled: true,
                FallbackReason: $"candidate_generated|{social.AddressReason}|send_enabled:{socialSettings.SendEnabled}|shadow_mode:{ambientSettings.ShadowMode}"),
                cancellationToken);
        }
        catch (Exception traceException)
        {
            // Candidate tracing is observational only. Failure must not change normal
            // routing or turn a future successful provider send into a retry loop.
            logger.LogWarning(
                traceException,
                "Ambient social candidate trace failed Group={GroupId} Message={MessageId}",
                groupId,
                incoming.MessageId);
        }

        // Social AI uses a stricter triple gate than Fact:
        // SocialPilot.Enabled => candidate generation only.
        // SocialPilot.SendEnabled + ShadowMode=false => user-visible send.
        if (ambientSettings.ShadowMode || !socialSettings.SendEnabled)
            return true;

        await TrySendAmbientSocialAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            decision,
            social,
            cancellationToken);
        return true;
    }

    private async Task<bool> TryHandleAmbientLeaseActionPreviewAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        if (ambientSettings.ShadowMode || botService is null || incoming.MentionedBot)
            return false;

        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        if (quote.HasQuote && !quote.RepliesToBot) return false;

        var hasLease = await new ZaloAmbientConversationLeaseResolver(db)
            .IsActiveAsync(connectionId, groupId, senderId, 180, cancellationToken);
        if (!hasLease) return false;

        var promotion = ZaloAmbientLeaseActionPromotionPolicy.TryCreate(incoming.Content);
        if (promotion is null) return false;

        var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        if (botId.Length == 0) return false;

        // Internal promotion supplies only the address metadata expected by the
        // existing router. The original sender/group/message identity is preserved,
        // so authorization, operator checks, pending confirmation and idempotency all
        // remain in ZaloBotService. This call may create a preview/pending state, but
        // it does not fabricate a confirmation turn.
        var promoted = incoming with
        {
            Content = promotion.PromotedContent,
            MentionedBot = true,
            Mentions = [new ZaloBridgeMention(botId, 0, 0)]
        };

        logger.LogInformation(
            "Ambient lease promoted preview-safe action Group={GroupId} Sender={SenderId} Message={MessageId} Intent={Intent}",
            groupId,
            senderId,
            incoming.MessageId,
            promotion.Intent);
        await botService.HandleIncomingAsync(promoted, cancellationToken);
        return true;
    }

    private async Task TrySendAmbientSocialAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        ZaloAmbientSocialReply social,
        CancellationToken cancellationToken)
    {
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        var observed = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId && item.MessageId == messageId,
                cancellationToken);
        if (observed is null || observed.BotReplySentAt is not null) return;

        if (string.Equals(observed.ReplyOutcome, "ambient_social_processing", StringComparison.Ordinal) &&
            observed.ProcessingStartedAt is { } startedAt &&
            startedAt >= DateTimeOffset.UtcNow.AddMinutes(-2))
            return;
        if (observed.ReplyOutcome is not null &&
            observed.ReplyOutcome is not "ambient_social_processing" and not "ambient_social_send_failed")
            return;

        var previousAttemptCount = observed.ReplyAttemptCount;
        var processingToken = $"ambient-social:{Guid.NewGuid():n}";
        var claimStartedAt = DateTimeOffset.UtcNow;
        var claimed = await db.ZaloGroupMessages
            .Where(item => item.Id == observed.Id &&
                           item.BotReplySentAt == null &&
                           item.ReplyAttemptCount == previousAttemptCount)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessingStartedAt, claimStartedAt)
                .SetProperty(item => item.ProcessingToken, processingToken)
                .SetProperty(item => item.ReplyAttemptCount, item => item.ReplyAttemptCount + 1)
                .SetProperty(item => item.ReplyOutcome, "ambient_social_processing"),
                cancellationToken);
        if (claimed == 0) return;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var idempotencyKey = $"ambient-social:{accountId}:{messageId}";
        BridgeSendMessageResponse send;
        try
        {
            send = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                social.Text,
                [],
                idempotencyKey: idempotencyKey);
            if (!send.Sent)
                throw new InvalidOperationException("Zalo bridge did not confirm ambient social send.");
        }
        catch (Exception sendException)
        {
            await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == connectionId &&
                               item.MessageId == messageId &&
                               item.ProcessingToken == processingToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.ProcessingToken, (string?)null)
                    .SetProperty(item => item.ReplyOutcome, "ambient_social_send_failed"),
                    cancellationToken);
            logger.LogWarning(
                sendException,
                "Could not send ambient Social reply Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
            return;
        }

        var providerReplyId = NormalizeProviderMessageId(send.MessageId);
        var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
        var botName = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.Id == connectionId)
            .Select(item => item.DisplayName)
            .SingleOrDefaultAsync(cancellationToken)
            ?? "Volley Bot";

        try
        {
            await EnsureV2OutboundMessageAsync(
                connectionId,
                groupId,
                persistedReplyId,
                accountId,
                botName,
                social.Text,
                cancellationToken);
            if (providerReplyId is not null)
            {
                await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                    connectionId,
                    groupId,
                    providerReplyId,
                    messageId,
                    cancellationToken);
            }
        }
        catch (Exception persistenceException)
        {
            // Provider already accepted the message. Never make persistence failure
            // look retryable from this invocation.
            logger.LogWarning(
                persistenceException,
                "Ambient social sent but outbound persistence failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
        }

        try
        {
            await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == connectionId &&
                               item.MessageId == messageId &&
                               item.ProcessingToken == processingToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.BotReplySentAt, DateTimeOffset.UtcNow)
                    .SetProperty(item => item.SelectedIntent, ZaloBotIntent.GeneralChat.ToString())
                    .SetProperty(item => item.AiCalled, true)
                    .SetProperty(item => item.ReplyOutcome, "ambient_social_sent")
                    .SetProperty(item => item.ProcessingToken, (string?)null),
                    cancellationToken);
        }
        catch (Exception terminalPersistenceException)
        {
            logger.LogWarning(
                terminalPersistenceException,
                "Ambient social provider send succeeded but terminal state persistence failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
            return;
        }

        try
        {
            await new ZaloBotTraceStore(db).WriteAsync(new ZaloBotTraceEntry(
                MessageId: messageId,
                GroupId: groupId,
                SenderZaloUserId: senderId,
                AddressReason: "AmbientSocialSent",
                IntentSource: "AmbientSocialPilot",
                Intent: ZaloBotIntent.GeneralChat.ToString(),
                Confidence: social.EffectiveScore / 100d,
                ContextMessageIdsJson: JsonSerializer.Serialize(decision.Situation.RecentMessageIds.Take(12)),
                AiCalled: true,
                ReplyMessageId: persistedReplyId,
                FallbackReason: social.AddressReason),
                cancellationToken);
        }
        catch (Exception traceException)
        {
            logger.LogWarning(
                traceException,
                "Ambient social sent but terminal trace failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
        }
    }
}
