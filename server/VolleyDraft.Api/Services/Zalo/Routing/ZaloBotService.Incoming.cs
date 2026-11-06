using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    public async Task HandleIncomingAsync(ZaloIncomingMessageEvent incoming, CancellationToken cancellationToken = default)
    {
        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var messageId = NormalizeId(incoming.MessageId);
        var connections = await db.ZaloConnections
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.ZaloGroupId == groupId && session.BotEnabled))
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        var connection = connections.FirstOrDefault();
        if (connection is null || string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(messageId)) return;
        var connectionIds = connections.Select(item => item.Id).ToList();

        var recoveredIncoming = RecoverTextualBotMention(incoming, connection.DisplayName);
        if (recoveredIncoming.MentionedBot && !incoming.MentionedBot)
        {
            logger.LogInformation(
                "Recovered textual Zalo bot mention Account={AccountId} Group={GroupId} Message={MessageId}",
                accountId,
                groupId,
                messageId);
        }
        incoming = recoveredIncoming;

        var storedMessage = await db.ZaloGroupMessages.SingleOrDefaultAsync(message =>
            message.ZaloConnectionId == connection.Id && message.MessageId == messageId, cancellationToken);
        if (storedMessage is null)
        {
            storedMessage = new ZaloGroupMessage
            {
                ZaloConnectionId = connection.Id,
                GroupId = groupId,
                MessageId = messageId,
                SenderId = NormalizeId(incoming.SenderId),
                SenderName = Clean(incoming.SenderName, 160) ?? "Thành viên Zalo",
                Content = Clean(incoming.Content, 4000) ?? string.Empty,
                IsFromBot = false,
                SentAt = ToSafeTimestamp(incoming.SentAtUnixMs),
                ReceivedAt = DateTimeOffset.UtcNow
            };
            db.ZaloGroupMessages.Add(storedMessage);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                storedMessage = await db.ZaloGroupMessages.SingleAsync(message =>
                    message.ZaloConnectionId == connection.Id && message.MessageId == messageId, cancellationToken);
            }
        }

        var explicitlyMentioned = incoming.MentionedBot && incoming.Mentions.Any(mention =>
            NormalizeId(mention.Uid) == NormalizeId(incoming.BotId));
        var groupLeaveAction = ZaloGroupLeaveCommand.Parse(ExtractQuestion(incoming), explicitlyMentioned);
        if ((!explicitlyMentioned && groupLeaveAction == ZaloGroupLeaveAction.None) || storedMessage.BotReplySentAt is not null) return;

        var processingToken = Guid.NewGuid().ToString("n");
        var leaseCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        var claimed = await db.ZaloGroupMessages
            .Where(message => message.Id == storedMessage.Id &&
                              message.BotReplySentAt == null &&
                              (message.ReplyOutcome == null ||
                               (message.ReplyOutcome != "throttled" && message.ReplyOutcome != "no_reply")) &&
                              (message.ProcessingStartedAt == null || message.ProcessingStartedAt < leaseCutoff))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, DateTimeOffset.UtcNow)
                .SetProperty(message => message.ProcessingToken, processingToken)
                .SetProperty(message => message.ReplyAttemptCount, message => message.ReplyAttemptCount + 1)
                .SetProperty(message => message.ReplyOutcome, "processing"), cancellationToken);
        if (claimed == 0)
        {
            logger.LogInformation("Zalo bot duplicate skipped Account={AccountId} Group={GroupId} Message={MessageId}", accountId, groupId, messageId);
            return;
        }

        var exactCooldownSeconds = Math.Clamp(configuration.GetValue("ZaloBot:ExactCommandCooldownSeconds", 2), 0, 60);
        var incomingQuestion = ExtractQuestion(incoming);
        var bypassCooldown = groupLeaveAction != ZaloGroupLeaveAction.None ||
                             ZaloBotIntelligence.IsConfirmation(incomingQuestion) || ZaloBotIntelligence.IsCancel(incomingQuestion);
        var cooldownCutoff = DateTimeOffset.UtcNow.AddSeconds(-exactCooldownSeconds);
        var tooSoon = !bypassCooldown && await db.ZaloGroupMessages.AsNoTracking().AnyAsync(message =>
                message.Id != storedMessage.Id && message.ZaloConnectionId == connection.Id &&
                message.GroupId == groupId && message.SenderId == NormalizeId(incoming.SenderId) &&
                message.ProcessingStartedAt >= cooldownCutoff, cancellationToken);
        if (tooSoon)
        {
            await FinishMessageAsync(storedMessage.Id, processingToken, null, false, "throttled", cancellationToken);
            return;
        }

        BotAnswer response;
        try
        {
            response = groupLeaveAction != ZaloGroupLeaveAction.None
                ? await BuildGroupLeaveCommandAnswerAsync(connection, groupId, incoming, groupLeaveAction, cancellationToken)
                : await BuildAnswerAsync(connectionIds, connection.Id, groupId, incoming, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Zalo bot routing failed Account={AccountId} Group={GroupId} Message={MessageId}", accountId, groupId, messageId);
            await ReleaseMessageAsync(storedMessage.Id, processingToken, "routing_failed", cancellationToken);
            throw;
        }
        // Every leave command (including denied/cancelled requests) bypasses AI,
        // irrespective of the response intent.
        if (groupLeaveAction == ZaloGroupLeaveAction.None &&
            !response.TextGeneratedByAi &&
            response.Intent is not (ZaloBotIntent.GeneralChat or ZaloBotIntent.GroupLeaveRequest or ZaloBotIntent.GroupLeaveConfirm) &&
            ZaloBotIntelligence.CanUseAiStyleRewrite(response.Intent) &&
            response.Mentions is not { Count: > 0 } &&
            ai.IsConfigured &&
            configuration.GetValue("ZaloBot:AiStyleEnabled", true) &&
            await IsAiCallAllowedAsync(connection.Id, groupId, incoming.SenderId, cancellationToken))
        {
            var rewritten = await ai.RewriteFactualAnswerAsync(
                new ZaloAiRewriteContext(
                    incomingQuestion,
                    Clean(incoming.SenderName, 160) ?? "Thành viên Zalo",
                    response.Intent,
                    response.Text,
                    response.ProtectedTerms),
                cancellationToken);
            response = response with { AiCalled = true };
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                response = response with
                {
                    Text = rewritten,
                    AiCalled = true,
                    TextGeneratedByAi = true
                };
            }
        }
        if (string.IsNullOrWhiteSpace(response.Text))
        {
            await FinishMessageAsync(storedMessage.Id, processingToken, response.Intent, response.AiCalled, "no_reply", cancellationToken);
            return;
        }

        var senderName = (Clean(incoming.SenderName, 50) ?? "bạn").TrimStart('@');
        var mentionLabel = $"@{senderName}";
        var isGroupFarewell = response.Intent == ZaloBotIntent.GroupLeaveConfirm;
        // A farewell is addressed to everyone, not just the member who confirmed.
        var reply = isGroupFarewell ? response.Text.Trim() : $"{mentionLabel} {response.Text.Trim()}";
        var outgoingMentions = isGroupFarewell
            ? new List<BridgeOutgoingMention>()
            : new List<BridgeOutgoingMention>
            {
                new(NormalizeId(incoming.SenderId), 0, mentionLabel.Length)
            };
        if (response.Mentions is { Count: > 0 })
        {
            var responseOffset = mentionLabel.Length + 1;
            outgoingMentions.AddRange(response.Mentions.Select(mention => mention with
            {
                Pos = mention.Pos + responseOffset
            }));
        }
        try
        {
            await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                groupId,
                reply,
                outgoingMentions,
                response.ImageUrl,
                $"{accountId}:{messageId}",
                response.ImageBase64,
                response.ImageContentType,
                response.ImageFileName);
        }
        catch
        {
            await ReleaseMessageAsync(storedMessage.Id, processingToken, "send_failed", cancellationToken);
            throw;
        }

        await FinishMessageAsync(storedMessage.Id, processingToken, response.Intent, response.AiCalled, "sent", cancellationToken);

        if (response.Intent == ZaloBotIntent.GroupLeaveConfirm)
        {
            await ExecuteGroupLeaveAfterGoodbyeAsync(connection, groupId, incoming);
            return;
        }

        if (response.Intent == ZaloBotIntent.GeneralChat)
        {
            try
            {
                if (ZaloBanterAffinityPolicy.LooksLikeDirectBanter(incoming))
                {
                    await new ZaloBanterAffinityStore(db).RememberAsync(
                        connection.Id,
                        groupId,
                        NormalizeId(incoming.SenderId),
                        messageId,
                        "direct_general_chat_banter",
                        cancellationToken);
                }
            }
            catch (Exception banterMemoryException) when (banterMemoryException is not OperationCanceledException)
            {
                logger.LogWarning(
                    banterMemoryException,
                    "Could not remember direct Zalo banter Group={GroupId} Sender={SenderId} Message={MessageId}",
                    groupId,
                    incoming.SenderId,
                    messageId);
            }
        }

        await ZaloDirectedAudienceBotIntegration.RememberAsync(
            db, logger, connection.Id, groupId, NormalizeId(incoming.SenderId), messageId,
            response.ReferencedAudience, cancellationToken);

        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = connection.Id,
            GroupId = groupId,
            MessageId = $"bot:{Guid.NewGuid():n}",
            SenderId = connection.AccountZaloId,
            SenderName = connection.DisplayName,
            Content = reply,
            IsFromBot = true,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Zalo bot replied Account={AccountId} Group={GroupId} Message={MessageId} Intent={Intent} AiCalled={AiCalled}",
            accountId, groupId, messageId, response.Intent, response.AiCalled);
    }

    internal static ZaloIncomingMessageEvent RecoverTextualBotMention(
        ZaloIncomingMessageEvent incoming,
        string? botDisplayName)
    {
        var normalizedBotId = NormalizeId(incoming.BotId);
        if (normalizedBotId.Length == 0) return incoming;

        var hasStructuredBotMention = incoming.Mentions.Any(mention =>
            NormalizeId(mention.Uid) == normalizedBotId);
        if (hasStructuredBotMention)
        {
            return incoming.MentionedBot
                ? incoming
                : incoming with { MentionedBot = true };
        }

        if (incoming.MentionedBot)
        {
            // Trust the bridge-owned addressed flag even if a provider version omitted
            // the raw mention span. Add a metadata-only marker so downstream lanes use
            // the same explicit-addressing semantics as structured mentions.
            return incoming with
            {
                Mentions = incoming.Mentions
                    .Concat([new ZaloBridgeMention(normalizedBotId, -1, 0)])
                    .ToList()
            };
        }

        if (!TryFindLeadingBotMention(incoming.Content, botDisplayName, out var position, out var length))
            return incoming;

        return incoming with
        {
            MentionedBot = true,
            Mentions = incoming.Mentions
                .Concat([new ZaloBridgeMention(normalizedBotId, position, length)])
                .ToList()
        };
    }

    private static bool TryFindLeadingBotMention(
        string? content,
        string? botDisplayName,
        out int position,
        out int length)
    {
        position = -1;
        length = 0;
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(botDisplayName))
            return false;

        var displayName = botDisplayName.Trim().TrimStart('@').Trim();
        if (displayName.Length == 0) return false;

        var namePattern = string.Join(
            @"\s+",
            Regex.Split(displayName, @"\s+")
                .Where(token => token.Length > 0)
                .Select(Regex.Escape));
        if (namePattern.Length == 0) return false;

        var match = Regex.Match(
            content,
            $@"^\s*@{namePattern}(?=$|[\s:,.!?;\-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        var atOffset = match.Value.IndexOf('@');
        if (atOffset < 0) return false;
        position = match.Index + atOffset;
        length = match.Length - atOffset;
        return length > 1;
    }
}
