using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloStatefulGuestPendingKind
{
    None,
    AddQuantity,
    UpdateTarget,
    UpdateFields,
    CancelTarget
}

internal static class ZaloStatefulGuestFollowupPolicy
{
    internal static ZaloStatefulGuestPendingKind PendingKind(string? outcome) => outcome switch
    {
        "semantic_guest_quantity_ambiguous" => ZaloStatefulGuestPendingKind.AddQuantity,
        "semantic_guest_update_target_ambiguous" or
        "semantic_guest_update_target_low_confidence" or
        "semantic_guest_invalid_guest_target" => ZaloStatefulGuestPendingKind.UpdateTarget,
        "semantic_guest_profile_fields_ambiguous" => ZaloStatefulGuestPendingKind.UpdateFields,
        "semantic_guest_cancel_target_ambiguous" => ZaloStatefulGuestPendingKind.CancelTarget,
        _ => ZaloStatefulGuestPendingKind.None
    };

    internal static IReadOnlyList<string> MissingFields(ZaloStatefulGuestPendingKind kind) => kind switch
    {
        ZaloStatefulGuestPendingKind.AddQuantity => ["pendingAction:AddGuests", "quantity"],
        ZaloStatefulGuestPendingKind.UpdateTarget => ["pendingAction:UpdateGuestProfiles", "guestTarget"],
        ZaloStatefulGuestPendingKind.UpdateFields => ["pendingAction:UpdateGuestProfiles", "profileFields"],
        ZaloStatefulGuestPendingKind.CancelTarget => ["pendingAction:CancelGuests", "guestTarget"],
        _ => []
    };

    internal static bool IsSemanticGuestTerminal(string? outcome) =>
        PendingKind(outcome) != ZaloStatefulGuestPendingKind.None || outcome is
        "guest_semantic_added" or
        "guest_semantic_add_idempotent" or
        "guest_semantic_profile_updated" or
        "guest_semantic_cancelled" or
        "guest_semantic_pending_abandoned" or
        "guest_semantic_poll_sync_failed" or
        "guest_semantic_execution_failed";

    internal static bool IsRecentAdd(string? outcome) => outcome is
        "guest_semantic_added" or "guest_semantic_add_idempotent";

    internal static bool IsFresh(
        DateTimeOffset updatedAt,
        DateTimeOffset now,
        int minutes) => updatedAt >= now.AddMinutes(-Math.Max(1, minutes));

    internal static bool IsPendingAbandon(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized is "thoi" or "bo qua" or "khoi" or "cancel" or "huy" or
               "khong them nua" or "thoi khong them nua" or "bo di";
    }

    internal static int? TryParsePendingQuantity(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty)
            .Replace("@npc", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized switch
        {
            "1" or "+1" or "mot" or "1 ban" or "mot ban" => 1,
            "2" or "+2" or "hai" or "2 ban" or "hai ban" => 2,
            _ => null
        };
    }
}

public sealed partial class ZaloOverbookService
{
    private sealed record StatefulSemanticGuestContext(
        SemanticGuestTurnContext Turn,
        string SourceMessageId,
        ZaloStatefulGuestPendingKind PendingKind,
        bool TechnicalFallbackShouldExplain);

