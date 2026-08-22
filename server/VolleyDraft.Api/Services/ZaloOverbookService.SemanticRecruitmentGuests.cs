using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string SemanticGuestConversationIntent = "RecruitmentGuestProfileSemantic";

    private sealed record SemanticGuestTurnContext(
        MatchSession Session,
        ZaloSemanticGuestAnchorKind AnchorKind,
        string? RecruitmentMessageId,
        IReadOnlyList<ZaloGuestReservation> ExistingGuests,
        IReadOnlyList<string> PendingMissingFields);

    /// <summary>
    /// Runs after V2 has captured ReplyTo topology but before Ambient/general routing.
    /// A grounded guest conversation gets first refusal; AI may interpret language but
    /// only validated DB-backed plans can reach the mutation service.
    /// </summary>
    private async Task<bool> TryHandleSemanticRecruitmentGuestPreRouteAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(incoming.Content)) return false;
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (senderId.Length == 0) return false;
        var turn = await ResolveSemanticGuestTurnContextAsync(
            connectionId,
            groupId,
            incoming.MessageId,
            senderId,
            cancellationToken);
        if (turn is null) return false;

        // Active follow-up context is intentionally a softer gate than ReplyTo. It is
        // only used to wake semantic understanding for short profile/cancel turns;
        // unrelated text can still fall through when the model returns None.
        if (turn.AnchorKind == ZaloSemanticGuestAnchorKind.ActiveGuestConversation &&
            !LooksLikePotentialGuestContinuation(incoming.Content))
            return false;

        var actionSettings = ZaloSemanticActionSettings.FromConfiguration(configuration);
        var recentIds = await LoadRecentGuestContextMessageIdsAsync(
            connectionId,
            groupId,
            actionSettings.MaxContextMessages,
            cancellationToken);
        var conversation = await ZaloReadOnlyConversationContextLoader.LoadAsync(
            db,
            connectionId,
            groupId,
            incoming,
            recentIds,
            actionSettings.MaxContextMessages,
            cancellationToken);
        var snapshot = await BuildSemanticGuestSnapshotAsync(turn, senderId, incoming.SenderName, cancellationToken);
        var planner = new ZaloSemanticGuestPlanner(configuration, logger);
        var plan = await planner.PlanAsync(
            connectionId,
            groupId,
            incoming.Content,
            conversation,
            snapshot,
            actionSettings,
            cancellationToken);
        var aiCalled = !IsSemanticGuestTechnicalFallback(plan.Reason);

        if (IsSemanticGuestTechnicalFallback(plan.Reason))
            plan = BuildSemanticGuestFallbackPlan(incoming.Content, snapshot);

        if (plan.Action == ZaloSemanticGuestActionKind.None)
        {
            // A reply directly to one of our guest/recruitment messages belongs to this
            // lane even when it is just acknowledgement; keep legacy/general AI away.
            if (turn.AnchorKind is ZaloSemanticGuestAnchorKind.RecruitmentBroadcast or ZaloSemanticGuestAnchorKind.GuestConversation)
            {
                await MarkSemanticGuestTurnWithoutReplyAsync(
                    connectionId,
                    groupId,
                    incoming,
                    "guest_semantic_noop",
                    aiCalled,
                    cancellationToken);
                return true;
            }
            return false;
        }

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, actionSettings);
        if (!validation.Accepted)
        {
            var clarification = string.IsNullOrWhiteSpace(validation.ClarificationReason)
                ? "Tui hiểu ý liên quan guest nhưng chưa đủ chắc để đổi dữ liệu. Nói rõ hơn giúp tui nha."
                : validation.ClarificationReason;
            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                turn.Session.Id,
                clarification,
                validation.Reason,
                aiCalled,
                cancellationToken);
            return true;
        }

        try
        {
            var service = new ZaloGuestReservationService(db);
            string reply;
            string outcome;
            if (validation.Action == ZaloSemanticGuestActionKind.AddGuests)
            {
                // Poll is source of truth. Semantic meaning was already decided; now
                // refresh authoritative roster before preview/capacity mutation.
                var sync = await RefreshLinkedPollForDraftReminderAsync(turn.Session, cancellationToken);
                if (!sync.Success)
                {
                    await SendSemanticGuestReplyAsync(
                        connectionId,
                        accountId,
                        botName,
                        groupId,
                        incoming,
                        turn.Session.Id,
                        $"Tui hiểu ông muốn +{validation.Quantity} cho {turn.Session.Name}, nhưng chưa sync được poll thật nên chưa giữ slot để khỏi cộng nhầm nha.",
                        "guest_semantic_poll_sync_failed",
                        aiCalled,
                        cancellationToken);
                    return true;
                }

                await new ZaloGuestIdentityReconciler(db).ReconcileAsync(turn.Session.Id, cancellationToken);
                var preview = await BuildSemanticGuestMutationPreviewAsync(
                    turn.Session.Id,
                    validation.Quantity,
                    cancellationToken);
                logger.LogInformation(
                    "Semantic guest preview Session={SessionId} Sender={SenderId} Before={Before}/{Capacity} Add={Add} Admit={Admit} Wait={Wait} After={After}",
                    turn.Session.Id,
                    senderId,
                    preview.BeforeEffectiveSlots,
                    preview.Capacity,
                    validation.Quantity,
                    preview.AdmitCount,
                    preview.WaitlistCount,
                    preview.AfterEffectiveSlots);

                var command = new ZaloRecruitmentGuestCommand(
                    ZaloRecruitmentGuestCommandKind.Add,
                    validation.Quantity,
                    validation.Items.Select(item => new ZaloRecruitmentGuestSpec(
                        item.DisplayName,
                        item.Gender,
                        item.Role,
                        item.Level)).ToArray());
                var result = await service.AddAsync(
                    turn.Session,
                    senderId,
                    FriendlySponsorName(incoming.SenderName, senderId),
                    incoming.MessageId,
                    turn.RecruitmentMessageId,
                    command,
                    cancellationToken);
                reply = BuildSemanticGuestAddReply(result);
                outcome = result.Idempotent ? "guest_semantic_add_idempotent" : "guest_semantic_added";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    turn.Session.Id,
                    incoming.MessageId,
                    turn.RecruitmentMessageId,
                    cancellationToken);
            }
            else if (validation.Action == ZaloSemanticGuestActionKind.UpdateGuestProfiles)
            {
                var changed = new List<ZaloGuestReservation>();
                foreach (var item in validation.Items)
                {
                    var result = await service.UpdateProfileAsync(
                        turn.Session,
                        senderId,
                        new ZaloRecruitmentGuestCommand(
                            ZaloRecruitmentGuestCommandKind.UpdateProfile,
                            SponsorSequence: item.SponsorSequence,
                            RenameTo: item.DisplayName,
                            Gender: item.Gender,
                            Role: item.Role,
                            Level: item.Level),
                        cancellationToken);
                    if (result.NeedsClarification)
                    {
                        await SendSemanticGuestReplyAsync(
                            connectionId,
                            accountId,
                            botName,
                            groupId,
                            incoming,
                            turn.Session.Id,
                            result.Clarification ?? "Tui chưa xác định chắc guest nào cần cập nhật.",
                            "guest_semantic_update_clarification",
                            aiCalled,
                            cancellationToken);
                        return true;
                    }
                    changed.AddRange(result.Changed);
                }
                reply = BuildSemanticGuestProfileReply(turn.Session.Name, changed);
                outcome = "guest_semantic_profile_updated";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    turn.Session.Id,
                    incoming.MessageId,
                    turn.RecruitmentMessageId,
                    cancellationToken);
            }
            else
            {
                var changed = new List<ZaloGuestReservation>();
                foreach (var item in validation.Items)
                {
                    var result = await service.CancelAsync(
                        turn.Session,
                        senderId,
                        new ZaloRecruitmentGuestCommand(
                            ZaloRecruitmentGuestCommandKind.Cancel,
                            SponsorSequence: item.SponsorSequence),
                        cancellationToken);
                    if (result.NeedsClarification)
                    {
                        await SendSemanticGuestReplyAsync(
                            connectionId,
                            accountId,
                            botName,
                            groupId,
                            incoming,
                            turn.Session.Id,
                            result.Clarification ?? "Tui chưa xác định chắc guest nào nghỉ.",
                            "guest_semantic_cancel_clarification",
                            aiCalled,
                            cancellationToken);
                        return true;
                    }
                    changed.AddRange(result.Changed);
                }
                var names = string.Join(", ", changed.DistinctBy(item => item.Id).Select(item => item.DisplayName));
                reply = $"Ok, tui rút {names} khỏi {turn.Session.Name}. Slot trống sẽ ưu tiên guest đang chờ trước; nếu vẫn thiếu thì luồng kiếm thêm tiếp tục theo cooldown.";
                outcome = "guest_semantic_cancelled";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    turn.Session.Id,
                    incoming.MessageId,
                    turn.RecruitmentMessageId,
                    cancellationToken);
            }

            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                turn.Session.Id,
                reply,
                outcome,
                aiCalled,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Semantic guest execution failed Session={SessionId} Message={MessageId}",
                turn.Session.Id,
                incoming.MessageId);
            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                turn.Session.Id,
                "Tui hiểu ý guest nhưng thao tác DB chưa chạy an toàn được, nên tui chưa đổi roster nha. Thử lại chút nữa giúp tui.",
                "guest_semantic_execution_failed",
                aiCalled,
                cancellationToken);
            return true;
        }
    }

    private async Task<SemanticGuestTurnContext?> ResolveSemanticGuestTurnContextAsync(
        string connectionId,
        string groupId,
        string incomingMessageId,
        string senderId,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        string? recruitmentMessageId = null;
        var anchorKind = ZaloSemanticGuestAnchorKind.None;
        var pendingMissing = Array.Empty<string>();

        var relation = await new ZaloMessageGraphStore(db)
            .LoadRelationAsync(connectionId, groupId, incomingMessageId, cancellationToken);
        if (relation?.RelationType == "ReplyTo" && !string.IsNullOrWhiteSpace(relation.ToMessageId))
        {
            var anchor = await db.ZaloGroupMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    item.MessageId == relation.ToMessageId &&
                    item.IsFromBot,
                    cancellationToken);
            if (anchor?.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome)
            {
                sessionId = ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId(anchor.SelectedIntent);
                recruitmentMessageId = anchor.MessageId;
                anchorKind = ZaloSemanticGuestAnchorKind.RecruitmentBroadcast;
            }
            else if (anchor?.ReplyOutcome == ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome)
            {
                sessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(anchor.SelectedIntent);
                anchorKind = ZaloSemanticGuestAnchorKind.GuestConversation;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var active = await new ZaloConversationStateV2Store(db)
                .LoadActiveAsync(groupId, senderId, cancellationToken);
            if (active is not null && string.Equals(active.Intent, SemanticGuestConversationIntent, StringComparison.Ordinal))
            {
                sessionId = TryReadSemanticGuestConversationSessionId(active.CollectedArgumentsJson);
                pendingMissing = ReadStringArray(active.MissingArgumentsJson);
                anchorKind = ZaloSemanticGuestAnchorKind.ActiveGuestConversation;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var now = DateTimeOffset.UtcNow;
        var session = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item =>
                item.Id == sessionId &&
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.ZaloConnection != null &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                item.StartTime != null && item.StartTime > now,
                cancellationToken);
        if (session is null) return null;

        var guests = await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item => item.SessionId == session.Id &&
                           item.SponsorZaloUserId == senderId &&
                           (item.Status == ZaloGuestReservationStatus.Active || item.Status == ZaloGuestReservationStatus.Waitlisted))
            .OrderBy(item => item.SponsorSequence)
            .ToListAsync(cancellationToken);
        return new SemanticGuestTurnContext(session, anchorKind, recruitmentMessageId, guests, pendingMissing);
    }

    private async Task<ZaloSemanticGuestGroundingSnapshot> BuildSemanticGuestSnapshotAsync(
        SemanticGuestTurnContext turn,
        string senderId,
        string? senderName,
        CancellationToken cancellationToken)
    {
        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(turn.Session.Id, cancellationToken: cancellationToken);
        var utc = DateTimeOffset.UtcNow;
        return new ZaloSemanticGuestGroundingSnapshot(
            turn.Session.Id,
            turn.Session.Name,
            turn.Session.StartTime,
            readiness?.EffectiveSlotCount ?? 0,
            readiness?.Capacity ?? Math.Max(1, turn.Session.TeamCount * turn.Session.TeamSize),
            ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(turn.Session.StartTime, utc, configuration),
            senderId,
            FriendlySponsorName(senderName, senderId),
            turn.AnchorKind,
            turn.RecruitmentMessageId,
            turn.ExistingGuests.Select(item => new ZaloSemanticGuestGroundingGuest(
                item.Id,
                item.SponsorSequence,
                item.DisplayName,
                item.Gender,
                item.Level,
                item.Role,
                item.Status.ToString())).ToArray(),
            turn.PendingMissingFields,
            utc,
            utc.ToOffset(TimeSpan.FromHours(7)));
    }

    private async Task<ZaloSemanticGuestMutationPreview> BuildSemanticGuestMutationPreviewAsync(
        string sessionId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(sessionId, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Không đọc được roster để preview guest.");
        var available = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
        var admitted = Math.Min(quantity, available);
        return new ZaloSemanticGuestMutationPreview(
            readiness.EffectiveSlotCount,
            readiness.Capacity,
            admitted,
            Math.Max(0, quantity - admitted),
            readiness.EffectiveSlotCount + admitted);
    }

    private async Task SyncSemanticGuestConversationStateAsync(
        string groupId,
        string senderId,
        string sessionId,
        string lastMessageId,
        string? sourceMessageId,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var guests = await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId &&
                           item.SponsorZaloUserId == senderId &&
                           (item.Status == ZaloGuestReservationStatus.Active || item.Status == ZaloGuestReservationStatus.Waitlisted))
            .OrderBy(item => item.SponsorSequence)
            .ToListAsync(cancellationToken);
        var missing = guests
            .Where(item => item.Gender is null)
            .Select(item => $"gender:#{item.SponsorSequence}")
            .ToArray();
        var store = new ZaloConversationStateV2Store(db);
        if (missing.Length == 0)
        {
            var active = await store.LoadActiveAsync(groupId, senderId, cancellationToken);
            if (active is not null && string.Equals(active.Intent, SemanticGuestConversationIntent, StringComparison.Ordinal))
                await store.CompleteAsync(groupId, senderId, cancellationToken);
            return;
        }

        var minutes = Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:GuestProfileConversationMinutes", 60),
            10,
            180);
        await store.SaveActiveAsync(
            groupId,
            senderId,
            SemanticGuestConversationIntent,
            JsonSerializer.Serialize(new
            {
                sessionId,
                reservationIds = guests.Select(item => item.Id).ToArray(),
                sponsorSequences = guests.Select(item => item.SponsorSequence).ToArray()
            }),
            JsonSerializer.Serialize(missing),
            JsonSerializer.Serialize(guests.Select(item => new
            {
                item.Id,
                item.SponsorSequence,
                item.DisplayName,
                item.Gender,
                item.Level,
                item.Role,
                Status = item.Status.ToString()
            })),
            sourceMessageId,
            lastMessageId,
            DateTimeOffset.UtcNow.AddMinutes(minutes),
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadRecentGuestContextMessageIdsAsync(
        string connectionId,
        string groupId,
        int maxContextMessages,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           item.ReceivedAt >= cutoff)
            .Select(item => new { item.MessageId, item.ReceivedAt })
            .Take(160)
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(item => item.ReceivedAt)
            .Select(item => item.MessageId)
            .TakeLast(Math.Clamp(maxContextMessages * 3, 12, 60))
            .ToArray();
    }

    private async Task SendSemanticGuestReplyAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string sessionId,
        string body,
        string outcome,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var storedIncoming = await EnsureV2IncomingMessageAsync(connectionId, groupId, incoming, cancellationToken);
        if (storedIncoming.BotReplySentAt is not null) return;
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var senderName = FriendlySponsorName(incoming.SenderName, senderId);
        var label = $"@{senderName}";
        var text = $"{label} {body}";
        IReadOnlyList<BridgeOutgoingMention> mentions = senderId.Length == 0
            ? []
            : [new BridgeOutgoingMention(senderId, 0, label.Length)];
        var idempotencyKey = $"guest-semantic:{connectionId}:{incoming.MessageId}";
        var send = await bridge.SendGroupMessageAsync(
            accountId,
            groupId,
            text,
            mentions,
            idempotencyKey: idempotencyKey);
        var providerReplyId = NormalizeProviderMessageId(send.MessageId);
        var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
        await EnsureV2OutboundMessageAsync(
            connectionId,
            groupId,
            persistedReplyId,
            accountId,
            botName,
            text,
            cancellationToken);

        var outbound = await db.ZaloGroupMessages.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.MessageId == persistedReplyId,
            cancellationToken);
        if (outbound is not null)
        {
            outbound.SelectedIntent = ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(sessionId);
            outbound.ReplyOutcome = ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome;
        }
        if (providerReplyId is not null)
        {
            await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                connectionId,
                groupId,
                providerReplyId,
                incoming.MessageId,
                cancellationToken);
        }

        storedIncoming.BotReplySentAt = DateTimeOffset.UtcNow;
        storedIncoming.SelectedIntent = ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(sessionId);
        storedIncoming.ReplyOutcome = outcome;
        storedIncoming.AiCalled = aiCalled;
        storedIncoming.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSemanticGuestTurnWithoutReplyAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string outcome,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var stored = await EnsureV2IncomingMessageAsync(connectionId, groupId, incoming, cancellationToken);
        stored.ReplyOutcome = outcome;
        stored.SelectedIntent = "RecruitmentGuestSemanticNoop";
        stored.AiCalled = aiCalled;
        stored.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ZaloSemanticGuestPlan BuildSemanticGuestFallbackPlan(
        string content,
        ZaloSemanticGuestGroundingSnapshot snapshot)
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse(content);
        if (command is not null)
        {
            if (command.Kind == ZaloRecruitmentGuestCommandKind.Add)
            {
                return new ZaloSemanticGuestPlan(
                    ZaloSemanticGuestActionKind.AddGuests,
                    1,
                    command.Quantity,
                    1,
                    (command.Guests ?? []).Select(item => new ZaloSemanticGuestPlanItem(
                        string.Empty,
                        null,
                        null,
                        item.DisplayName,
                        item.DisplayName is null ? 0 : 1,
                        item.Gender,
                        item.Gender is null ? 0 : 1,
                        item.Level,
                        item.Level is null ? 0 : 1,
                        item.Role,
                        item.Role is null ? 0 : 1,
                        1)).ToArray(),
                    false,
                    string.Empty,
                    "semantic_guest_deterministic_fallback");
            }

            var candidates = snapshot.ExistingGuests;
            ZaloSemanticGuestGroundingGuest? target = null;
            if (command.SponsorSequence is { } seq)
                target = candidates.SingleOrDefault(item => item.SponsorSequence == seq);
            else if (!string.IsNullOrWhiteSpace(command.GuestReference))
            {
                var reference = ZaloBotIntelligence.Normalize(command.GuestReference);
                var matches = candidates.Where(item =>
                        ZaloBotIntelligence.Normalize(item.DisplayName).Contains(reference, StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (matches.Length == 1) target = matches[0];
            }
            else if (candidates.Count == 1)
                target = candidates[0];

            if (command.Kind == ZaloRecruitmentGuestCommandKind.UpdateProfile && target is not null)
            {
                return new ZaloSemanticGuestPlan(
                    ZaloSemanticGuestActionKind.UpdateGuestProfiles,
                    1,
                    1,
                    1,
                    [new ZaloSemanticGuestPlanItem(
                        $"#{target.SponsorSequence}",
                        target.ReservationId,
                        target.SponsorSequence,
                        command.RenameTo,
                        command.RenameTo is null ? 0 : 1,
                        command.Gender,
                        command.Gender is null ? 0 : 1,
                        command.Level,
                        command.Level is null ? 0 : 1,
                        command.Role,
                        command.Role is null ? 0 : 1,
                        1)],
                    false,
                    string.Empty,
                    "semantic_guest_deterministic_fallback");
            }
            if (command.Kind == ZaloRecruitmentGuestCommandKind.Cancel && target is not null)
            {
                return new ZaloSemanticGuestPlan(
                    ZaloSemanticGuestActionKind.CancelGuests,
                    1,
                    1,
                    1,
                    [new ZaloSemanticGuestPlanItem(
                        $"#{target.SponsorSequence}", target.ReservationId, target.SponsorSequence,
                        null, 0, null, 0, null, 0, null, 0, 1)],
                    false,
                    string.Empty,
                    "semantic_guest_deterministic_fallback");
            }
        }

        // Safe fallback for the most common short profile continuation when AI is
        // unavailable. It never guesses a target when more than one guest is pending.
        var normalized = ZaloBotIntelligence.Normalize(content);
        var missingGender = snapshot.ExistingGuests.Where(item => item.Gender is null).OrderBy(item => item.SponsorSequence).ToArray();
        if (missingGender.Length == 1 && normalized is "nam" or "nu")
        {
            var target = missingGender[0];
            return new ZaloSemanticGuestPlan(
                ZaloSemanticGuestActionKind.UpdateGuestProfiles,
                1,
                1,
                1,
                [new ZaloSemanticGuestPlanItem(
                    $"#{target.SponsorSequence}", target.ReservationId, target.SponsorSequence,
                    null, 0,
                    normalized == "nam" ? PlayerGender.Male : PlayerGender.Female, 1,
                    null, 0, null, 0, 1)],
                false,
                string.Empty,
                "semantic_guest_short_profile_fallback");
        }
        if (missingGender.Length == 2 && normalized is "nam nu" or "nu nam")
        {
            var firstMale = normalized == "nam nu";
            return new ZaloSemanticGuestPlan(
                ZaloSemanticGuestActionKind.UpdateGuestProfiles,
                1,
                2,
                1,
                [
                    new ZaloSemanticGuestPlanItem(
                        $"#{missingGender[0].SponsorSequence}", missingGender[0].ReservationId, missingGender[0].SponsorSequence,
                        null, 0, firstMale ? PlayerGender.Male : PlayerGender.Female, 1, null, 0, null, 0, 1),
                    new ZaloSemanticGuestPlanItem(
                        $"#{missingGender[1].SponsorSequence}", missingGender[1].ReservationId, missingGender[1].SponsorSequence,
                        null, 0, firstMale ? PlayerGender.Female : PlayerGender.Male, 1, null, 0, null, 0, 1)
                ],
                false,
                string.Empty,
                "semantic_guest_short_profile_fallback");
        }

        return ZaloSemanticGuestPlan.None("semantic_guest_fallback_no_match");
    }

    private static bool IsSemanticGuestTechnicalFallback(string reason) => reason is
        "semantic_guest_disabled" or
        "semantic_guest_ai_not_configured" or
        "semantic_guest_budget_exhausted" or
        "semantic_guest_ai_error" or
        "semantic_guest_malformed_json";

    private static bool LooksLikePotentialGuestContinuation(string content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content);
        if (normalized.Length == 0 || normalized.Length > 180) return false;
        if (ZaloRecruitmentGuestPolicy.TryParse(content) is not null) return true;
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token is "nam" or "nu" or "kha" or "gioi" or "tot" or "tb" or
                          "trung" or "moi" or "newbie" or "ten" or "nghi" or "huy" || token.StartsWith('#'));
    }

    private static string? TryReadSemanticGuestConversationSessionId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("sessionId", out var node) && node.ValueKind == JsonValueKind.String
                ? node.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ReadStringArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .Take(20)
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildSemanticGuestAddReply(ZaloGuestSignupMutationResult result)
    {
        var all = result.Added.Concat(result.Waitlisted).OrderBy(item => item.SponsorSequence).ToList();
        var names = string.Join(", ", all.Select(FormatSemanticGuest));
        var admitted = result.Added.Count;
        var waiting = result.Waitlisted.Count;
        var state = waiting == 0
            ? $"giữ {admitted} guest vào roster"
            : admitted == 0
                ? $"roster đang full nên đưa {waiting} guest vào danh sách chờ"
                : $"giữ {admitted} guest vào roster, {waiting} guest vào danh sách chờ";
        var roster = result.BeforeEffectiveSlots == result.AfterEffectiveSlots
            ? $"{result.AfterEffectiveSlots}/{result.Capacity}"
            : $"{result.BeforeEffectiveSlots}/{result.Capacity} → {result.AfterEffectiveSlots}/{result.Capacity}";
        var missing = all.Where(item => item.Gender is null).Select(item => $"#{item.SponsorSequence}").ToArray();
        var ask = missing.Length == 0
            ? string.Empty
            : $" Còn {string.Join(", ", missing)} chưa có giới tính; nói `#1 nam`, `#1 nữ`, hoặc với 2 bạn thì `nam nữ`/`2 nam`. Biết trình độ cứ nói luôn kiểu `#1 nam khá`; không biết tui để Mới.";
        var full = result.AfterEffectiveSlots >= result.Capacity
            ? " Kèo đủ slot rồi nên tui dừng réo tuyển thêm."
            : string.Empty;
        return $"Ok 👌 {state} ở {result.SessionName}. {roster}. {names}.{ask}{full}";
    }

    private static string BuildSemanticGuestProfileReply(string sessionName, IEnumerable<ZaloGuestReservation> changed)
    {
        var items = changed.DistinctBy(item => item.Id).OrderBy(item => item.SponsorSequence).ToArray();
        var text = string.Join(", ", items.Select(FormatSemanticGuest));
        return $"Đã cập nhật guest {sessionName}: {text} 👌";
    }

    private static string FormatSemanticGuest(ZaloGuestReservation item)
    {
        var profile = new List<string>();
        if (item.Gender is not null) profile.Add(item.Gender == PlayerGender.Male ? "Nam" : "Nữ");
        if (item.Level is not null) profile.Add(item.Level switch
        {
            PlayerLevel.Good => "Khá",
            PlayerLevel.Average => "Trung bình",
            _ => "Mới"
        });
        if (item.Role is not null && item.Role != PlayerRole.New) profile.Add(item.Role.ToString());
        return $"#{item.SponsorSequence} {item.DisplayName}{(profile.Count == 0 ? string.Empty : $" ({string.Join(" / ", profile)})")}";
    }
}
