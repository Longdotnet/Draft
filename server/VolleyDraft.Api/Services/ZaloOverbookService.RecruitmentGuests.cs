using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    public async Task<int> ProcessRecruitmentGuestTurnsDueAsync(
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastEnabled", true))
            return 0;

        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-12);
        var rows = await db.ZaloGroupMessages
            .Where(message =>
                !message.IsFromBot &&
                message.BotReplySentAt == null &&
                message.ReplyOutcome == null &&
                message.ReceivedAt >= cutoff)
            .Take(400)
            .ToListAsync(cancellationToken);
        var candidates = rows
            .OrderBy(message => message.ReceivedAt)
            .Take(80)
            .ToList();
        if (candidates.Count == 0) return 0;

        var handled = 0;
        foreach (var message in candidates)
        {
            var command = ZaloRecruitmentGuestPolicy.TryParse(message.Content);
            if (command is null) continue;

            var connection = await db.ZaloConnections
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == message.ZaloConnectionId, cancellationToken);
            if (connection is null) continue;

            var resolution = await ResolveRecruitmentGuestSessionAsync(
                connection.Id,
                message.GroupId,
                message.MessageId,
                message.Content,
                message.SenderId,
                command,
                cancellationToken);
            if (resolution.Session is null)
            {
                if (resolution.AmbiguousChoices.Count > 1)
                {
                    var choices = string.Join(", ", resolution.AmbiguousChoices.Take(4).Select(FormatDraftSessionChoice));
                    await SendRecruitmentGuestReplyAsync(
                        connection,
                        message,
                        $"Ông đang nói guest cho kèo nào: {choices}? Reply đúng tin tuyển người của kèo đó, hoặc ghi T4/T6/ngày trong câu giúp tui nha.",
                        "guest_session_ambiguous",
                        cancellationToken);
                    handled += 1;
                }
                continue;
            }

            var session = resolution.Session;
            try
            {
                string reply;
                string outcome;
                var service = new ZaloGuestReservationService(db);
                if (command.Kind == ZaloRecruitmentGuestCommandKind.Add)
                {
                    var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
                    if (!sync.Success)
                    {
                        await SendRecruitmentGuestReplyAsync(
                            connection,
                            message,
                            $"Tui hiểu ông muốn +{command.Quantity} cho {session.Name}, nhưng chưa sync được đúng poll nên chưa giữ slot để khỏi cộng nhầm nha. Thử lại chút nữa giúp tui.",
                            "guest_poll_sync_failed",
                            cancellationToken);
                        handled += 1;
                        continue;
                    }

                    // If a previously named outside guest has since joined the group and
                    // voted, collapse the old manual placeholder before counting room for
                    // this new +1/+2. Only exact unique names are reconciled.
                    await new ZaloGuestIdentityReconciler(db)
                        .ReconcileAsync(session.Id, cancellationToken);

                    var result = await service.AddAsync(
                        session,
                        ZaloOverbookLogic.NormalizeId(message.SenderId),
                        FriendlySponsorName(message.SenderName, message.SenderId),
                        message.MessageId,
                        resolution.RecruitmentMessageId,
                        command,
                        cancellationToken);
                    reply = BuildGuestAddReply(result);
                    outcome = result.Idempotent ? "guest_signup_idempotent" : "guest_signup_added";
                }
                else if (command.Kind == ZaloRecruitmentGuestCommandKind.Cancel)
                {
                    var result = await service.CancelAsync(
                        session,
                        ZaloOverbookLogic.NormalizeId(message.SenderId),
                        command,
                        cancellationToken);
                    if (result.NeedsClarification)
                    {
                        reply = result.Clarification ?? "Nói rõ guest nào nghỉ giúp tui nha.";
                        outcome = "guest_cancel_clarification";
                    }
                    else
                    {
                        var names = string.Join(", ", result.Changed.Select(item => item.DisplayName));
                        reply = $"Ok, tui rút {names} khỏi {session.Name}. Slot trống sẽ ưu tiên guest đang chờ trước; nếu vẫn thiếu thì luồng kiếm thêm sẽ réo lại theo cooldown.";
                        outcome = "guest_cancelled";
                    }
                }
                else
                {
                    var result = await service.UpdateProfileAsync(
                        session,
                        ZaloOverbookLogic.NormalizeId(message.SenderId),
                        command,
                        cancellationToken);
                    if (result.NeedsClarification)
                    {
                        reply = result.Clarification ?? "Nói rõ guest nào cần cập nhật giúp tui nha.";
                        outcome = "guest_profile_clarification";
                    }
                    else
                    {
                        var changed = string.Join(", ", result.Changed.Select(item =>
                            $"#{item.SponsorSequence} {item.DisplayName}{(item.Gender is null ? string.Empty : $" ({GuestGender(item.Gender)})")}"));
                        reply = $"Đã cập nhật guest {session.Name}: {changed} 👌";
                        outcome = "guest_profile_updated";
                    }
                }

                await SendRecruitmentGuestReplyAsync(
                    connection,
                    message,
                    reply,
                    outcome,
                    cancellationToken);
                handled += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Recruitment guest turn failed Session={SessionId} Message={MessageId}",
                    session.Id,
                    message.MessageId);
                await SendRecruitmentGuestReplyAsync(
                    connection,
                    message,
                    $"Tui hiểu ý guest cho {session.Name} nhưng thao tác chưa chạy an toàn được: {Truncate(exception.Message, 180)}",
                    "guest_mutation_failed",
                    cancellationToken);
                handled += 1;
            }
        }
        return handled;
    }

    public async Task<int> ProcessGuestWaitlistDueAsync(CancellationToken cancellationToken = default)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var service = new ZaloGuestReservationService(db);
        var sessionIds = await service.ListSessionsWithWaitingAsync(cancellationToken);
        var sent = 0;
        foreach (var sessionId in sessionIds)
        {
            var session = await db.MatchSessions
                .AsNoTracking()
                .Include(item => item.ZaloConnection)
                .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
            if (session?.ZaloConnection is null || !session.BotEnabled || string.IsNullOrWhiteSpace(session.ZaloGroupId))
                continue;

            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success) continue;
            await new ZaloGuestIdentityReconciler(db)
                .ReconcileAsync(sessionId, cancellationToken);
            var promotions = await service.PromoteWaitingAsync(sessionId, cancellationToken);
            foreach (var promotion in promotions)
            {
                var label = $"@{FriendlySponsorName(promotion.SponsorDisplayName, promotion.SponsorZaloUserId)}";
                var text = $"{label} có slot trống ở {promotion.SessionName} nên tui chuyển {promotion.DisplayName} từ guest waitlist vào roster rồi nha 👌 Hiện {promotion.EffectiveSlots}/{promotion.Capacity}.";
                var idempotencyKey = $"guest-promote:{promotion.SessionId}:{promotion.DisplayName}:{promotion.EffectiveSlots}";
                try
                {
                    await bridge.SendGroupMessageAsync(
                        session.ZaloConnection.AccountZaloId,
                        session.ZaloGroupId!,
                        text,
                        [new BridgeOutgoingMention(promotion.SponsorZaloUserId, 0, label.Length)],
                        idempotencyKey: idempotencyKey);
                    sent += 1;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Could not announce guest waitlist promotion Session={SessionId}", promotion.SessionId);
                }
            }
        }
        return sent;
    }

    private async Task<(MatchSession? Session, string? RecruitmentMessageId, IReadOnlyList<MatchSession> AmbiguousChoices)> ResolveRecruitmentGuestSessionAsync(
        string connectionId,
        string groupId,
        string incomingMessageId,
        string content,
        string sponsorZaloUserId,
        ZaloRecruitmentGuestCommand command,
        CancellationToken cancellationToken)
    {
        var upcoming = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.ZaloConnection != null &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                (item.StartTime == null || item.StartTime > DateTimeOffset.UtcNow))
            .ToListAsync(cancellationToken);
        if (upcoming.Count == 0) return (null, null, []);

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
                    item.IsFromBot &&
                    item.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome,
                    cancellationToken);
            if (anchor is not null)
            {
                var anchoredId = ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId(anchor.SelectedIntent);
                var anchored = upcoming.SingleOrDefault(item => string.Equals(item.Id, anchoredId, StringComparison.Ordinal));
                if (anchored is not null) return (anchored, anchor.MessageId, []);
            }

            if (!string.IsNullOrWhiteSpace(relation.QuotedContentSnapshot))
            {
                var quoted = ZaloBotIntelligence.Normalize(relation.QuotedContentSnapshot);
                var matches = upcoming.Where(item => quoted.Contains(ZaloBotIntelligence.Normalize(item.Name), StringComparison.Ordinal)).ToList();
                if (matches.Count == 1) return (matches[0], relation.ToMessageId, []);
            }
        }

        var references = upcoming.Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime)).ToList();
        var explicitMatches = ZaloBotIntelligence.ResolveSessionReference(ZaloBotIntelligence.Normalize(content), references);
        var matched = upcoming.Where(item => explicitMatches.Contains(item.Id, StringComparer.Ordinal)).ToList();
        if (matched.Count == 1) return (matched[0], null, []);
        if (matched.Count > 1) return (null, null, matched);

        if (command.Kind is ZaloRecruitmentGuestCommandKind.Cancel or ZaloRecruitmentGuestCommandKind.UpdateProfile)
        {
            var sponsorId = ZaloOverbookLogic.NormalizeId(sponsorZaloUserId);
            var guestSessionIds = await db.ZaloGuestReservations
                .AsNoTracking()
                .Where(item => item.SponsorZaloUserId == sponsorId &&
                               (item.Status == ZaloGuestReservationStatus.Active || item.Status == ZaloGuestReservationStatus.Waitlisted))
                .Select(item => item.SessionId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var guestSessions = upcoming.Where(item => guestSessionIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
            if (guestSessions.Count == 1) return (guestSessions[0], null, []);
            if (guestSessions.Count > 1) return (null, null, guestSessions);
        }

        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var recruiting = new List<MatchSession>();
        foreach (var session in upcoming)
        {
            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
                recruiting.Add(session);
        }
        if (recruiting.Count == 1) return (recruiting[0], null, []);
        return recruiting.Count > 1 ? (null, null, recruiting) : (null, null, []);
    }

    private async Task SendRecruitmentGuestReplyAsync(
        ZaloConnection connection,
        ZaloGroupMessage incoming,
        string body,
        string outcome,
        CancellationToken cancellationToken)
    {
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var senderName = FriendlySponsorName(incoming.SenderName, senderId);
        var label = $"@{senderName}";
        var text = $"{label} {body}";
        IReadOnlyList<BridgeOutgoingMention> mentions = senderId.Length == 0
            ? []
            : [new BridgeOutgoingMention(senderId, 0, label.Length)];
        var idempotencyKey = $"guest-recruit:{connection.Id}:{incoming.MessageId}";
        var send = await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            incoming.GroupId,
            text,
            mentions,
            idempotencyKey: idempotencyKey);
        var providerReplyId = NormalizeProviderMessageId(send.MessageId);
        var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
        await EnsureV2OutboundMessageAsync(
            connection.Id,
            incoming.GroupId,
            persistedReplyId,
            connection.AccountZaloId,
            connection.DisplayName,
            text,
            cancellationToken);
        if (providerReplyId is not null)
        {
            await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                connection.Id,
                incoming.GroupId,
                providerReplyId,
                incoming.MessageId,
                cancellationToken);
        }

        incoming.BotReplySentAt = DateTimeOffset.UtcNow;
        incoming.SelectedIntent = "RecruitmentGuest";
        incoming.ReplyOutcome = outcome;
        incoming.AiCalled = false;
        incoming.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildGuestAddReply(ZaloGuestSignupMutationResult result)
    {
        var admitted = result.Added.Count;
        var waiting = result.Waitlisted.Count;
        var names = result.Added.Concat(result.Waitlisted)
            .Select(item => $"#{item.SponsorSequence} {item.DisplayName}")
            .ToList();
        var roster = result.BeforeEffectiveSlots == result.AfterEffectiveSlots
            ? $"{result.AfterEffectiveSlots}/{result.Capacity}"
            : $"{result.BeforeEffectiveSlots}/{result.Capacity} → {result.AfterEffectiveSlots}/{result.Capacity}";
        var state = waiting == 0
            ? $"giữ {admitted} guest vào roster"
            : admitted == 0
                ? $"roster đang full nên đưa {waiting} guest vào danh sách chờ"
                : $"giữ {admitted} guest vào roster, {waiting} guest còn lại vào danh sách chờ";
        var missingGender = result.Added.Concat(result.Waitlisted)
            .Where(item => item.Gender is null)
            .Select(item => $"#{item.SponsorSequence}")
            .ToList();
        var profileNote = missingGender.Count == 0
            ? string.Empty
            : $" Trước draft cần bổ sung giới tính cho {string.Join(", ", missingGender)}; nói kiểu `bạn #1 nam`/`nữ` là được.";
        var fullNote = result.AfterEffectiveSlots >= result.Capacity
            ? " Kèo đủ slot rồi nên bot sẽ im recruitment; nếu sau đó hụt slot mà quyết định kiếm thêm vẫn còn thì bot réo lại theo cooldown."
            : string.Empty;
        return $"Ok 👌 {state} ở {result.SessionName}. {roster}. {string.Join(", ", names)}.{profileNote}{fullNote}";
    }

    private static string FriendlySponsorName(string? displayName, string fallback)
    {
        var value = (displayName ?? string.Empty).Trim().TrimStart('@');
        return value.Length > 0 ? value : fallback;
    }

    private static string GuestGender(PlayerGender? gender) => gender switch
    {
        PlayerGender.Male => "nam",
        PlayerGender.Female => "nữ",
        _ => "chưa rõ"
    };
}