    /// <summary>
    /// Handles the stateful turns that cannot be safely expressed as a keyword gate:
    /// 1) a clarification answer such as bare "2" after the bot asked +1/+2,
    /// 2) natural follow-up profile language shortly after a guest mutation,
    /// 3) correction/undo against only the reservations created by the most recent add.
    /// It runs before the older semantic guest pre-route and before Ambient/GeneralChat.
    /// </summary>
    private async Task<bool> TryHandleStatefulSemanticGuestFollowupAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(incoming.Content) || incoming.Content.Length > 500)
            return false;

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (senderId.Length == 0) return false;
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);

        // Multi-session task stack is consulted first only for an explicit pending
        // clarification. Profile tasks are held as a fallback so the narrower recent
        // mutation correction window below keeps priority for undo/"à nhầm" turns.
        var taskStackContext = await ResolveGuestTaskStackContextAsync(
            connectionId,
            groupId,
            senderId,
            incoming.Content,
            cancellationToken);
        StatefulSemanticGuestContext? context = taskStackContext?.PendingKind != ZaloStatefulGuestPendingKind.None
            ? taskStackContext
            : await ResolveStatefulSemanticGuestContextAsync(
                connectionId,
                groupId,
                senderId,
                incoming.MessageId,
                cancellationToken);
        context ??= taskStackContext;
        if (context is null) return false;

        if (context.PendingKind != ZaloStatefulGuestPendingKind.None &&
            ZaloStatefulGuestFollowupPolicy.IsPendingAbandon(incoming.Content))
        {
            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                context.Turn.Session.Id,
                "Ok, tui bỏ yêu cầu guest đang hỏi dở nha. Roster chưa bị đổi thêm gì.",
                "guest_semantic_pending_abandoned",
                aiCalled: false,
                cancellationToken);
            return true;
        }

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
        var snapshot = await BuildSemanticGuestSnapshotAsync(
            context.Turn,
            senderId,
            incoming.SenderName,
            cancellationToken);
        var planner = new ZaloSemanticGuestPlanner(configuration, logger);
        var plan = await planner.PlanAsync(
            connectionId,
            groupId,
            incoming.Content,
            conversation,
            snapshot,
            actionSettings,
            cancellationToken);
        var technicalFallback = IsSemanticGuestTechnicalFallback(plan.Reason);
        var aiCalled = !technicalFallback;
        if (technicalFallback)
            plan = BuildStatefulSemanticGuestFallbackPlan(incoming.Content, snapshot, context.PendingKind);

        if (plan.Action == ZaloSemanticGuestActionKind.None)
        {
            if (technicalFallback && context.TechnicalFallbackShouldExplain)
            {
                var help = context.PendingKind switch
                {
                    ZaloStatefulGuestPendingKind.AddQuantity =>
                        "Tui đang không đọc được câu tự nhiên ổn định. Ông chỉ cần nói `1` hoặc `2` để chốt số bạn nha.",
                    ZaloStatefulGuestPendingKind.CancelTarget or ZaloStatefulGuestPendingKind.UpdateTarget =>
                        "Tui đang không đọc được câu tự nhiên ổn định. Nói rõ `#1`/`#2` giúp tui nha.",
                    ZaloStatefulGuestPendingKind.UpdateFields =>
                        "Tui đang không đọc được câu tự nhiên ổn định. Nói kiểu `#1 nam`, `#1 nữ`, `#1 nam khá` giúp tui nha.",
                    _ => ""
                };
                if (help.Length > 0)
                {
                    await SendSemanticGuestReplyAsync(
                        connectionId,
                        accountId,
                        botName,
                        groupId,
                        incoming,
                        context.Turn.Session.Id,
                        help,
                        "guest_semantic_fallback_guidance",
                        aiCalled: false,
                        cancellationToken);
                    return true;
                }
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
                context.Turn.Session.Id,
                clarification,
                validation.Reason,
                aiCalled,
                cancellationToken);
            return true;
        }

        return await ExecuteStatefulSemanticGuestMutationAsync(
            connectionId,
            accountId,
            botName,
            groupId,
            senderId,
            incoming,
            context,
            validation,
            aiCalled,
            cancellationToken);
    }

    private async Task<StatefulSemanticGuestContext?> ResolveStatefulSemanticGuestContextAsync(
        string connectionId,
        string groupId,
        string senderId,
        string currentMessageId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingMinutes = Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:GuestPendingClarificationMinutes", 15),
            5,
            60);
        var correctionMinutes = Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:GuestCorrectionMinutes", 10),
            3,
            30);
        var naturalMinutes = Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:GuestNaturalFollowupMinutes", 15),
            5,
            30);
        var cutoff = now.AddMinutes(-Math.Max(pendingMinutes, Math.Max(correctionMinutes, naturalMinutes)));

        var recentRows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                !item.IsFromBot &&
                item.SenderId == senderId &&
                item.MessageId != currentMessageId &&
                item.ReceivedAt >= cutoff &&
                item.ReplyOutcome != null)
            .Take(80)
            .ToListAsync(cancellationToken);
        var ordered = recentRows
            .OrderByDescending(item => item.ReceivedAt)
            .ThenByDescending(item => item.MessageId, StringComparer.Ordinal)
            .ToList();

        var latestTerminal = ordered.FirstOrDefault(item =>
            ZaloStatefulGuestFollowupPolicy.IsSemanticGuestTerminal(item.ReplyOutcome));
        if (latestTerminal is not null &&
            ZaloStatefulGuestFollowupPolicy.IsFresh(latestTerminal.ReceivedAt, now, pendingMinutes))
        {
            var pendingKind = ZaloStatefulGuestFollowupPolicy.PendingKind(latestTerminal.ReplyOutcome);
            if (pendingKind != ZaloStatefulGuestPendingKind.None)
            {
                var sessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(latestTerminal.SelectedIntent);
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    var session = await LoadStatefulGuestSessionAsync(
                        connectionId,
                        groupId,
                        sessionId!,
                        cancellationToken);
                    if (session is not null)
                    {
                        var recruitmentMessageId = pendingKind == ZaloStatefulGuestPendingKind.AddQuantity
                            ? await ResolvePendingRecruitmentMessageIdAsync(
                                connectionId,
                                groupId,
                                senderId,
                                session.Id,
                                ordered,
                                cancellationToken)
                            : null;
                        if (pendingKind != ZaloStatefulGuestPendingKind.AddQuantity ||
                            !string.IsNullOrWhiteSpace(recruitmentMessageId))
                        {
                            var guests = pendingKind == ZaloStatefulGuestPendingKind.AddQuantity
                                ? new List<ZaloGuestReservation>()
                                : await LoadStatefulSponsorGuestsAsync(session.Id, senderId, cancellationToken);
                            return new StatefulSemanticGuestContext(
                                new SemanticGuestTurnContext(
                                    session,
                                    ZaloSemanticGuestAnchorKind.PendingGuestAction,
                                    recruitmentMessageId,
                                    guests,
                                    ZaloStatefulGuestFollowupPolicy.MissingFields(pendingKind)),
                                latestTerminal.MessageId,
                                pendingKind,
                                TechnicalFallbackShouldExplain: true);
                        }
                    }
                }
            }
        }

        var latestAdd = ordered.FirstOrDefault(item =>
            ZaloStatefulGuestFollowupPolicy.IsRecentAdd(item.ReplyOutcome) &&
            ZaloStatefulGuestFollowupPolicy.IsFresh(item.ReceivedAt, now, correctionMinutes));
        if (latestAdd is not null)
        {
            var sessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(latestAdd.SelectedIntent);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var recentGuests = await db.ZaloGuestReservations
                    .AsNoTracking()
                    .Where(item =>
                        item.SessionId == sessionId &&
                        item.SponsorZaloUserId == senderId &&
                        item.SourceMessageId == latestAdd.MessageId &&
                        (item.Status == ZaloGuestReservationStatus.Active ||
                         item.Status == ZaloGuestReservationStatus.Waitlisted))
                    .OrderBy(item => item.SponsorSequence)
                    .ToListAsync(cancellationToken);
                if (recentGuests.Count > 0)
                {
                    var session = await LoadStatefulGuestSessionAsync(
                        connectionId,
                        groupId,
                        sessionId!,
                        cancellationToken);
                    if (session is not null)
                    {
                        return new StatefulSemanticGuestContext(
                            new SemanticGuestTurnContext(
                                session,
                                ZaloSemanticGuestAnchorKind.RecentGuestMutation,
                                recentGuests[0].RecruitmentMessageId,
                                recentGuests,
                                ["recentMutationCorrection"]),
                            latestAdd.MessageId,
                            ZaloStatefulGuestPendingKind.None,
                            TechnicalFallbackShouldExplain: false);
                    }
                }
            }
        }

        var active = await new ZaloConversationStateV2Store(db)
            .LoadActiveAsync(groupId, senderId, cancellationToken);
        if (active is not null &&
            string.Equals(active.Intent, SemanticGuestConversationIntent, StringComparison.Ordinal) &&
            ZaloStatefulGuestFollowupPolicy.IsFresh(active.UpdatedAt, now, naturalMinutes))
        {
            var sessionId = TryReadSemanticGuestConversationSessionId(active.CollectedArgumentsJson);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var session = await LoadStatefulGuestSessionAsync(
                    connectionId,
                    groupId,
                    sessionId!,
                    cancellationToken);
                if (session is not null)
                {
                    var guests = await LoadStatefulSponsorGuestsAsync(session.Id, senderId, cancellationToken);
                    if (guests.Count > 0)
                    {
                        return new StatefulSemanticGuestContext(
                            new SemanticGuestTurnContext(
                                session,
                                ZaloSemanticGuestAnchorKind.ActiveGuestConversation,
                                null,
                                guests,
                                ReadStringArray(active.MissingArgumentsJson)),
                            active.LastMessageId ?? active.SourceMessageId ?? string.Empty,
                            ZaloStatefulGuestPendingKind.None,
                            TechnicalFallbackShouldExplain: false);
                    }
                }
            }
        }

        return null;
    }

    private async Task<MatchSession?> LoadStatefulGuestSessionAsync(
        string connectionId,
        string groupId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item =>
                item.Id == sessionId &&
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.ZaloConnection != null &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                item.StartTime != null &&
                item.StartTime > now,
                cancellationToken);
    }

    private async Task<List<ZaloGuestReservation>> LoadStatefulSponsorGuestsAsync(
        string sessionId,
        string senderId,
        CancellationToken cancellationToken) =>
        await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item =>
                item.SessionId == sessionId &&
                item.SponsorZaloUserId == senderId &&
                (item.Status == ZaloGuestReservationStatus.Active ||
                 item.Status == ZaloGuestReservationStatus.Waitlisted))
            .OrderBy(item => item.SponsorSequence)
            .ToListAsync(cancellationToken);

    private async Task<string?> ResolvePendingRecruitmentMessageIdAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        IReadOnlyList<ZaloGroupMessage> recentRows,
        CancellationToken cancellationToken)
    {
        foreach (var row in recentRows
                     .Where(item =>
                         item.SenderId == senderId &&
                         string.Equals(
                             ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(item.SelectedIntent),
                             sessionId,
                             StringComparison.Ordinal))
                     .Take(20))
        {
            var relation = await new ZaloMessageGraphStore(db)
                .LoadRelationAsync(connectionId, groupId, row.MessageId, cancellationToken);
            if (relation?.RelationType != "ReplyTo" || string.IsNullOrWhiteSpace(relation.ToMessageId))
                continue;
            var anchor = await db.ZaloGroupMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    item.MessageId == relation.ToMessageId &&
                    item.IsFromBot,
                    cancellationToken);
            if (anchor?.ReplyOutcome != ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome)
                continue;
            var groundedSessionId = ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId(anchor.SelectedIntent);
            if (string.Equals(groundedSessionId, sessionId, StringComparison.Ordinal))
                return anchor.MessageId;
        }
        return null;
    }

    private static ZaloSemanticGuestPlan BuildStatefulSemanticGuestFallbackPlan(
        string content,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloStatefulGuestPendingKind pendingKind)
    {
        if (pendingKind == ZaloStatefulGuestPendingKind.AddQuantity)
        {
            var quantity = ZaloStatefulGuestFollowupPolicy.TryParsePendingQuantity(content);
            if (quantity is not null)
            {
                return new ZaloSemanticGuestPlan(
                    ZaloSemanticGuestActionKind.AddGuests,
                    1,
                    quantity,
                    1,
                    Enumerable.Range(0, quantity.Value)
                        .Select(_ => new ZaloSemanticGuestPlanItem(
                            string.Empty, null, null, null, 0, null, 0, null, 0, null, 0, 1))
                        .ToArray(),
                    false,
                    string.Empty,
                    "semantic_guest_pending_quantity_fallback");
            }
        }

        if (snapshot.AnchorKind == ZaloSemanticGuestAnchorKind.RecentGuestMutation)
        {
            var normalized = ZaloBotIntelligence.Normalize(content);
            var guests = snapshot.ExistingGuests.OrderBy(item => item.SponsorSequence).ToArray();
            if (guests.Length > 0 &&
                (normalized.Contains("undo", StringComparison.Ordinal) ||
                 normalized.Contains("bo 2 ban hoi nay", StringComparison.Ordinal) ||
                 normalized.Contains("bo hai ban hoi nay", StringComparison.Ordinal)))
            {
                return CancelRecentGuestsFallback(guests);
            }
            if (guests.Length == 2 &&
                (normalized.Contains("chi +1", StringComparison.Ordinal) ||
                 normalized.Contains("chi 1", StringComparison.Ordinal)))
            {
                return CancelRecentGuestsFallback([guests[1]]);
            }
            if (guests.Length == 1 && normalized.Contains("nham", StringComparison.Ordinal))
            {
                PlayerGender? gender = normalized.Contains(" nu", StringComparison.Ordinal) || normalized.EndsWith("nu", StringComparison.Ordinal)
                    ? PlayerGender.Female
                    : normalized.Contains(" nam", StringComparison.Ordinal) || normalized.EndsWith("nam", StringComparison.Ordinal)
                        ? PlayerGender.Male
                        : null;
                if (gender is not null)
                {
                    var guest = guests[0];
                    return new ZaloSemanticGuestPlan(
                        ZaloSemanticGuestActionKind.UpdateGuestProfiles,
                        1,
                        1,
                        1,
                        [new ZaloSemanticGuestPlanItem(
                            $"#{guest.SponsorSequence}", guest.ReservationId, guest.SponsorSequence,
                            null, 0, gender, 1, null, 0, null, 0, 1)],
                        false,
                        string.Empty,
                        "semantic_guest_recent_correction_fallback");
                }
            }
        }

        return BuildSemanticGuestFallbackPlan(content, snapshot);
    }

    private static ZaloSemanticGuestPlan CancelRecentGuestsFallback(
        IReadOnlyList<ZaloSemanticGuestGroundingGuest> guests) => new(
        ZaloSemanticGuestActionKind.CancelGuests,
        1,
        guests.Count,
        1,
        guests.Select(item => new ZaloSemanticGuestPlanItem(
            $"#{item.SponsorSequence}",
            item.ReservationId,
            item.SponsorSequence,
            null,
            0,
            null,
            0,
            null,
            0,
            null,
            0,
            1)).ToArray(),
        false,
        string.Empty,
        "semantic_guest_recent_cancel_fallback");

    private async Task<bool> ExecuteStatefulSemanticGuestMutationAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        StatefulSemanticGuestContext context,
        ZaloSemanticGuestValidationResult validation,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new ZaloGuestReservationService(db);
            string reply;
            string outcome;

            if (validation.Action == ZaloSemanticGuestActionKind.AddGuests)
            {
                var sync = await RefreshLinkedPollForDraftReminderAsync(context.Turn.Session, cancellationToken);
                if (!sync.Success)
                {
                    await SendSemanticGuestReplyAsync(
                        connectionId,
                        accountId,
                        botName,
                        groupId,
                        incoming,
                        context.Turn.Session.Id,
                        $"Tui hiểu ông muốn +{validation.Quantity} cho {context.Turn.Session.Name}, nhưng chưa sync được poll thật nên chưa giữ slot để khỏi cộng nhầm nha.",
                        "guest_semantic_poll_sync_failed",
                        aiCalled,
                        cancellationToken);
                    return true;
                }

                await new ZaloGuestIdentityReconciler(db)
                    .ReconcileAsync(context.Turn.Session.Id, cancellationToken);
                var preview = await BuildSemanticGuestMutationPreviewAsync(
                    context.Turn.Session.Id,
                    validation.Quantity,
                    cancellationToken);
                logger.LogInformation(
                    "Stateful semantic guest preview Session={SessionId} Sender={SenderId} Before={Before}/{Capacity} Add={Add} Admit={Admit} Wait={Wait} After={After}",
                    context.Turn.Session.Id,
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
                    context.Turn.Session,
                    senderId,
                    FriendlySponsorName(incoming.SenderName, senderId),
                    incoming.MessageId,
                    context.Turn.RecruitmentMessageId,
                    command,
                    cancellationToken);
                reply = BuildSemanticGuestAddReply(result);
                outcome = result.Idempotent ? "guest_semantic_add_idempotent" : "guest_semantic_added";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    context.Turn.Session.Id,
                    incoming.MessageId,
                    context.Turn.RecruitmentMessageId,
                    cancellationToken);
            }
            else if (validation.Action == ZaloSemanticGuestActionKind.UpdateGuestProfiles)
            {
                var changed = new List<ZaloGuestReservation>();
                foreach (var item in validation.Items)
                {
                    var result = await service.UpdateProfileAsync(
                        context.Turn.Session,
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
                            context.Turn.Session.Id,
                            result.Clarification ?? "Tui chưa xác định chắc guest nào cần cập nhật.",
                            "semantic_guest_update_target_ambiguous",
                            aiCalled,
                            cancellationToken);
                        return true;
                    }
                    changed.AddRange(result.Changed);
                }
                reply = BuildSemanticGuestProfileReply(context.Turn.Session.Name, changed);
                outcome = "guest_semantic_profile_updated";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    context.Turn.Session.Id,
                    incoming.MessageId,
                    context.Turn.RecruitmentMessageId,
                    cancellationToken);
            }
            else
            {
                var changed = new List<ZaloGuestReservation>();
                foreach (var item in validation.Items)
                {
                    var result = await service.CancelAsync(
                        context.Turn.Session,
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
                            context.Turn.Session.Id,
                            result.Clarification ?? "Tui chưa xác định chắc guest nào nghỉ.",
                            "semantic_guest_cancel_target_ambiguous",
                            aiCalled,
                            cancellationToken);
                        return true;
                    }
                    changed.AddRange(result.Changed);
                }

                var promotions = await service.PromoteWaitingAsync(context.Turn.Session.Id, cancellationToken);
                var readiness = await new ZaloDraftReadinessService(db)
                    .BuildAsync(context.Turn.Session.Id, cancellationToken: cancellationToken);
                var names = string.Join(", ", changed.DistinctBy(item => item.Id).Select(item => item.DisplayName));
                var rosterText = readiness is null
                    ? string.Empty
                    : $" Roster hiện {readiness.EffectiveSlotCount}/{readiness.Capacity}.";
                var promotionText = promotions.Count == 0
                    ? ""
                    : $" Tui đã đẩy {string.Join(", ", promotions.Select(item => item.DisplayName))} từ guest waitlist lên trước.";
                var recruitText = readiness is not null && readiness.EffectiveSlotCount < readiness.Capacity
                    ? " Nếu KeepRecruiting đang bật thì luồng kiếm thêm sẽ tiếp tục theo cooldown."
                    : "";
                reply = $"Ok, tui rút {names} khỏi {context.Turn.Session.Name}.{promotionText}{rosterText}{recruitText}";
                outcome = "guest_semantic_cancelled";
                await SyncSemanticGuestConversationStateAsync(
                    groupId,
                    senderId,
                    context.Turn.Session.Id,
                    incoming.MessageId,
                    context.Turn.RecruitmentMessageId,
                    cancellationToken);
            }

            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                context.Turn.Session.Id,
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
                "Stateful semantic guest execution failed Session={SessionId} Message={MessageId}",
                context.Turn.Session.Id,
                incoming.MessageId);
            await SendSemanticGuestReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                context.Turn.Session.Id,
                "Tui hiểu ý guest nhưng thao tác DB chưa chạy an toàn được, nên tui chưa đổi roster nha. Thử lại chút nữa giúp tui.",
                "guest_semantic_execution_failed",
                aiCalled,
                cancellationToken);
            return true;
        }
    }
}
