using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleDirectedAudienceContinuationAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        if (incoming.MentionedBot) return false;

        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        if (quote.HasQuote && !quote.RepliesToBot) return false;

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (senderId.Length == 0) return false;
        var hasLease = await new ZaloAmbientConversationLeaseResolver(db)
            .IsActiveAsync(connectionId, groupId, senderId, 180, cancellationToken);
        if (!hasLease) return false;

        var directed = await new ZaloDirectedAudienceMessageService(
                db,
                integration,
                configuration,
                logger)
            .TryHandleAsync(
                connectionId,
                groupId,
                incoming,
                incoming.Content,
                cancellationToken);
        if (directed is null) return false;

        await SendDirectedAudienceContinuationAsync(
            connectionId,
            groupId,
            senderId,
            incoming,
            directed,
            cancellationToken);
        return true;
    }

    private async Task SendDirectedAudienceContinuationAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloDirectedAudienceReply directed,
        CancellationToken cancellationToken)
    {
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        if (messageId.Length == 0) return;
        var observed = await EnsureDirectedAudienceIncomingAsync(
            connectionId,
            groupId,
            incoming,
            cancellationToken);
        if (observed.BotReplySentAt is not null) return;

        if (string.Equals(observed.ReplyOutcome, "directed_audience_processing", StringComparison.Ordinal) &&
            observed.ProcessingStartedAt is { } startedAt &&
            startedAt >= DateTimeOffset.UtcNow.AddMinutes(-2))
            return;
        if (observed.ReplyOutcome is not null &&
            observed.ReplyOutcome is not "directed_audience_processing" and not "directed_audience_send_failed")
            return;

        var previousAttemptCount = observed.ReplyAttemptCount;
        var processingToken = $"directed-audience:{Guid.NewGuid():n}";
        var claimed = await db.ZaloGroupMessages
            .Where(item => item.Id == observed.Id &&
                           item.BotReplySentAt == null &&
                           item.ReplyAttemptCount == previousAttemptCount)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessingStartedAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.ProcessingToken, processingToken)
                .SetProperty(item => item.ReplyAttemptCount, item => item.ReplyAttemptCount + 1)
                .SetProperty(item => item.ReplyOutcome, "directed_audience_processing"),
                cancellationToken);
        if (claimed == 0) return;

        var senderName = CleanDirectedAudienceName(incoming.SenderName, 50);
        var senderLabel = $"@{senderName}";
        var replyText = $"{senderLabel} {directed.Text.Trim()}";
        var outgoingMentions = new List<BridgeOutgoingMention>
        {
            new(senderId, 0, senderLabel.Length)
        };
        var responseOffset = senderLabel.Length + 1;
        outgoingMentions.AddRange(directed.Mentions.Select(mention => mention with
        {
            Pos = mention.Pos + responseOffset
        }));

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var idempotencyKey = $"directed-audience:{accountId}:{messageId}";
        BridgeSendMessageResponse send;
        try
        {
            send = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                replyText,
                outgoingMentions,
                idempotencyKey: idempotencyKey);
            if (!send.Sent)
                throw new InvalidOperationException("Zalo bridge did not confirm directed audience send.");
        }
        catch (Exception sendException)
        {
            await db.ZaloGroupMessages
                .Where(item => item.Id == observed.Id && item.ProcessingToken == processingToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.ProcessingToken, (string?)null)
                    .SetProperty(item => item.ReplyOutcome, "directed_audience_send_failed"),
                    cancellationToken);
            logger.LogWarning(
                sendException,
                "Could not send directed audience continuation Group={GroupId} Message={MessageId}",
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
                replyText,
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
            logger.LogWarning(
                persistenceException,
                "Directed audience continuation sent but outbound persistence failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
        }

        await db.ZaloGroupMessages
            .Where(item => item.Id == observed.Id && item.ProcessingToken == processingToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.BotReplySentAt, DateTimeOffset.UtcNow)
                .SetProperty(item => item.SelectedIntent, "DirectedAudienceMessage")
                .SetProperty(item => item.AiCalled, directed.AiCalled)
                .SetProperty(item => item.ReplyOutcome, "directed_audience_sent")
                .SetProperty(item => item.ProcessingToken, (string?)null),
                cancellationToken);

        logger.LogInformation(
            "Continued grounded audience conversation without re-mention Group={GroupId} Sender={SenderId} Message={MessageId} MentionCount={MentionCount}",
            groupId,
            senderId,
            messageId,
            directed.Mentions.Count);
    }

    private async Task<ZaloGroupMessage> EnsureDirectedAudienceIncomingAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        var existing = await db.ZaloGroupMessages.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.MessageId == messageId,
            cancellationToken);
        if (existing is not null) return existing;

        var row = new ZaloGroupMessage
        {
            ZaloConnectionId = connectionId,
            GroupId = groupId,
            MessageId = messageId,
            SenderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId),
            SenderName = incoming.SenderName,
            Content = incoming.Content,
            IsFromBot = false,
            SentAt = DirectedAudienceTimestamp(incoming.SentAtUnixMs),
            ReceivedAt = DateTimeOffset.UtcNow
        };
        db.ZaloGroupMessages.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return row;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await db.ZaloGroupMessages.SingleAsync(item =>
                item.ZaloConnectionId == connectionId && item.MessageId == messageId,
                cancellationToken);
        }
    }

    private static string CleanDirectedAudienceName(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('@');
        if (text.Length == 0) text = "bạn";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static DateTimeOffset DirectedAudienceTimestamp(long unixMs)
    {
        try
        {
            var value = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            return Math.Abs((value - DateTimeOffset.UtcNow).TotalDays) > 30
                ? DateTimeOffset.UtcNow
                : value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
