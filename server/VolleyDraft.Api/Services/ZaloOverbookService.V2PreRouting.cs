using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Transitional pre-routing adapter used by the Zalo webhook before the legacy
    /// ZaloBotService path. It wires only V2 operations that are safe to run before
    /// domain routing: quote graph capture, structured pending-state shadowing,
    /// explicit self-memory ingestion and deterministic user-owned memory controls.
    ///
    /// Returning true means the message was fully handled and the legacy bot must
    /// not process it again. Returning false leaves existing domain behavior intact.
    /// </summary>
    private async Task<bool> TryHandleV2PreRoutingAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return false;

        // Keep DateTimeOffset ordering out of provider SQL. SQLite cannot ORDER BY
        // DateTimeOffset, while PostgreSQL can; selecting first in memory keeps both
        // providers on the same deterministic semantics.
        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new
            {
                item.Id,
                item.AccountZaloId,
                item.DisplayName,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var connection = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (connection is null) return false;

        // Capture reply/quote topology for every message in a bot-enabled group,
        // even when the message is not addressed to the bot. This is observational
        // context only and never grants authorization.
        try
        {
            await new ZaloMessageGraphStore(db)
                .RememberIncomingQuoteAsync(connection.Id, incoming, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not persist V2 Zalo quote graph Connection={ConnectionId} Group={GroupId} Message={MessageId}",
                connection.Id,
                groupId,
                incoming.MessageId);
        }

        // Ambient observation always runs before the legacy address gate. A live
        // Fact pilot requires two independent gates: ShadowMode=false AND
        // FactPilot.Enabled=true. Explicit mentions/replies continue through V1.
        if (!incoming.MentionedBot)
        {
            await TryObserveAmbientShadowAsync(connection.Id, groupId, incoming, cancellationToken);
            return false;
        }

        await ShadowAndApplyPendingTopicSwitchAsync(
            accountId,
            groupId,
            incoming,
            cancellationToken);

        ZaloMemoryPreRouteResult memory;
        try
        {
            memory = await new ZaloMemoryV2Service(db)
                .ProcessAsync(groupId, incoming, incoming.Content, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "V2 pre-routing memory ingestion failed Group={GroupId} Sender={SenderId} Message={MessageId}",
                groupId,
                incoming.SenderId,
                incoming.MessageId);
            return false;
        }

        // Ordinary explicit self-concepts are captured before routing but the domain
        // command continues through V1. Only deterministic memory-control commands
        // are fully handled here.
        if (!memory.Handled || string.IsNullOrWhiteSpace(memory.Response)) return false;

        var storedIncoming = await EnsureV2IncomingMessageAsync(
            connection.Id,
            groupId,
            incoming,
            cancellationToken);

        var idempotencyKey = $"memory-v2:{accountId}:{incoming.MessageId}";
        var send = await bridge.SendGroupMessageAsync(
            accountId,
            groupId,
            memory.Response,
            [],
            idempotencyKey: idempotencyKey);

        var providerReplyId = NormalizeProviderMessageId(send.MessageId);
        var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
        await EnsureV2OutboundMessageAsync(
            connection.Id,
            groupId,
            persistedReplyId,
            connection.AccountZaloId,
            connection.DisplayName,
            memory.Response,
            cancellationToken);

        if (providerReplyId is not null)
        {
            await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                connection.Id,
                groupId,
                providerReplyId,
                incoming.MessageId,
                cancellationToken);
        }

        storedIncoming.BotReplySentAt = DateTimeOffset.UtcNow;
        storedIncoming.SelectedIntent = "MemoryControl";
        storedIncoming.AiCalled = false;
        storedIncoming.ReplyOutcome = "sent";
        storedIncoming.ProcessingStartedAt = storedIncoming.ProcessingStartedAt ?? DateTimeOffset.UtcNow;
        storedIncoming.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);

        var quoted = ZaloQuotedContextResolver.Resolve(incoming);
        await new ZaloBotTraceStore(db).WriteAsync(
            new ZaloBotTraceEntry(
                incoming.MessageId,
                groupId,
                ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                quoted.RepliesToBot ? "ReplyToBot" : "ExplicitMention",
                IntentSource: "Deterministic",
                Intent: "MemoryControl",
                Confidence: 1,
                QuotedMessageId: quoted.MessageId,
                ConceptIdsJson: memory.RememberedConcept is null
                    ? "[]"
                    : JsonSerializer.Serialize(new[] { memory.RememberedConcept.Id }),
                AiCalled: false,
                ReplyMessageId: providerReplyId ?? persistedReplyId),
            cancellationToken);

        return true;
    }

    private async Task TryObserveAmbientShadowAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var settings = ZaloAmbientSettings.FromConfiguration(configuration);
        if (!settings.Enabled) return;

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        if (senderId.Length == 0 ||
            (botId.Length > 0 && string.Equals(senderId, botId, StringComparison.Ordinal)))
            return;

        try
        {
            var decision = await new ZaloAmbientObserver(db)
                .ObserveAsync(connectionId, incoming, settings, cancellationToken);
            logger.LogDebug(
                "Ambient observation Group={GroupId} Message={MessageId} Sender={SenderId} WouldReply={WouldReply} Score={Score} Kind={Kind} Intent={Intent} ShadowMode={ShadowMode}",
                groupId,
                incoming.MessageId,
                senderId,
                decision.WouldReply,
                decision.Score,
                decision.Kind,
                decision.Intent,
                settings.ShadowMode);

            if (await TryHandleAmbientSocialAsync(
                    connectionId,
                    groupId,
                    senderId,
                    incoming,
                    decision,
                    settings,
                    cancellationToken))
                return;

            var pilot = ZaloAmbientFactPilotSettings.FromConfiguration(configuration);
            if (settings.ShadowMode || !pilot.Enabled) return;

            var fact = await new ZaloAmbientFactResponder(db).TryBuildAsync(
                incoming.AccountId,
                groupId,
                incoming,
                decision,
                pilot.MinimumScore,
                cancellationToken);
            if (fact is null) return;

            await TrySendAmbientFactAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                decision,
                fact,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Ambient participation is additive. A failure here must never interrupt
            // normal chat ingestion, poll sync, or explicitly-addressed bot routing.
            logger.LogWarning(
                exception,
                "Ambient observation/pilot failed Group={GroupId} Sender={SenderId} Message={MessageId}",
                groupId,
                senderId,
                incoming.MessageId);
        }
    }

    private async Task TrySendAmbientFactAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        ZaloAmbientFactReply fact,
        CancellationToken cancellationToken)
    {
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        var observed = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ZaloConnectionId == connectionId && item.MessageId == messageId, cancellationToken);
        if (observed is null || observed.BotReplySentAt is not null) return;

        // Evaluate lease staleness in memory for SQLite/PostgreSQL parity. The atomic
        // claim uses ReplyAttemptCount as a simple non-null concurrency token so two
        // webhook deliveries cannot both win without nullable SQL equality semantics.
        if (string.Equals(observed.ReplyOutcome, "ambient_processing", StringComparison.Ordinal) &&
            observed.ProcessingStartedAt is { } startedAt &&
            startedAt >= DateTimeOffset.UtcNow.AddMinutes(-2))
            return;
        if (observed.ReplyOutcome is not null &&
            observed.ReplyOutcome is not "ambient_processing" and not "ambient_send_failed")
            return;

        var previousAttemptCount = observed.ReplyAttemptCount;
        var processingToken = $"ambient-fact:{Guid.NewGuid():n}";
        var claimStartedAt = DateTimeOffset.UtcNow;
        var claimed = await db.ZaloGroupMessages
            .Where(item => item.Id == observed.Id &&
                           item.BotReplySentAt == null &&
                           item.ReplyAttemptCount == previousAttemptCount)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessingStartedAt, claimStartedAt)
                .SetProperty(item => item.ProcessingToken, processingToken)
                .SetProperty(item => item.ReplyAttemptCount, item => item.ReplyAttemptCount + 1)
                .SetProperty(item => item.ReplyOutcome, "ambient_processing"), cancellationToken);
        if (claimed == 0) return;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var idempotencyKey = $"ambient-fact:{accountId}:{messageId}";
        BridgeSendMessageResponse send;
        try
        {
            send = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                fact.Text,
                [],
                idempotencyKey: idempotencyKey);
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
                    .SetProperty(item => item.ReplyOutcome, "ambient_send_failed"), cancellationToken);
            logger.LogWarning(
                sendException,
                "Could not send ambient Fact reply Group={GroupId} Message={MessageId} Intent={Intent}",
                groupId,
                messageId,
                fact.Intent);
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
                fact.Text,
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
            // The provider send already succeeded. Do not turn telemetry failure
            // into a second user-visible send; the idempotency key remains stable.
            logger.LogWarning(
                persistenceException,
                "Ambient fact sent but outbound telemetry persistence failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
        }

        try
        {
            var replySentAt = DateTimeOffset.UtcNow;
            var selectedIntent = fact.Intent.ToString();
            await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == connectionId &&
                               item.MessageId == messageId &&
                               item.ProcessingToken == processingToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.BotReplySentAt, replySentAt)
                    .SetProperty(item => item.SelectedIntent, selectedIntent)
                    .SetProperty(item => item.AiCalled, false)
                    .SetProperty(item => item.ReplyOutcome, "ambient_sent")
                    .SetProperty(item => item.ProcessingToken, (string?)null), cancellationToken);
        }
        catch (Exception terminalPersistenceException)
        {
            // Provider has already accepted the idempotent send. Do not classify a
            // terminal persistence problem as a send failure and do not issue another
            // user-visible send from this invocation.
            logger.LogWarning(
                terminalPersistenceException,
                "Ambient fact provider send succeeded but terminal state persistence failed Group={GroupId} Message={MessageId}",
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
                AddressReason: "AmbientFactSent",
                IntentSource: "AmbientFactPilot",
                Intent: fact.Intent.ToString(),
                Confidence: decision.Score / 100d,
                ContextMessageIdsJson: JsonSerializer.Serialize(decision.Situation.RecentMessageIds.Take(12)),
                ResolvedSessionId: fact.SessionId,
                AiCalled: false,
                ReplyMessageId: persistedReplyId), cancellationToken);
        }
        catch (Exception traceException)
        {
            logger.LogWarning(
                traceException,
                "Ambient fact sent but terminal trace persistence failed Group={GroupId} Message={MessageId}",
                groupId,
                messageId);
        }
    }

    private async Task ShadowAndApplyPendingTopicSwitchAsync(
        string accountId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (senderId.Length == 0) return;
        var now = DateTimeOffset.UtcNow;

        // Keep stable identity predicates in SQL, then evaluate DateTimeOffset expiry
        // and ordering in memory so SQLite/PostgreSQL use the same temporal semantics.
        var pendingRows = await db.ZaloBotConversationStates
            .Include(item => item.ZaloConnection)
            .Where(item => item.GroupId == groupId &&
                           item.SenderZaloUserId == senderId &&
                           item.ZaloConnection.AccountZaloId == accountId)
            .ToListAsync(cancellationToken);
        var pending = pendingRows
            .Where(item => item.ExpiresAt > now)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (pending is null) return;

        var v2Store = new ZaloConversationStateV2Store(db);
        try
        {
            // Project the legacy payload into the typed V2 envelope in this webhook
            // turn, before topic-switch/routing decisions. Do not copy arbitrary
            // legacy JSON into V2 collected arguments.
            var typed = ZaloLegacyPendingPayloadAdapter.Adapt(
                pending.PendingIntent,
                pending.PendingPayloadJson);
            await v2Store.SaveActiveAsync(
                groupId,
                senderId,
                pending.PendingIntent,
                typed.CollectedArgumentsJson,
                typed.MissingArgumentsJson,
                typed.CandidateEntitiesJson,
                sourceMessageId: null,
                lastMessageId: incoming.MessageId,
                expiresAt: pending.ExpiresAt,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not project legacy pending state into typed V2 state Group={GroupId} Sender={SenderId} Intent={Intent}",
                groupId,
                senderId,
                pending.PendingIntent);
            return;
        }

        var migration = ZaloConversationStateMigrationPolicy.Evaluate(pending.PendingIntent, incoming.Content);
        if (migration.Decision != ZaloTopicSwitchDecision.SwitchToNewIntent) return;

        db.ZaloBotConversationStates.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);
        await v2Store.CancelAsync(groupId, senderId, cancellationToken);

        var quoted = ZaloQuotedContextResolver.Resolve(incoming);
        await new ZaloBotTraceStore(db).WriteAsync(
            new ZaloBotTraceEntry(
                incoming.MessageId,
                groupId,
                senderId,
                quoted.RepliesToBot ? "ReplyToBot" : "ExplicitMention",
                IntentSource: "DeterministicPreRouting",
                Intent: migration.FreshIntent,
                Confidence: migration.Confidence,
                QuotedMessageId: quoted.MessageId,
                PendingStateBefore: pending.PendingIntent,
                PendingStateAfter: null,
                AiCalled: false,
                FallbackReason: migration.Reason),
            cancellationToken);
    }

    private async Task<ZaloGroupMessage> EnsureV2IncomingMessageAsync(
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

        var now = DateTimeOffset.UtcNow;
        var message = new ZaloGroupMessage
        {
            ZaloConnectionId = connectionId,
            GroupId = groupId,
            MessageId = messageId,
            SenderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId),
            SenderName = Trim(incoming.SenderName, 160, "Thành viên Zalo"),
            Content = Trim(incoming.Content, 4000, string.Empty),
            MessageType = "chat",
            ObservationSource = "RealtimeV2",
            IsFromBot = false,
            SentAt = ToSafeV2Timestamp(incoming.SentAtUnixMs),
            ReceivedAt = now,
            FirstObservedAt = now,
            LastObservedAt = now,
            ReplyAttemptCount = 1,
            ProcessingStartedAt = now,
            ProcessingToken = $"memory-v2:{Guid.NewGuid():n}",
            ReplyOutcome = "processing"
        };
        db.ZaloGroupMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return message;
        }
        catch (DbUpdateException)
        {
            db.Entry(message).State = EntityState.Detached;
            return await db.ZaloGroupMessages.SingleAsync(item =>
                item.ZaloConnectionId == connectionId && item.MessageId == messageId,
                cancellationToken);
        }
    }

    private async Task EnsureV2OutboundMessageAsync(
        string connectionId,
        string groupId,
        string messageId,
        string senderId,
        string senderName,
        string content,
        CancellationToken cancellationToken)
    {
        if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                item.ZaloConnectionId == connectionId && item.MessageId == messageId,
                cancellationToken))
            return;

        var now = DateTimeOffset.UtcNow;
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = connectionId,
            GroupId = groupId,
            MessageId = messageId,
            SenderId = senderId,
            SenderName = Trim(senderName, 160, "Volley Bot"),
            Content = Trim(content, 4000, string.Empty),
            MessageType = "chat",
            ObservationSource = "OutboundV2",
            IsFromBot = true,
            SentAt = now,
            ReceivedAt = now,
            FirstObservedAt = now,
            LastObservedAt = now,
            ReplyOutcome = "sent"
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? NormalizeProviderMessageId(string? value)
    {
        var id = (value ?? string.Empty).Trim();
        return id.Length == 0 ? null : id.Length <= 160 ? id : id[..160];
    }

    private static DateTimeOffset ToSafeV2Timestamp(long unixMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string Trim(string? value, int maxLength, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) text = fallback;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
