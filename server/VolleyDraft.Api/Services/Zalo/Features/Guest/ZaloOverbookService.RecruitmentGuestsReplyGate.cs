using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private sealed record RecruitmentGuestSponsorResolution(
        string ZaloUserId,
        string DisplayName,
        bool Delegated,
        string? Error = null)
    {
        public bool IsResolved => Error is null && ZaloUserId.Length > 0;
    }

    private sealed record ReplyGatedGuestResolution(
        MatchSession? Session,
        string? RecruitmentMessageId,
        ZaloRecruitmentGuestReplyAnchorKind AnchorKind);

    public async Task<int> ProcessReplyGatedRecruitmentGuestTurnsDueAsync(
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

            var resolution = await ResolveReplyGatedRecruitmentGuestSessionAsync(
                message.ZaloConnectionId,
                message.GroupId,
                message.MessageId,
                command.Kind,
                cancellationToken);
            if (resolution.Session is null)
            {
                // Deliberately silent. Command-looking text in ordinary group chat must
                // never wake the guest flow unless it is a reply to one of our grounded
                // recruitment/guest messages.
                continue;
            }

            var connection = await db.ZaloConnections
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == message.ZaloConnectionId, cancellationToken);
            if (connection is null) continue;

            var session = resolution.Session;
            try
            {
                string reply;
                string outcome;
                string? outboundSelectedIntent = null;
                string? outboundReplyOutcome = null;
                var service = new ZaloGuestReservationService(db);

                if (command.Kind == ZaloRecruitmentGuestCommandKind.Add)
                {
                    var sponsor = await ResolveRecruitmentGuestSponsorAsync(
                        message.ZaloConnectionId,
                        message.GroupId,
                        message.SenderId,
                        message.SenderName,
                        command.SponsorReference,
                        cancellationToken);
                    if (!sponsor.IsResolved)
                    {
                        await SendReplyGatedRecruitmentGuestReplyAsync(
                            connection,
                            message,
                            session.Id,
                            sponsor.Error ?? "Chưa xác định được người bảo lãnh guest nên tui chưa cộng slot.",
                            "guest_sponsor_unresolved",
                            cancellationToken);
                        handled += 1;
                        continue;
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (!ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(session.StartTime, now, configuration))
                    {
                        var opensAt = ZaloRecruitmentGuestGatePolicy
                            .GetAddWindowOpensAt(session.StartTime, configuration)
                            ?.ToOffset(TimeSpan.FromHours(7));
                        var when = opensAt is null
                            ? "gần sát giờ chơi"
                            : $"khoảng {opensAt.Value:HH:mm}";
                        var hours = (int)ZaloRecruitmentGuestGatePolicy.GetSignupWindow(configuration).TotalHours;
                        await SendReplyGatedRecruitmentGuestReplyAsync(
                            connection,
                            message,
                            session.Id,
                            $"Kèo {session.Name} chưa mở + bạn ngoài group nha. Tui ưu tiên anh em trong group vote trước; {when} ({hours} giờ trước trận) mới nhận `+1`/`+2` bằng cách reply tin tuyển người của tui.",
                            "guest_signup_too_early",
                            cancellationToken);
                        handled += 1;
                        continue;
                    }

                    var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
                    if (!sync.Success)
                    {
                        await SendReplyGatedRecruitmentGuestReplyAsync(
                            connection,
                            message,
                            session.Id,
                            $"Tui hiểu ông muốn +{command.Quantity} cho {session.Name}, nhưng chưa sync được đúng poll nên chưa giữ slot để khỏi cộng nhầm nha. Thử lại chút nữa giúp tui.",
                            "guest_poll_sync_failed",
                            cancellationToken);
                        handled += 1;
                        continue;
                    }

                    await new ZaloGuestIdentityReconciler(db)
                        .ReconcileAsync(session.Id, cancellationToken);

                    var result = await service.AddAsync(
                        session,
                        sponsor.ZaloUserId,
                        sponsor.DisplayName,
                        message.MessageId,
                        resolution.RecruitmentMessageId,
                        command,
                        cancellationToken);
                    reply = BuildGuestAddReply(result, includeProfileNote: false);
                    outcome = result.Idempotent ? "guest_signup_idempotent" : "guest_signup_added";

                    var profileGuests = result.Added.Concat(result.Waitlisted)
                        .Where(GuestNeedsProfile)
                        .ToList();
                    if (profileGuests.Count > 0)
                    {
                        var guestRefs = string.Join(", ", profileGuests.Select(item => $"#{item.SponsorSequence}"));
                        outboundSelectedIntent = ZaloRecruitmentGuestGatePolicy
                            .GuestProfileContextSelectedIntent(session.Id, message.MessageId);
                        if (sponsor.Delegated)
                        {
                            reply += $" Còn thiếu hồ sơ cho {guestRefs}. Người vừa + guest chọn `1` nếu admin sẽ tự cập nhật giúp; chọn `2` để tui tag @{sponsor.DisplayName} vào cập nhật cho bạn của họ.";
                            outboundReplyOutcome = ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome;
                        }
                        else
                        {
                            reply += $" Còn thiếu hồ sơ cho {guestRefs}. Reply ngay tin này bằng thông tin kiểu `#1 tên Minh, nam, chủ công, khá`; có thể bổ sung từng phần.";
                            outboundReplyOutcome = ZaloRecruitmentGuestGatePolicy.GuestProfileSponsorPromptReplyOutcome;
                        }
                    }
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

                await SendReplyGatedRecruitmentGuestReplyAsync(
                    connection,
                    message,
                    session.Id,
                    reply,
                    outcome,
                    cancellationToken,
                    outboundSelectedIntent,
                    outboundReplyOutcome);
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
                    "Reply-gated recruitment guest turn failed Session={SessionId} Message={MessageId}",
                    session.Id,
                    message.MessageId);
                await SendReplyGatedRecruitmentGuestReplyAsync(
                    connection,
                    message,
                    session.Id,
                    $"Tui hiểu ý guest cho {session.Name} nhưng thao tác chưa chạy an toàn được: {Truncate(exception.Message, 180)}",
                    "guest_mutation_failed",
                    cancellationToken);
                handled += 1;
            }
        }

        return handled;
    }

    private async Task<RecruitmentGuestSponsorResolution> ResolveRecruitmentGuestSponsorAsync(
        string connectionId,
        string groupId,
        string senderId,
        string senderName,
        string? sponsorReference,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = ZaloOverbookLogic.NormalizeId(senderId);
        var fallback = new RecruitmentGuestSponsorResolution(
            normalizedSenderId,
            FriendlySponsorName(senderName, senderId),
            Delegated: false);
        if (string.IsNullOrWhiteSpace(sponsorReference))
            return fallback;

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member =>
                member.ZaloConnectionId == connectionId &&
                member.GroupId == groupId &&
                member.IsCurrentMember)
            .ToListAsync(cancellationToken);
        var matches = ZaloRecruitmentGuestSponsorPolicy.FindCurrentMembers(
            sponsorReference,
            members);
        if (matches.Count == 1)
        {
            return new RecruitmentGuestSponsorResolution(
                ZaloOverbookLogic.NormalizeId(matches[0].ZaloUserId),
                matches[0].DisplayName,
                Delegated: true);
        }

        if (matches.Count > 1)
        {
            return new RecruitmentGuestSponsorResolution(
                string.Empty,
                sponsorReference,
                Delegated: true,
                Error: $"Có nhiều thành viên tên {sponsorReference}; tui chưa dám gán guest cho nhầm người. Mention rõ đúng người rồi thử lại nha.");
        }

        return new RecruitmentGuestSponsorResolution(
            string.Empty,
            sponsorReference,
            Delegated: true,
            Error: $"Tui hiểu đây là guest của {sponsorReference}, nhưng không xác nhận được người này đang là thành viên hiện tại của group nên chưa cộng slot.");
    }

    private async Task<ReplyGatedGuestResolution> ResolveReplyGatedRecruitmentGuestSessionAsync(
        string connectionId,
        string groupId,
        string incomingMessageId,
        ZaloRecruitmentGuestCommandKind commandKind,
        CancellationToken cancellationToken)
    {
        var relation = await new ZaloMessageGraphStore(db)
            .LoadRelationAsync(connectionId, groupId, incomingMessageId, cancellationToken);
        if (relation?.RelationType != "ReplyTo" || string.IsNullOrWhiteSpace(relation.ToMessageId))
            return new ReplyGatedGuestResolution(null, null, ZaloRecruitmentGuestReplyAnchorKind.None);

        var anchor = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.MessageId == relation.ToMessageId &&
                item.IsFromBot,
                cancellationToken);
        if (anchor is null)
            return new ReplyGatedGuestResolution(null, null, ZaloRecruitmentGuestReplyAnchorKind.None);

        string? sessionId = null;
        string? recruitmentMessageId = null;
        var anchorKind = ZaloRecruitmentGuestReplyAnchorKind.None;

        if (anchor.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome)
        {
            sessionId = ZaloKeepRecruitingBroadcastPolicy.TryReadSessionId(anchor.SelectedIntent);
            recruitmentMessageId = anchor.MessageId;
            anchorKind = ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast;
        }
        else if (anchor.ReplyOutcome == ZaloRecruitmentGuestGatePolicy.GuestSignupGuidanceReplyOutcome)
        {
            sessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(anchor.SelectedIntent);
            anchorKind = ZaloRecruitmentGuestReplyAnchorKind.GuestSignupGuidance;
        }
        else if (commandKind == ZaloRecruitmentGuestCommandKind.Add &&
                 anchor.ReplyOutcome == "sent" &&
                 ZaloRecruitmentGuestGatePolicy.LooksLikeSignupGuidance(anchor.Content))
        {
            // Compatibility for guidance sent before GuestSignupGuidance carried an
            // explicit session id. Follow the bot-reply edge back to the original user
            // request (which may contain "hôm nay"/weekday/date), then resolve exactly
            // one session using the same guest-session resolver. Ambiguous recovery
            // stays fail-closed.
            var anchorRelation = await new ZaloMessageGraphStore(db)
                .LoadRelationAsync(connectionId, groupId, anchor.MessageId, cancellationToken);
            if (anchorRelation?.RelationType == "BotReply" &&
                !string.IsNullOrWhiteSpace(anchorRelation.ToMessageId))
            {
                var source = await db.ZaloGroupMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.ZaloConnectionId == connectionId &&
                        item.GroupId == groupId &&
                        item.MessageId == anchorRelation.ToMessageId &&
                        !item.IsFromBot,
                        cancellationToken);
                var sourceCommand = ZaloRecruitmentGuestPolicy.TryParse(source?.Content);
                if (source is not null && sourceCommand?.Kind == ZaloRecruitmentGuestCommandKind.Add)
                {
                    var recovered = await ResolveRecruitmentGuestSessionAsync(
                        connectionId,
                        groupId,
                        source.MessageId,
                        source.Content,
                        source.SenderId,
                        sourceCommand,
                        cancellationToken);
                    if (recovered.Session is not null)
                    {
                        sessionId = recovered.Session.Id;
                        recruitmentMessageId = recovered.RecruitmentMessageId;
                        anchorKind = ZaloRecruitmentGuestReplyAnchorKind.GuestSignupGuidance;
                    }
                }
            }
        }
        else if (anchor.ReplyOutcome == ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome)
        {
            sessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(anchor.SelectedIntent);
            anchorKind = ZaloRecruitmentGuestReplyAnchorKind.GuestConversation;
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            !ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(commandKind, anchorKind))
            return new ReplyGatedGuestResolution(null, null, anchorKind);

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
                item.StartTime != null &&
                item.StartTime > now,
                cancellationToken);

        return new ReplyGatedGuestResolution(session, recruitmentMessageId, anchorKind);
    }

    private async Task SendReplyGatedRecruitmentGuestReplyAsync(
        ZaloConnection connection,
        ZaloGroupMessage incoming,
        string sessionId,
        string body,
        string outcome,
        CancellationToken cancellationToken,
        string? outboundSelectedIntent = null,
        string? outboundReplyOutcome = null)
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

        var outbound = await db.ZaloGroupMessages
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connection.Id &&
                item.GroupId == incoming.GroupId &&
                item.MessageId == persistedReplyId,
                cancellationToken);
        if (outbound is not null)
        {
            outbound.SelectedIntent = outboundSelectedIntent ??
                                      ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(sessionId);
            outbound.ReplyOutcome = outboundReplyOutcome ??
                                    ZaloRecruitmentGuestGatePolicy.GuestConversationReplyOutcome;
        }

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
        incoming.SelectedIntent = ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(sessionId);
        incoming.ReplyOutcome = outcome;
        incoming.AiCalled = false;
        incoming.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
