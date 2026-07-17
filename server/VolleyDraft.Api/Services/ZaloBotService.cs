using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBotService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    AiAssistantService ai,
    ZaloIntegrationService zaloIntegration,
    SessionDraftService draftService,
    ZaloTeamCardService teamCards,
    SessionWaitlistService waitlists,
    ZaloBotActionHistoryService actionHistory,
    ZaloListenerCoordinator listenerCoordinator,
    ZaloSchedulerTrigger schedulerTrigger,
    IConfiguration configuration,
    ILogger<ZaloBotService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<ServiceResult<ZaloBotSettingsResponse>> GetSettingsAsync(string adminUserId, string sessionId)
    {
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        return session is null
            ? ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.")
            : ServiceResult<ZaloBotSettingsResponse>.Success(ToSettings(session));
    }

    public async Task<ServiceResult<ZaloBotSettingsResponse>> UpdateSettingsAsync(
        string adminUserId,
        string sessionId,
        UpdateZaloBotSettingsRequest request)
    {
        var session = await db.MatchSessions.SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
        {
            return ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        }
        if (request.BotEnabled && (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId)))
        {
            return ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status400BadRequest, "Hãy liên kết nhóm Zalo trước khi bật bot.");
        }
        if (request.ReminderEnabled && (!request.BotEnabled || request.StartTime is null))
        {
            return ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status400BadRequest, "Reminder cần bật bot và có thời gian bắt đầu trận.");
        }
        if (!IsOptionalHttpUrl(request.LocationImageUrl) || !IsOptionalHttpUrl(request.PaymentQrImageUrl))
        {
            return ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status400BadRequest, "URL ảnh vị trí và QR thanh toán phải dùng http hoặc https.");
        }

        var previousStartTime = session.StartTime;
        var previousReminderEnabled = session.ReminderEnabled;
        var previousLeadHours = session.ReminderLeadHours;
        var previousIntervalHours = session.ReminderIntervalHours;
        session.StartTime = request.StartTime;
        session.Location = Clean(request.Location, 500);
        session.ParkingInstructions = Clean(request.ParkingInstructions, 1000);
        session.LocationImageUrl = Clean(request.LocationImageUrl, 2048);
        session.PaymentInstructions = Clean(request.PaymentInstructions, 1000);
        session.PaymentQrImageUrl = Clean(request.PaymentQrImageUrl, 2048);
        session.BotEnabled = request.BotEnabled;
        session.BotCustomInstructions = Clean(request.BotCustomInstructions, 2000);
        if (request.BotOperatorZaloUserIds is not null)
        {
            session.BotOperatorZaloUserIdsJson = JsonSerializer.Serialize(request.BotOperatorZaloUserIds
                .Select(NormalizeId)
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(20));
        }
        session.ReminderEnabled = request.ReminderEnabled;
        session.ReminderLeadHours = Math.Clamp(request.ReminderLeadHours, 1, 336);
        session.ReminderIntervalHours = Math.Clamp(request.ReminderIntervalHours, 1, 168);
        var reminderIntervalChanged = previousIntervalHours != session.ReminderIntervalHours;
        if (reminderIntervalChanged || session.ReminderIntervalMinutes <= 0)
            session.ReminderIntervalMinutes = session.ReminderIntervalHours * 60;
        if (!previousReminderEnabled && session.ReminderEnabled)
        {
            session.ReminderIntervalMinutes = session.ReminderIntervalHours * 60;
            session.ReminderRepeats = true;
        }
        var reminderScheduleChanged = !previousReminderEnabled ||
                                      previousStartTime != session.StartTime ||
                                      previousLeadHours != session.ReminderLeadHours ||
                                      previousIntervalHours != session.ReminderIntervalHours;
        if (session.ReminderEnabled && session.StartTime is not null)
        {
            if (reminderScheduleChanged || session.NextReminderAt is null)
            {
                var now = DateTimeOffset.UtcNow;
                var configuredStart = session.StartTime.Value.AddHours(-session.ReminderLeadHours);
                session.NextReminderAt = configuredStart > now ? configuredStart : now;
            }
            session.ReminderFailureCount = 0;
            session.LastReminderError = null;
        }
        else
        {
            session.NextReminderAt = null;
            session.ReminderLeaseToken = null;
            session.ReminderLeaseUntil = null;
            session.ReminderFailureCount = 0;
            session.LastReminderError = null;
        }
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(session.ZaloConnectionId))
        {
            var listenerReady = await listenerCoordinator.EnsureConnectionAsync(session.ZaloConnectionId);
            if (session.BotEnabled && !listenerReady)
            {
                return ServiceResult<ZaloBotSettingsResponse>.Failure(
                    StatusCodes.Status502BadGateway,
                    "Đã lưu cấu hình nhưng listener Zalo chưa đăng nhập được. Hãy đăng nhập lại bằng QR rồi thử lưu lại.");
            }
        }
        if (session.ReminderEnabled) schedulerTrigger.TryTrigger();
        return ServiceResult<ZaloBotSettingsResponse>.Success(ToSettings(session));
    }

    public async Task<ServiceResult<IReadOnlyList<ZaloBotOperatorCandidateResponse>>> GetOperatorCandidatesAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
            return ServiceResult<IReadOnlyList<ZaloBotOperatorCandidateResponse>>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ServiceResult<IReadOnlyList<ZaloBotOperatorCandidateResponse>>.Success([]);

        var authorized = ParseOperatorIds(session.BotOperatorZaloUserIdsJson);
        var recentSenders = await db.ZaloGroupMessages.AsNoTracking()
            .Where(message => message.ZaloConnectionId == session.ZaloConnectionId &&
                              message.GroupId == session.ZaloGroupId &&
                              !message.IsFromBot)
            .OrderByDescending(message => message.SentAt)
            .Take(1000)
            .Select(message => new { message.SenderId, message.SenderName, message.SentAt })
            .ToListAsync();
        var candidates = recentSenders
            .GroupBy(item => NormalizeId(item.SenderId), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(100)
            .ToList();
        return ServiceResult<IReadOnlyList<ZaloBotOperatorCandidateResponse>>.Success(candidates
            .Select(item => new ZaloBotOperatorCandidateResponse(
                item.SenderId,
                item.SenderName,
                authorized.Contains(NormalizeId(item.SenderId)),
                item.SentAt))
            .ToList());
    }

    public async Task<ServiceResult<PagedResponse<ZaloBotLearnedRuleResponse>>> GetLearnedRulesAsync(
        string adminUserId,
        string sessionId,
        int page,
        int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
            return ServiceResult<PagedResponse<ZaloBotLearnedRuleResponse>>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ServiceResult<PagedResponse<ZaloBotLearnedRuleResponse>>.Success(new([], page, pageSize, 0, 0));
        var query = db.ZaloBotLearnedRules.AsNoTracking()
            .Where(rule => rule.ZaloConnectionId == session.ZaloConnectionId && rule.GroupId == session.ZaloGroupId);
        var totalItems = await query.CountAsync();
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling((double)totalItems / pageSize);
        if (totalPages > 0) page = Math.Min(page, totalPages);
        var rules = await query
            .OrderBy(rule => rule.Status == ZaloBotRuleStatus.Pending ? 0 : 1)
            .ThenByDescending(rule => rule.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(rule => ToLearnedRuleResponse(rule))
            .ToListAsync();
        return ServiceResult<PagedResponse<ZaloBotLearnedRuleResponse>>.Success(
            new(rules, page, pageSize, totalItems, totalPages));
    }

    public async Task<ServiceResult<ZaloBotLearnedRuleResponse>> ReviewLearnedRuleAsync(
        string adminUserId,
        string sessionId,
        string ruleId,
        ReviewZaloBotLearnedRuleRequest request)
    {
        if (request.Status is not (ZaloBotRuleStatus.Approved or ZaloBotRuleStatus.Rejected or ZaloBotRuleStatus.Disabled))
            return ServiceResult<ZaloBotLearnedRuleResponse>.Failure(StatusCodes.Status400BadRequest, "Trạng thái duyệt không hợp lệ.");
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
            return ServiceResult<ZaloBotLearnedRuleResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        var rule = await db.ZaloBotLearnedRules.SingleOrDefaultAsync(item =>
            item.Id == ruleId && item.ZaloConnectionId == session.ZaloConnectionId && item.GroupId == session.ZaloGroupId);
        if (rule is null)
            return ServiceResult<ZaloBotLearnedRuleResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy đề xuất ghi nhớ.");
        rule.Status = request.Status;
        rule.Priority = Math.Clamp(request.Priority, -100, 100);
        rule.ReviewNote = Clean(request.ReviewNote, 500);
        rule.ApprovedByUserId = adminUserId;
        rule.ApprovedAt = DateTimeOffset.UtcNow;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return ServiceResult<ZaloBotLearnedRuleResponse>.Success(ToLearnedRuleResponse(rule));
    }

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
        if (!explicitlyMentioned || storedMessage.BotReplySentAt is not null) return;

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
        var bypassCooldown = ZaloBotIntelligence.IsConfirmation(incomingQuestion) || ZaloBotIntelligence.IsCancel(incomingQuestion);
        var tooSoon = !bypassCooldown && await db.ZaloGroupMessages.AsNoTracking().AnyAsync(message =>
                message.Id != storedMessage.Id && message.ZaloConnectionId == connection.Id &&
                message.GroupId == groupId && message.SenderId == NormalizeId(incoming.SenderId) &&
                message.ProcessingStartedAt >= DateTimeOffset.UtcNow.AddSeconds(-exactCooldownSeconds), cancellationToken);
        if (tooSoon)
        {
            await FinishMessageAsync(storedMessage.Id, processingToken, null, false, "throttled", cancellationToken);
            return;
        }

        BotAnswer response;
        try
        {
            response = await BuildAnswerAsync(connectionIds, connection.Id, groupId, incoming, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Zalo bot routing failed Account={AccountId} Group={GroupId} Message={MessageId}", accountId, groupId, messageId);
            await ReleaseMessageAsync(storedMessage.Id, processingToken, "routing_failed", cancellationToken);
            throw;
        }
        if (!response.TextGeneratedByAi &&
            response.Intent != ZaloBotIntent.GeneralChat &&
            (ZaloBotIntelligence.CanUseAiStyleRewrite(response.Intent) || response.ProtectedTerms is { Count: > 0 }) &&
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
        var reply = $"{mentionLabel} {response.Text.Trim()}";
        var outgoingMentions = new List<BridgeOutgoingMention>
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
                $"{accountId}:{messageId}");
        }
        catch
        {
            await ReleaseMessageAsync(storedMessage.Id, processingToken, "send_failed", cancellationToken);
            throw;
        }

        await FinishMessageAsync(storedMessage.Id, processingToken, response.Intent, response.AiCalled, "sent", cancellationToken);

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

    private async Task FinishMessageAsync(string id, string token, ZaloBotIntent? intent, bool aiCalled, string outcome, CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages.Where(message => message.Id == id && message.ProcessingToken == token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.BotReplySentAt, outcome == "sent" ? DateTimeOffset.UtcNow : null)
                .SetProperty(message => message.SelectedIntent, intent == null ? null : intent.Value.ToString())
                .SetProperty(message => message.AiCalled, aiCalled)
                .SetProperty(message => message.ReplyOutcome, outcome), cancellationToken);
    }

    private async Task ReleaseMessageAsync(string id, string token, string outcome, CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages.Where(message => message.Id == id && message.ProcessingToken == token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(message => message.ProcessingToken, (string?)null)
                .SetProperty(message => message.ReplyOutcome, outcome), cancellationToken);
    }

    private async Task<BotAnswer> BuildAnswerAsync(
        IReadOnlyList<string> connectionIds,
        string activeConnectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var question = ExtractQuestion(incoming);
        var normalizedQuestion = NormalizeText(question);

        var sessions = await LoadSessionSnapshotsAsync(connectionIds, groupId, incoming.SenderId, cancellationToken);
        if (sessions.Count == 0)
        {
            return new BotAnswer("Nhóm này chưa có trận nào đang bật bot. Bạn nhờ admin kiểm tra cấu hình nhé.", null);
        }

        // Resolve an outstanding confirmation before parsing the new text as a
        // fresh command. This is what makes "xác nhận" execute the exact
        // previewed reminder instead of re-parsing the word "xác nhận".
        var pending = await ResolvePendingConversationAsync(activeConnectionId, groupId, incoming.SenderId, normalizedQuestion, sessions, cancellationToken);
        if (pending.Cancelled)
        {
            return new BotAnswer("Đã huỷ yêu cầu đang chờ. Chưa có thay đổi nào được thực hiện.", null, ZaloBotIntent.GeneralChat);
        }
        if (pending.ReminderCommand is not null && pending.TargetSessions is { Count: > 0 })
        {
            return await HandleReminderCommandAsync(
                pending.ReminderCommand,
                sessions,
                normalizedQuestion,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                true,
                true,
                pending.TargetSessions);
        }
        if (pending.RepairCommand is not null && pending.Session is not null)
        {
            var repairConfirmed = pending.Intent == ZaloBotIntent.RepairShareSlotConfirm;
            return await HandleRepairShareSlotAsync(
                new ZaloIntentDecision(
                    repairConfirmed ? ZaloBotIntent.RepairShareSlotConfirm : ZaloBotIntent.RepairShareSlot,
                    1,
                    pending.Session.Name,
                    false,
                    null,
                    repairConfirmed ? "repair_share_slot_confirmation" : "repair_share_slot_session_selected"),
                [pending.Session],
                pending.Session.Name,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false,
                repairConfirmed,
                pending.RepairCommand);
        }
        if (pending.TransferCommand is not null && pending.Session is not null)
        {
            var transferConfirmed = pending.Intent == ZaloBotIntent.SlotTransferConfirm;
            return await HandleSlotTransferAsync(
                new ZaloIntentDecision(
                    transferConfirmed ? ZaloBotIntent.SlotTransferConfirm : ZaloBotIntent.SlotTransfer,
                    1,
                    pending.Session.Name,
                    false,
                    null,
                    transferConfirmed ? "slot_transfer_confirmation" : "slot_transfer_session_selected"),
                [pending.Session],
                pending.Session.Name,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false,
                transferConfirmed,
                pending.TransferCommand);
        }
        if (pending.GuestCommand is not null && pending.Session is not null)
        {
            return await AddGuestPlayerAsync(
                new ZaloIntentDecision(
                    ZaloBotIntent.AddGuestPlayer,
                    1,
                    pending.Session.Name,
                    false,
                    null,
                    "add_guest_session_selected"),
                [pending.Session],
                NormalizeText(pending.Session.Name),
                question,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false,
                pending.GuestCommand);
        }
        if (pending.RebalancePlan is not null && pending.Session is not null)
        {
            var denial = await GetOperatorDenialAsync(
                pending.Session,
                incoming.SenderId,
                ZaloBotIntent.RebalanceTeamsConfirm,
                false);
            if (denial is not null) return denial;
            var before = await actionHistory.CaptureAsync(pending.Session.Id, cancellationToken);
            var rebalanced = await draftService.ApplyTeamRebalanceAsync(
                pending.Session.AdminUserId,
                pending.RebalancePlan,
                cancellationToken);
            if (!rebalanced.IsSuccess || rebalanced.Value is null)
                return new BotAnswer(
                    rebalanced.Error ?? "Không áp dụng được phương án cân bằng.",
                    null,
                    ZaloBotIntent.RebalanceTeamsConfirm);

            var plan = rebalanced.Value.Plan;
            await actionHistory.RecordAsync(
                pending.Session.Id,
                incoming.SenderId,
                incoming.SenderName,
                "RebalanceTeams",
                $"Cân bằng {plan.FirstTeamName} và {plan.SecondTeamName} trong {pending.Session.Name}",
                before,
                cancellationToken);
            var facts = FormatTeamRebalanceFacts(plan);
            var lineup = FormatTeamLineup(pending.Session.Name, rebalanced.Value.State.TeamPreview);
            return new BotAnswer(
                $"Đã cân bằng lại {plan.FirstTeamName} và {plan.SecondTeamName}.\n{facts}\n{lineup}",
                teamCards.GetPublicUrl(pending.Session.Id),
                ZaloBotIntent.RebalanceTeamsConfirm,
                ProtectedTerms: [facts, lineup]);
        }
        if (!string.IsNullOrWhiteSpace(pending.ActionHistoryId) && pending.Session is not null)
        {
            var denial = await GetOperatorDenialAsync(pending.Session, incoming.SenderId, ZaloBotIntent.UndoActionConfirm, false);
            if (denial is not null) return denial;
            var undone = await actionHistory.UndoAsync(
                pending.Session.AdminUserId,
                pending.Session.Id,
                pending.ActionHistoryId,
                incoming.SenderId,
                cancellationToken);
            return undone.IsSuccess && undone.Value is not null
                ? new BotAnswer($"Đã hoàn tác dữ liệu backend của thao tác: {undone.Value.Summary}. Tin nhắn Zalo cũ vẫn được giữ nguyên.", null, ZaloBotIntent.UndoActionConfirm)
                : new BotAnswer(undone.Error ?? "Không thể hoàn tác thao tác này.", null, ZaloBotIntent.UndoActionConfirm);
        }
        if (pending.Clarification is not null)
        {
            return new BotAnswer(pending.Clarification, null, ZaloBotIntent.Unknown);
        }

        var earlyDecision = ZaloBotIntelligence.ClassifyDeterministically(question);
        if (earlyDecision.Intent == ZaloBotIntent.SlotTransfer)
        {
            return await HandleSlotTransferAsync(
                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);
        }
        if (earlyDecision.Intent is ZaloBotIntent.WaitlistJoin or ZaloBotIntent.WaitlistLeave or
            ZaloBotIntent.WaitlistStatus or ZaloBotIntent.WaitlistAccept or ZaloBotIntent.WaitlistDecline)
        {
            return await HandleWaitlistIntentAsync(
                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);
        }
        if (earlyDecision.Intent == ZaloBotIntent.RepairShareSlot)
        {
            return await HandleRepairShareSlotAsync(
                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);
        }
        if (earlyDecision.Intent == ZaloBotIntent.TeamPreference)
        {
            return await HandleTeamPreferenceAsync(
                earlyDecision,
                sessions,
                normalizedQuestion,
                question,
                incoming,
                cancellationToken,
                false);
        }

        if (ZaloBotIntelligence.TryParseReminderCommand(question, out var reminderCommand))
        {
            return await HandleReminderCommandAsync(
                reminderCommand,
                sessions,
                normalizedQuestion,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false);
        }

        var learnedRules = await LoadLearnedRulesAsync(connectionIds, groupId, cancellationToken);

        if (TryParseNaturalLearning(question, out var naturalTrigger, out var naturalAnswer))
        {
            return await SaveLearnedRuleAsync(
                activeConnectionId,
                groupId,
                incoming,
                naturalTrigger,
                naturalAnswer,
                cancellationToken);
        }

        ZaloIntentDecision decision;
        if (pending.Intent is not null && pending.Session is not null)
        {
            decision = new ZaloIntentDecision(pending.Intent.Value, 1, pending.Session.Name, false, null, "conversation_follow_up");
            normalizedQuestion = NormalizeText(pending.Session.Name);
        }
        else
        {
            decision = ZaloBotIntelligence.ClassifyDeterministically(question);
            if (decision.Intent == ZaloBotIntent.Unknown)
            {
                var commandWithSelector = Regex.Match(normalizedQuestion, @"^(?<command>10|[1-9])\s+(?<selector>.+)$", RegexOptions.CultureInvariant);
                if (commandWithSelector.Success && HasExplicitSessionSelector(sessions, commandWithSelector.Groups["selector"].Value))
                {
                    decision = new ZaloIntentDecision(
                        ZaloBotIntelligence.IntentForCommand(int.Parse(commandWithSelector.Groups["command"].Value, CultureInfo.InvariantCulture)),
                        1,
                        commandWithSelector.Groups["selector"].Value,
                        false,
                        null,
                        "numeric_command_with_valid_session_selector");
                }
            }
        }

        var learnedRuleMatch = learnedRules
            .Select(rule => new
            {
                Rule = rule,
                RuleIntent = ZaloBotIntelligence.ClassifyDeterministically(rule.Trigger).Intent,
                Score = ZaloBotIntelligence.TokenSimilarity(rule.NormalizedTrigger, question)
            })
            .Where(match => match.Score >= configuration.GetValue("ZaloBot:LearnedRuleSimilarityThreshold", .82))
            .Where(match => match.RuleIntent == decision.Intent ||
                            (decision.Intent == ZaloBotIntent.Unknown && match.RuleIntent is ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat))
            .OrderByDescending(match => match.Rule.Priority)
            .ThenByDescending(match => match.Score)
            .FirstOrDefault();

        ZaloBotLearnedRule? conversationalLearnedRule = null;
        if (learnedRuleMatch is not null)
        {
            if (learnedRuleMatch.RuleIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat) &&
                ZaloBotIntelligence.PrefersNearestSession(learnedRuleMatch.Rule.Answer) &&
                !HasExplicitSessionSelector(sessions, normalizedQuestion))
            {
                var nearest = sessions.FirstOrDefault(IsUpcoming) ?? sessions.First();
                normalizedQuestion = NormalizeText(nearest.Name);
                decision = decision with
                {
                    Intent = learnedRuleMatch.RuleIntent,
                    SessionReference = nearest.Name,
                    Reason = "approved_rule_prefer_nearest_session"
                };
                logger.LogInformation(
                    "Applied approved Zalo behavior rule Rule={RuleId} Intent={Intent} Session={SessionId}",
                    learnedRuleMatch.Rule.Id,
                    decision.Intent,
                    nearest.Id);
            }
            else if (learnedRuleMatch.RuleIntent is ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat)
            {
                conversationalLearnedRule = learnedRuleMatch.Rule;
            }
        }

        if (decision.Intent == ZaloBotIntent.Help)
        {
            return new BotAnswer(
                " \n🤖 Menu bot:\n1. Xem giờ và địa điểm trận\n2. Kiểm tra mình có trong danh sách\n3. Xem vị trí và hướng dẫn gửi xe\n4. Xem còn thiếu bao nhiêu slot\n5. Xem các trận sắp tới\n6. Xem QR và hướng dẫn thanh toán\n7. Xem danh sách 3 team\n8. Đồng bộ người đã vote lên web (có quyền)\n9. Tự chạy draft/khui túi (có quyền + xác nhận)\n10. Gửi ảnh card 3 team\n\nBạn cũng có thể nói: “cân bằng team 2 và team 3”, “cho tui vào danh sách chờ T6”, “nhận slot”, “xem waitlist”, “xem lịch sử thao tác” hoặc “undo thao tác vừa rồi”. Các lệnh thay đổi đội hình sẽ hỏi xác nhận; undo chỉ khôi phục dữ liệu backend, không thu hồi tin Zalo.\n\nNgười có quyền gồm trưởng nhóm, phó nhóm và UID được admin cấp. Nếu có nhiều trận, hãy thêm ngày hoặc tên trận.",
                null,
                ZaloBotIntent.Help);
        }

        if (decision.Intent == ZaloBotIntent.SlotTransfer)
            return await HandleSlotTransferAsync(decision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);

        if (decision.Intent is ZaloBotIntent.WaitlistJoin or ZaloBotIntent.WaitlistLeave or
            ZaloBotIntent.WaitlistStatus or ZaloBotIntent.WaitlistAccept or ZaloBotIntent.WaitlistDecline)
            return await HandleWaitlistIntentAsync(decision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);

        if (decision.Intent is ZaloBotIntent.ActionHistory or ZaloBotIntent.UndoAction)
            return await HandleActionHistoryIntentAsync(decision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);

        if (decision.Intent == ZaloBotIntent.UpcomingSessions)
        {
            var upcoming = sessions.Where(IsUpcoming).Take(5).ToList();
            var lines = upcoming.Select(session =>
            {
                var schedule = session.StartTime is null ? "chưa chốt giờ" : FormatVietnamTime(session.StartTime.Value);
                var location = string.IsNullOrWhiteSpace(session.Location) ? "chưa chốt sân" : session.Location;
                return $"- {session.Name}: {schedule}, {location}, {session.PlayerCount}/{session.Capacity} slot";
            });
            return new BotAnswer(upcoming.Count == 0
                ? "Hiện chưa có trận sắp tới nào được cấu hình."
                : "Các trận sắp tới:\n" + string.Join("\n", lines), null, ZaloBotIntent.UpcomingSessions);
        }

        if (decision.Intent is ZaloBotIntent.TeamLineup or ZaloBotIntent.TeamImage)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null, decision.Intent);
            var session = selected.Session!;
            var state = await draftService.GetDraftStateAsync(session.AdminUserId, session.Id);
            if (!state.IsSuccess || state.Value is null)
                return new BotAnswer(state.Error ?? "Mình chưa đọc được đội hình của buổi này.", null, decision.Intent);
            var lineup = await BuildTeamLineupMessageAsync(
                session.Name,
                state.Value.TeamPreview,
                ZaloTeamLineupFormatter.WantsPlayerMentions(question),
                cancellationToken);
            var imageUrl = decision.Intent == ZaloBotIntent.TeamImage ? teamCards.GetPublicUrl(session.Id) : null;
            return new BotAnswer(lineup.Text, imageUrl, decision.Intent, Mentions: lineup.Mentions);
        }

        if (decision.Intent == ZaloBotIntent.UpdatePlayerProfile)
            return await UpdatePlayerProfileAsync(decision, sessions, normalizedQuestion, question, incoming, false);

        if (decision.Intent == ZaloBotIntent.AddGuestPlayer)
            return await AddGuestPlayerAsync(
                decision,
                sessions,
                normalizedQuestion,
                question,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false);

        if (decision.Intent == ZaloBotIntent.TeamPreference)
            return await HandleTeamPreferenceAsync(
                decision,
                sessions,
                normalizedQuestion,
                question,
                incoming,
                cancellationToken,
                false);

        if (decision.Intent == ZaloBotIntent.ShareSlot)
            return await ShareSlotAsync(decision, sessions, normalizedQuestion, question, incoming, cancellationToken, false);

        if (decision.Intent == ZaloBotIntent.RepairShareSlot)
            return await HandleRepairShareSlotAsync(decision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);

        if (decision.Intent == ZaloBotIntent.IncompleteProfiles)
            return await ListIncompleteProfilesAsync(
                decision,
                sessions,
                normalizedQuestion,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false);

        if (decision.Intent == ZaloBotIntent.SyncPoll)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null, decision.Intent);
            var session = selected.Session!;
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, false);
            if (denial is not null) return denial;
            var syncBefore = await actionHistory.CaptureAsync(session.Id, cancellationToken);
            var synced = await zaloIntegration.SyncLatestPollAsync(session.AdminUserId, session.Id, question);
            if (!synced.IsSuccess || synced.Value is null)
                return new BotAnswer(synced.Error ?? "Không đồng bộ được poll.", null, decision.Intent);
            await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
                "SyncPoll", $"Đồng bộ poll lên danh sách {session.Name}", syncBefore, cancellationToken);
            await waitlists.ProcessVacanciesAsync(session.Id, cancellationToken);
            return new BotAnswer(
                synced.Value.Message + await BuildIncompleteProfilePromptAsync(session, true),
                null,
                decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.AutoDraft)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null, decision.Intent);
            var session = selected.Session!;
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, false);
            if (denial is not null) return denial;
            var syncError = await SyncPollBeforeDraftAsync(session, question);
            if (syncError is not null) return new BotAnswer(syncError, null, decision.Intent);
            var incompletePrompt = await BuildIncompleteProfilePromptAsync(session, false);
            if (incompletePrompt.Length > 0) return new BotAnswer(incompletePrompt.TrimStart(), null, decision.Intent);
            var pendingIntent = session.Status == SessionStatus.Finished
                ? ZaloBotIntent.RedraftConfirm
                : ZaloBotIntent.AutoDraftConfirm;
            await SaveActionConfirmationAsync(activeConnectionId, groupId, incoming.SenderId, session.Id, pendingIntent, cancellationToken);
            return new BotAnswer(
                session.Status == SessionStatus.Finished
                    ? $"⚠️ {session.Name} đã draft xong. Draft lại sẽ xoá kết quả bốc team hiện tại và khui lại từ đầu. Gõ @bot xác nhận draft lại để chạy, hoặc @bot huỷ."
                    : $"⚠️ Tự draft sẽ chọn captain nếu cần và khui toàn bộ túi của {session.Name}; kết quả đội sẽ thay đổi ngay trên web. Gõ @bot xác nhận draft để chạy, hoặc @bot huỷ.",
                null,
                decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.Redraft)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null, decision.Intent);
            var session = selected.Session!;
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, false);
            if (denial is not null) return denial;
            var incompletePrompt = await BuildIncompleteProfilePromptAsync(session, false);
            if (incompletePrompt.Length > 0) return new BotAnswer(incompletePrompt.TrimStart(), null, decision.Intent);
            await SaveActionConfirmationAsync(activeConnectionId, groupId, incoming.SenderId, session.Id, ZaloBotIntent.RedraftConfirm, cancellationToken);
            return new BotAnswer(
                $"⚠️ Draft lại {session.Name} sẽ xoá kết quả bốc team hiện tại và khui lại từ đầu, vẫn giữ captain hiện tại. Gõ @bot xác nhận draft lại để chạy, hoặc @bot huỷ.",
                null,
                decision.Intent);
        }

        if (decision.Intent is ZaloBotIntent.AutoDraftConfirm or ZaloBotIntent.RedraftConfirm)
        {
            var selected = sessions.SingleOrDefault(session => NormalizeText(session.Name) == normalizedQuestion) ?? sessions.FirstOrDefault();
            if (selected is null) return new BotAnswer("Mình không còn tìm thấy buổi cần draft.", null, decision.Intent);
            var denial = await GetOperatorDenialAsync(selected, incoming.SenderId, decision.Intent, false);
            if (denial is not null) return denial;
            var isRedraft = decision.Intent == ZaloBotIntent.RedraftConfirm;
            var draftBefore = await actionHistory.CaptureAsync(selected.Id, cancellationToken);
            var drafted = await draftService.AutoRunDraftAsync(selected.AdminUserId, selected.Id, isRedraft);
            if (!drafted.IsSuccess || drafted.Value is null)
                return new BotAnswer($"Không thể tự draft: {drafted.Error}", null, decision.Intent);
            await actionHistory.RecordAsync(selected.Id, incoming.SenderId, incoming.SenderName,
                isRedraft ? "Redraft" : "AutoDraft",
                $"{(isRedraft ? "Draft lại" : "Tự draft")} đội hình {selected.Name}", draftBefore, cancellationToken);
            return new BotAnswer(
                $"Đã {(isRedraft ? "draft lại" : "tự draft")} xong {selected.Name}.\n{FormatTeamLineup(selected.Name, drafted.Value.TeamPreview)}",
                teamCards.GetPublicUrl(selected.Id),
                decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.RebalanceTeams)
        {
            return await RebalanceTeamsAsync(
                decision,
                sessions,
                normalizedQuestion,
                question,
                activeConnectionId,
                groupId,
                incoming,
                cancellationToken,
                false);
        }

        if (decision.Intent == ZaloBotIntent.SwapTeamPlayers)
        {
            return await SwapTeamPlayersAsync(
                decision,
                sessions,
                normalizedQuestion,
                question,
                incoming,
                false);
        }

        if (decision.Intent == ZaloBotIntent.PaymentQr)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (string.IsNullOrWhiteSpace(session.PaymentQrImageUrl))
            {
                return new BotAnswer($"admin chưa cấu hình ảnh QR thanh toán cho {session.Name}.", null);
            }
            var instructions = string.IsNullOrWhiteSpace(session.PaymentInstructions)
                ? $"QR thanh toán cho {session.Name}. Khi chuyển khoản nhớ ghi tên Zalo của bạn nhé."
                : $"{session.Name} — {session.PaymentInstructions}";
            return new BotAnswer(instructions, session.PaymentQrImageUrl, decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.Roster)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (session.PlayerNames.Count == 0)
            {
                return new BotAnswer($"{session.Name} hiện chưa có ai trong danh sách.", null);
            }
            var players = session.PlayerNames.Select((name, index) => $"{index + 1}. {name}");
            return new BotAnswer(
                $"Danh sách {session.Name} ({session.PlayerCount}/{session.Capacity}):\n{string.Join("\n", players)}",
                null,
                decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.SelfMembership)
        {
            if (HasExplicitSessionSelector(sessions, normalizedQuestion))
            {
                var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
                if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
                var session = selected.Session!;
                return new BotAnswer(session.SenderIsListed
                    ? $"bạn đang ở danh sách của {session.Name}{FormatScheduleSuffix(session)}."
                    : $"mình chưa thấy bạn trong danh sách của {session.Name}.", null);
            }
            var upcoming = sessions.Where(IsUpcoming).Take(4).ToList();
            if (upcoming.Count == 0) upcoming = sessions.Take(1).ToList();
            var listed = upcoming.Where(session => session.SenderIsListed).ToList();
            if (listed.Count == 0)
            {
                return new BotAnswer($"mình chưa thấy bạn trong danh sách của {JoinSessionNames(upcoming)}.", null);
            }
            if (upcoming.Count == 1)
            {
                var session = upcoming[0];
                return new BotAnswer(
                    $"bạn đang ở danh sách của {session.Name}{FormatScheduleSuffix(session)}.",
                    null);
            }
            var statuses = upcoming.Select(session =>
                $"{session.Name}: {(session.SenderIsListed ? "đã có tên" : "chưa có tên")}{FormatShortTime(session.StartTime)}");
            return new BotAnswer(string.Join("\n", statuses), null);
        }

        if (decision.Intent == ZaloBotIntent.LocationParking)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (string.IsNullOrWhiteSpace(session.Location) && string.IsNullOrWhiteSpace(session.ParkingInstructions))
            {
                return new BotAnswer($"admin chưa cấu hình vị trí và chỗ gửi xe cho mình.", null);
            }
            var parts = new List<string> { session.Name };
            if (!string.IsNullOrWhiteSpace(session.Location)) parts.Add($"địa điểm: {session.Location}");
            if (!string.IsNullOrWhiteSpace(session.ParkingInstructions)) parts.Add($"gửi xe: {session.ParkingInstructions}");
            return new BotAnswer(string.Join(" — ", parts) + ".", session.LocationImageUrl, decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.SessionSchedule)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            return session.StartTime is null
                ? new BotAnswer($"admin chưa chốt giờ cho {session.Name}.", null)
                : new BotAnswer($"{session.Name} diễn ra lúc {FormatVietnamTime(session.StartTime.Value)}{FormatLocationSuffix(session)}.", null);
        }

        if (decision.Intent == ZaloBotIntent.MissingSlots)
        {
            var selected = await SelectSessionAsync(sessions, normalizedQuestion, activeConnectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            var missing = Math.Max(0, session.Capacity - session.PlayerCount);
            return new BotAnswer(missing == 0
                ? $"{session.Name} đã đủ {session.Capacity} slot."
                : $"{session.Name} đang có {session.PlayerCount}/{session.Capacity}, còn thiếu {missing} slot.", null);
        }

        if (decision.Intent == ZaloBotIntent.WeeklySessionCount)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var nextMonday = monday.AddDays(7);
            var thisWeek = sessions.Where(session => session.StartTime is not null)
                .Where(session =>
                {
                    var local = session.StartTime!.Value.ToOffset(VietnamOffset);
                    return local.Date >= monday && local.Date < nextMonday;
                })
                .OrderBy(session => session.StartTime)
                .ToList();
            if (thisWeek.Count == 0) return new BotAnswer("Tuần này nhóm chưa có trận nào được cấu hình.", null, decision.Intent);
            var lines = thisWeek.Select(session => $"- {session.Name}: {FormatVietnamTime(session.StartTime!.Value)}");
            return new BotAnswer($"Tuần này nhóm có {thisWeek.Count} trận:\n{string.Join("\n", lines)}", null, decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.ModelInfo)
        {
            return new BotAnswer(ai.GetPublicModelInfo(), null, decision.Intent);
        }

        if (conversationalLearnedRule is not null)
        {
            return new BotAnswer(RenderLearnedAnswer(conversationalLearnedRule.Answer, incoming.SenderName), null);
        }

        var recentMessageRows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message => connectionIds.Contains(message.ZaloConnectionId) && message.GroupId == groupId)
            .OrderByDescending(message => message.SentAt)
            .Take(20)
            .OrderBy(message => message.SentAt)
            .Select(message => new ZaloAiMessage(
                message.IsFromBot ? "assistant" : "user",
                message.SenderId,
                message.SenderName,
                message.Content,
                message.SentAt))
            .ToListAsync(cancellationToken);
        var minimumConfidence = configuration.GetValue("ZaloBot:ClassifierConfidenceThreshold", .72);
        var aiAllowed = await IsAiCallAllowedAsync(activeConnectionId, groupId, incoming.SenderId, cancellationToken);
        if (decision.Intent == ZaloBotIntent.Unknown && aiAllowed)
        {
            decision = await ai.ClassifyAsync(new ZaloIntentClassifierContext(
                question,
                new ZaloAiSender(NormalizeId(incoming.SenderId), incoming.SenderName),
                recentMessageRows,
                sessions.Take(8).Select(session => new ZaloAiSessionReference(session.Id, session.Name, session.StartTime)).ToList(),
                DateTimeOffset.UtcNow.ToOffset(VietnamOffset)), cancellationToken);
            if (decision.Confidence >= minimumConfidence && decision.Intent != ZaloBotIntent.GeneralChat)
            {
                return await ExecuteClassifiedIntentAsync(
                    decision,
                    sessions,
                    normalizedQuestion,
                    activeConnectionId,
                    groupId,
                    incoming,
                    cancellationToken);
            }
        }
        var aiContext = new ZaloAiContext(
            groupId,
            new ZaloAiSender(NormalizeId(incoming.SenderId), incoming.SenderName),
            incoming.Content,
            recentMessageRows,
            sessions.Take(5).Select(session => new ZaloAiSession(
                session.Id,
                session.Name,
                session.StartTime,
                session.Location,
                session.ParkingInstructions,
                session.PlayerCount,
                session.Capacity,
                session.SenderIsListed,
                session.LatestPoll,
                session.PlayerNames)).ToList(),
            CombineSettings(sessions.Select(session => session.CustomInstructions)),
            learnedRules.Select(rule => new ZaloAiLearnedRule(rule.Trigger, rule.Answer, rule.CreatedBySenderName)).ToList(),
            DateTimeOffset.UtcNow.ToOffset(VietnamOffset));
        if (!aiAllowed)
        {
            return new BotAnswer("Bạn hỏi hơi nhanh rồi 😄 Chờ một chút rồi hỏi lại giúp mình nhé.", null, ZaloBotIntent.GeneralChat);
        }
        return new BotAnswer(
            await ai.AnswerAsync(aiContext, cancellationToken),
            null,
            ZaloBotIntent.GeneralChat,
            true,
            true);
    }

    private async Task<PendingResolution> ResolvePendingConversationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string normalizedQuestion,
        IReadOnlyList<SessionSnapshot> sessions,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null) return PendingResolution.None;
        if (state.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingResolution.None;
        }
        if (ZaloBotIntelligence.IsCancel(normalizedQuestion))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return new PendingResolution(true, null, null, null);
        }
        if (state.PendingIntent == ZaloBotIntent.RebalanceTeamsConfirm.ToString())
        {
            TeamRebalanceConfirmationPayload? payload;
            try { payload = JsonSerializer.Deserialize<TeamRebalanceConfirmationPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var actionSession = payload is null ? null : sessions.SingleOrDefault(session => session.Id == payload.SessionId);
            if (payload is not null && actionSession is not null && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(
                    false,
                    ZaloBotIntent.RebalanceTeamsConfirm,
                    actionSession,
                    null,
                    RebalancePlan: payload.Plan);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            return new PendingResolution(
                false,
                null,
                null,
                "Mình đang chờ xác nhận phương án cân bằng team. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ.");
        }
        if (state.PendingIntent is not null &&
            (state.PendingIntent == ZaloBotIntent.AutoDraftConfirm.ToString() ||
             state.PendingIntent == ZaloBotIntent.RedraftConfirm.ToString()))
        {
            List<string> actionSessionIds;
            try { actionSessionIds = JsonSerializer.Deserialize<List<string>>(state.PendingPayloadJson) ?? []; }
            catch (JsonException) { actionSessionIds = []; }
            var actionSession = sessions.SingleOrDefault(session => actionSessionIds.Contains(session.Id));
            if (ZaloBotIntelligence.IsConfirmation(normalizedQuestion) && actionSession is not null)
            {
                var confirmationIntent = state.PendingIntent == ZaloBotIntent.RedraftConfirm.ToString()
                    ? ZaloBotIntent.RedraftConfirm
                    : ZaloBotIntent.AutoDraftConfirm;
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(false, confirmationIntent, actionSession, null);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (ZaloBotIntelligence.TryGetExactCommand(normalizedQuestion, out _) ||
                newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            return new PendingResolution(
                false,
                null,
                null,
                state.PendingIntent == ZaloBotIntent.RedraftConfirm.ToString()
                    ? "Đang chờ xác nhận draft lại. Gõ @bot xác nhận draft lại để chạy hoặc @bot huỷ."
                    : "Đang chờ xác nhận tự draft. Gõ @bot xác nhận draft để chạy hoặc @bot huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.UndoActionConfirm.ToString())
        {
            UndoConfirmationPayload? payload;
            try { payload = JsonSerializer.Deserialize<UndoConfirmationPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var actionSession = payload is null ? null : sessions.SingleOrDefault(session => session.Id == payload.SessionId);
            if (payload is not null && actionSession is not null && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(false, ZaloBotIntent.UndoActionConfirm, actionSession, null, ActionHistoryId: payload.ActionId);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            return new PendingResolution(false, null, null,
                "Mình đang chờ xác nhận hoàn tác dữ liệu. Gõ @bot xác nhận để khôi phục, hoặc @bot huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.RepairShareSlotConfirm.ToString())
        {
            RepairShareSlotConfirmationPayload? payload;
            try { payload = JsonSerializer.Deserialize<RepairShareSlotConfirmationPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var actionSession = payload is null ? null : sessions.SingleOrDefault(session => session.Id == payload.SessionId);
            if (payload is not null && actionSession is not null && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(
                    false,
                    ZaloBotIntent.RepairShareSlotConfirm,
                    actionSession,
                    null,
                    RepairCommand: payload.Command);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            return new PendingResolution(false, null, null,
                "Mình đang chờ xác nhận sửa share slot. Gõ @bot xác nhận để cập nhật đội hình, hoặc @bot huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.RepairShareSlot.ToString())
        {
            RepairShareSlotSelectionPayload? payload;
            try { payload = JsonSerializer.Deserialize<RepairShareSlotSelectionPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var repairCandidates = payload is null
                ? []
                : sessions.Where(session => payload.SessionIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
            var repairMatchedIds = ZaloBotIntelligence.ResolveSessionReference(
                normalizedQuestion,
                repairCandidates.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList());
            var repairMatches = repairCandidates.Where(session => repairMatchedIds.Contains(session.Id)).ToList();
            if (payload is not null && repairMatches.Count == 1)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(
                    false,
                    ZaloBotIntent.RepairShareSlot,
                    repairMatches[0],
                    null,
                    RepairCommand: payload.Command);
            }
            var repairChoices = repairCandidates.Take(4).Select(FormatSessionChoice);
            return new PendingResolution(false, null, null,
                $"Mình vẫn chưa xác định được trận cần sửa. Bạn trả lời bằng thứ, ngày hoặc tên trận: {string.Join(", ", repairChoices)}; hoặc gõ huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.SlotTransferConfirm.ToString())
        {
            SlotTransferConfirmationPayload? payload;
            try { payload = JsonSerializer.Deserialize<SlotTransferConfirmationPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var actionSession = payload is null ? null : sessions.SingleOrDefault(session => session.Id == payload.SessionId);
            if (payload is not null && actionSession is not null && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(false, ZaloBotIntent.SlotTransferConfirm, actionSession, null,
                    TransferCommand: payload.Command);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            return new PendingResolution(false, null, null,
                "Mình đang chờ xác nhận chuyển slot. Gõ @bot xác nhận để cập nhật đội hình hoặc @bot huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.SlotTransfer.ToString())
        {
            SlotTransferSelectionPayload? payload;
            try { payload = JsonSerializer.Deserialize<SlotTransferSelectionPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var transferCandidates = payload is null
                ? []
                : sessions.Where(session => payload.SessionIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
            var selected = SelectSession(transferCandidates, normalizedQuestion);
            if (payload is not null && selected.Session is not null)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(false, ZaloBotIntent.SlotTransfer, selected.Session, null,
                    TransferCommand: payload.Command);
            }
            var transferChoices = transferCandidates.Take(4).Select(FormatSessionChoice);
            return new PendingResolution(false, null, null,
                $"Mình chưa xác định được trận cần chuyển slot. Bạn trả lời bằng thứ, ngày hoặc tên trận: {string.Join(", ", transferChoices)}; hoặc gõ huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.AddGuestPlayer.ToString())
        {
            AddGuestSelectionPayload? payload;
            try { payload = JsonSerializer.Deserialize<AddGuestSelectionPayload>(state.PendingPayloadJson); }
            catch (JsonException) { payload = null; }
            var guestCandidates = payload is null
                ? []
                : sessions.Where(session => payload.SessionIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
            var selected = SelectSession(guestCandidates, normalizedQuestion);
            if (payload is not null && selected.Session is not null)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(
                    false,
                    ZaloBotIntent.AddGuestPlayer,
                    selected.Session,
                    null,
                    GuestCommand: payload.Command);
            }
            var guestChoices = guestCandidates.Take(4).Select(FormatSessionChoice);
            return new PendingResolution(
                false,
                null,
                null,
                $"Mình chưa xác định được trận cần +1. Bạn trả lời bằng thứ, ngày hoặc tên trận: {string.Join(", ", guestChoices)}; hoặc gõ huỷ.");
        }
        if (state.PendingIntent == ZaloBotIntent.ScheduleReminderConfirm.ToString())
        {
            ReminderConfirmationPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ReminderConfirmationPayload>(state.PendingPayloadJson);
            }
            catch (JsonException)
            {
                payload = null;
            }
            var targetSessions = payload is null
                ? []
                : sessions.Where(session => payload.SessionIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
            if (payload is not null && targetSessions.Count > 0 && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingResolution(
                    false,
                    ZaloBotIntent.ScheduleReminderConfirm,
                    targetSessions[0],
                    null,
                    payload.Command,
                    targetSessions);
            }
            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
            if (ZaloBotIntelligence.TryGetExactCommand(normalizedQuestion, out _) ||
                newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                return PendingResolution.None;
            }
            var action = payload?.Command.Kind switch
            {
                ZaloReminderCommandKind.Update => "cập nhật lịch nhắc",
                ZaloReminderCommandKind.Disable => "tắt lịch nhắc",
                _ => "tạo lịch nhắc"
            };
            return new PendingResolution(
                false,
                null,
                null,
                $"Mình đang chờ xác nhận để {action}. Gõ @bot xác nhận để thực hiện hoặc @bot huỷ.");
        }
        if (ZaloBotIntelligence.TryGetExactCommand(normalizedQuestion, out _) ||
            ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent == ZaloBotIntent.Help)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingResolution.None;
        }
        var freshIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;
        if (freshIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingResolution.None;
        }
        if (!Enum.TryParse<ZaloBotIntent>(state.PendingIntent, out var intent))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingResolution.None;
        }
        List<string> candidateIds;
        try
        {
            candidateIds = JsonSerializer.Deserialize<List<string>>(state.PendingPayloadJson) ?? [];
        }
        catch (JsonException)
        {
            candidateIds = [];
        }
        var candidates = sessions.Where(session => candidateIds.Contains(session.Id)).ToList();
        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(
            normalizedQuestion,
            candidates.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList());
        var matches = candidates.Where(session => matchedIds.Contains(session.Id)).ToList();
        if (matches.Count == 1)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return new PendingResolution(false, intent, matches[0], null);
        }
        var choices = candidates.Take(4).Select(FormatSessionChoice);
        return new PendingResolution(false, null, null,
            $"Mình vẫn chưa xác định được trận. Bạn trả lời bằng thứ, ngày hoặc tên trận: {string.Join(", ", choices)}; hoặc gõ huỷ.");
    }

    private async Task<SessionSelection> SelectSessionAsync(
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string connectionId,
        string groupId,
        string senderId,
        ZaloBotIntent intent,
        CancellationToken cancellationToken)
    {
        var selected = SelectSession(sessions, normalizedQuestion);
        if (selected.Session is not null) return selected;

        var candidates = sessions.Where(IsUpcoming).Take(8).ToList();
        if (candidates.Count == 0) candidates = sessions.Take(8).ToList();
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = intent.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(candidates.Select(candidate => candidate.Id).ToList());
        state.PreviousCommand = intent.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15), 1, 120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return selected;
    }

    private async Task SaveActionConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        ZaloBotIntent pendingIntent,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = pendingIntent.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new[] { sessionId });
        state.PreviousCommand = pendingIntent == ZaloBotIntent.RedraftConfirm
            ? ZaloBotIntent.Redraft.ToString()
            : ZaloBotIntent.AutoDraft.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveTeamRebalanceConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        TeamRebalancePlan plan,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.RebalanceTeamsConfirm.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new TeamRebalanceConfirmationPayload(sessionId, plan));
        state.PreviousCommand = ZaloBotIntent.RebalanceTeams.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveReminderConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        IReadOnlyList<SessionSnapshot> targets,
        ZaloReminderCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.ScheduleReminderConfirm.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new ReminderConfirmationPayload(
            targets.Select(target => target.Id).ToList(),
            command));
        state.PreviousCommand = ZaloBotIntent.ScheduleReminder.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveUndoConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        string actionId,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.UndoActionConfirm.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new UndoConfirmationPayload(sessionId, actionId));
        state.PreviousCommand = ZaloBotIntent.UndoAction.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveRepairShareSlotConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        ZaloRepairShareSlotCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.RepairShareSlotConfirm.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new RepairShareSlotConfirmationPayload(sessionId, command));
        state.PreviousCommand = ZaloBotIntent.RepairShareSlot.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveRepairShareSlotSelectionAsync(
        string connectionId,
        string groupId,
        string senderId,
        IReadOnlyList<SessionSnapshot> candidates,
        ZaloRepairShareSlotCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.RepairShareSlot.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new RepairShareSlotSelectionPayload(
            candidates.Select(candidate => candidate.Id).ToList(),
            command));
        state.PreviousCommand = ZaloBotIntent.RepairShareSlot.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15), 1, 120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveSlotTransferConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        ZaloSlotTransferCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.SlotTransferConfirm.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new SlotTransferConfirmationPayload(sessionId, command));
        state.PreviousCommand = ZaloBotIntent.SlotTransfer.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveSlotTransferSelectionAsync(
        string connectionId,
        string groupId,
        string senderId,
        IReadOnlyList<SessionSnapshot> candidates,
        ZaloSlotTransferCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.SlotTransfer.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new SlotTransferSelectionPayload(
            candidates.Select(candidate => candidate.Id).ToList(), command));
        state.PreviousCommand = ZaloBotIntent.SlotTransfer.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15), 1, 120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveAddGuestSelectionAsync(
        string connectionId,
        string groupId,
        string senderId,
        IReadOnlyList<SessionSnapshot> candidates,
        ZaloAddGuestCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedSenderId = NormalizeId(senderId);
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == normalizedSenderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = normalizedSenderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.AddGuestPlayer.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(new AddGuestSelectionPayload(
            candidates.Select(candidate => candidate.Id).ToList(),
            command));
        state.PreviousCommand = ZaloBotIntent.AddGuestPlayer.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15), 1, 120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> BuildIncompleteProfilePromptAsync(SessionSnapshot session, bool appendToExistingText)
    {
        var incomplete = await draftService.GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
        if (!incomplete.IsSuccess || incomplete.Value is null || incomplete.Value.Count == 0) return string.Empty;
        var names = string.Join(", ", incomplete.Value.Take(10).Select(player =>
        {
            return $"{player.DisplayName} ({string.Join("/", GetMissingProfileFields(player))})";
        }));
        var prefix = appendToExistingText ? "\n\n" : string.Empty;
        return prefix +
               $"⛔ Chưa thể draft vì chưa xác nhận hồ sơ: {names}. " +
               "Cập nhật từng người, ví dụ `@bot cập nhật Nick Tran: nam` hoặc `@bot cập nhật Nick Tran: nam, công, trung bình`. " +
               "Nếu chỉ biết giới tính, bot sẽ giữ vị trí Người mới và trình độ Mới.";
    }

    private async Task<BotAnswer> ListIncompleteProfilesAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        var selected = await SelectSessionAsync(
            sessions,
            selector,
            connectionId,
            groupId,
            incoming.SenderId,
            decision.Intent,
            cancellationToken);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification, null, decision.Intent, aiCalled);

        var session = selected.Session!;
        var result = await draftService.GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
        if (!result.IsSuccess || result.Value is null)
            return new BotAnswer(result.Error ?? "Mình chưa đọc được hồ sơ người chơi của buổi này.", null, decision.Intent, aiCalled);
        if (result.Value.Count == 0)
            return new BotAnswer(
                $"Hồ sơ người chơi của {session.Name}{FormatShortTime(session.StartTime)} đã đầy đủ giới tính, vị trí và trình độ.",
                null,
                decision.Intent,
                aiCalled);

        var players = result.Value.Select((player, index) =>
            $"{index + 1}. {player.DisplayName} — thiếu {string.Join(", ", GetMissingProfileFields(player))}");
        var example = result.Value[0].DisplayName;
        return new BotAnswer(
            $"Danh sách cần cập nhật của {session.Name}{FormatShortTime(session.StartTime)} ({result.Value.Count} người):\n" +
            string.Join("\n", players) +
            $"\n\nCập nhật từng người, ví dụ:\n@bot cập nhật {example}: nam, công, trung bình\n" +
            $"Nếu mới biết giới tính: @bot cập nhật {example}: nam",
            null,
            decision.Intent,
            aiCalled);
    }

    private static IReadOnlyList<string> GetMissingProfileFields(IncompletePlayerProfile player)
    {
        var missing = new List<string>(3);
        if (player.MissingGender) missing.Add("giới tính");
        if (player.MissingRole) missing.Add("vị trí");
        if (player.MissingLevel) missing.Add("trình độ");
        return missing;
    }

    private async Task<string?> SyncPollBeforeDraftAsync(SessionSnapshot session, string optionReference)
    {
        if (session.Status is not (SessionStatus.Setup or SessionStatus.CaptainSelection) ||
            string.IsNullOrWhiteSpace(session.LatestPoll))
            return null;
        var synced = await zaloIntegration.SyncLatestPollAsync(session.AdminUserId, session.Id, optionReference);
        return synced.IsSuccess
            ? null
            : $"Chưa thể draft vì không đồng bộ lại được poll: {synced.Error}";
    }

    private async Task<BotAnswer> UpdatePlayerProfileAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        bool aiCalled)
    {
        var mentionedUsers = ExtractMentionedUsers(incoming);
        if (mentionedUsers.Count > 1)
        {
            return new BotAnswer(
                "Mỗi lần chỉ cập nhật một người. Hãy @mention đúng một thành viên, hoặc gõ chính xác tên một khách ngoài nhóm.",
                null,
                decision.Intent,
                aiCalled);
        }
        var selector = NormalizeText(string.Join(' ', new[]
        {
            normalizedQuestion,
            decision.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var selected = SelectSession(sessions, selector);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại lệnh cập nhật kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        var mentionedPlayer = mentionedUsers.SingleOrDefault();
        var playerReference = mentionedPlayer?.DisplayName ?? ExtractProfilePlayerReference(originalQuestion);
        if (string.IsNullOrWhiteSpace(playerReference))
            return new BotAnswer("Mình chưa nhận ra tên người cần cập nhật. Ví dụ: @bot cập nhật Nick Tran: nam, công, trung bình.", null, decision.Intent, aiCalled);
        var parsed = ParsePlayerProfileValues(originalQuestion, playerReference);
        if (parsed.Gender is null && parsed.Role is null && parsed.Level is null)
            return new BotAnswer("Mình chưa nhận ra thông tin hồ sơ. Dùng giới tính nam/nữ; vị trí công/thủ/chuyền 2/toàn diện; trình độ tốt/trung bình/mới.", null, decision.Intent, aiCalled);

        var profileBefore = await actionHistory.CaptureAsync(session.Id);
        var updated = await draftService.UpdatePlayerProfileFromBotAsync(
            session.AdminUserId,
            session.Id,
            playerReference,
            parsed.Gender,
            parsed.Role,
            parsed.Level,
            mentionedPlayer?.ZaloUserId);
        if (!updated.IsSuccess || updated.Value is null)
            return new BotAnswer(updated.Error ?? "Không cập nhật được hồ sơ.", null, decision.Intent, aiCalled);
        var player = updated.Value;
        await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
            "UpdatePlayerProfile", $"Cập nhật hồ sơ {player.DisplayName} trong {session.Name}",
            profileBefore, CancellationToken.None);
        var remaining = await draftService.GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
        var remainingText = remaining.IsSuccess && remaining.Value is { Count: > 0 }
            ? $" Còn hồ sơ chưa xác nhận: {string.Join(", ", remaining.Value.Take(10).Select(item => item.DisplayName))}."
            : " Hồ sơ đã đủ điều kiện để draft.";
        return new BotAnswer(
            $"Đã cập nhật {player.DisplayName}: {FormatGender(player.Gender)}, {FormatRole(player.Role)}, {FormatLevel(player.Level)}.{remainingText}",
            null,
            decision.Intent,
            aiCalled,
            ProtectedTerms:
            [
                player.DisplayName,
                session.Name,
                FormatGender(player.Gender),
                FormatRole(player.Role),
                FormatLevel(player.Level)
            ]);
    }

    private async Task<BotAnswer> AddGuestPlayerAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled,
        ZaloAddGuestCommand? confirmedCommand = null)
    {
        ZaloAddGuestCommand? command = confirmedCommand;
        if (command is null && ZaloNaturalCommandParser.TryParseAddGuest(originalQuestion, out var parsedCommand))
            command = parsedCommand;
        command = ZaloNaturalCommandParser.BindExplicitAddGuestMention(
                      originalQuestion,
                      ExtractMentionedUsers(incoming),
                      command) ?? command;
        if (command is null ||
            string.IsNullOrWhiteSpace(command.SponsorReference) && string.IsNullOrWhiteSpace(command.GuestDisplayName))
        {
            return new BotAnswer(
                "Mình chưa xác định được khách của ai. Bạn có thể nói: @bot @Ngọc Huyền thêm +1 bạn cho T6; nếu biết tên khách thì nói thêm “tên là ...”.",
                null,
                decision.Intent,
                aiCalled);
        }

        var selector = NormalizeText(string.Join(' ', new[]
        {
            normalizedQuestion,
            command.SessionReference,
            decision.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var selected = sessions.Count == 1 && confirmedCommand is not null
            ? new SessionSelection(sessions[0], null)
            : SelectSession(sessions, selector);
        if (selected.Clarification is not null)
        {
            await SaveAddGuestSelectionAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                sessions.Where(IsUpcoming).Take(8).ToList() is { Count: > 0 } upcoming
                    ? upcoming
                    : sessions.Take(8).ToList(),
                command,
                cancellationToken);
            return new BotAnswer(
                selected.Clarification + " Bạn chỉ cần trả lời thứ, ngày hoặc tên trận; bot vẫn giữ yêu cầu +1 này.",
                null,
                decision.Intent,
                aiCalled);
        }
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        var sponsorName = await ResolveGuestSponsorNameAsync(session, command, cancellationToken);
        if (!string.IsNullOrWhiteSpace(command.SponsorReference) && string.IsNullOrWhiteSpace(sponsorName))
        {
            return new BotAnswer(
                $"Mình chưa xác định chính xác '{command.SponsorReference}'. Hãy @mention người bảo lãnh khách, hoặc gõ đúng tên đang hiển thị trong nhóm.",
                null,
                decision.Intent,
                aiCalled);
        }
        var baseGuestName = !string.IsNullOrWhiteSpace(command.GuestDisplayName)
            ? command.GuestDisplayName!.Trim().TrimStart('@')
            : $"Bạn của {sponsorName}";
        var guestName = await BuildUniqueGuestNameAsync(session.Id, baseGuestName, cancellationToken);
        if (string.IsNullOrWhiteSpace(guestName))
            return new BotAnswer("Mình chưa nhận ra khách của ai. Ví dụ: @bot +1 số lượng vote cho bạn của Nick Tran.", null, decision.Intent, aiCalled);
        var guestBefore = await actionHistory.CaptureAsync(session.Id);
        var added = await draftService.AddGuestPlayerFromBotAsync(session.AdminUserId, session.Id, guestName);
        if (!added.IsSuccess || added.Value is null)
            return new BotAnswer(added.Error ?? "Không +1 được người chơi.", null, decision.Intent, aiCalled);
        var result = added.Value;
        await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
            "AddGuestPlayer", $"Thêm {result.Player.DisplayName} vào {session.Name}", guestBefore);
        var divisible = result.PresentPlayerCount % result.TeamCount == 0;
        var countText = divisible
            ? $"Tổng hiện tại {result.PresentPlayerCount}, đã chia hết cho {result.TeamCount} team."
            : $"Tổng hiện tại {result.PresentPlayerCount}, chưa chia hết cho {result.TeamCount} team.";
        return new BotAnswer(
            $"Đã thêm {result.Player.DisplayName} vào {session.Name} trên web. {countText} " +
            $"Khách chưa có hồ sơ; trước khi draft hãy cập nhật ít nhất giới tính bằng `@bot cập nhật {result.Player.DisplayName}: nam` hoặc `nữ`.",
            null,
            decision.Intent,
            aiCalled,
             ProtectedTerms: [result.Player.DisplayName, session.Name, result.PresentPlayerCount.ToString(CultureInfo.InvariantCulture), result.TeamCount.ToString(CultureInfo.InvariantCulture)]);
    }

    private async Task<string?> ResolveGuestSponsorNameAsync(
        SessionSnapshot session,
        ZaloAddGuestCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.SponsorZaloUserId))
        {
            var normalizedZaloId = NormalizeId(command.SponsorZaloUserId);
            var sessionPlayers = await db.SessionPlayers
                .AsNoTracking()
                .Where(player => player.SessionId == session.Id && player.IsPresent)
                .Select(player => new
                {
                    player.DisplayName,
                    ZaloUserId = player.PlayerProfile == null ? null : player.PlayerProfile.ZaloUserId
                })
                .ToListAsync(cancellationToken);
            var player = sessionPlayers.SingleOrDefault(item => NormalizeId(item.ZaloUserId ?? string.Empty) == normalizedZaloId);
            if (player is not null) return player.DisplayName;

            var members = await ResolveZaloMembersAsync(session, [normalizedZaloId], cancellationToken);
            if (members.TryGetValue(normalizedZaloId, out var member) && !string.IsNullOrWhiteSpace(member.DisplayName))
                return member.DisplayName.Trim().TrimStart('@');

            // The UID is still the identity source. The visible label captured from
            // that same mention is safe as a display-only fallback if member lookup
            // is temporarily unavailable.
            return string.IsNullOrWhiteSpace(command.SponsorReference)
                ? null
                : command.SponsorReference.Trim().TrimStart('@');
        }

        if (string.IsNullOrWhiteSpace(command.SponsorReference)) return null;
        var reference = NormalizeText(command.SponsorReference);
        var exactPlayers = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == session.Id && player.IsPresent)
            .Select(player => player.DisplayName)
            .ToListAsync(cancellationToken);
        var exact = exactPlayers
            .Where(name => NormalizeText(name) == reference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return exact.Count == 1 ? exact[0] : null;
    }

    private async Task<string> BuildUniqueGuestNameAsync(
        string sessionId,
        string baseGuestName,
        CancellationToken cancellationToken)
    {
        var cleanBase = ZaloNaturalCommandParser.RemoveTrailingSessionReference(baseGuestName, out _)
            .Trim(' ', ',', '.', ':', ';', '@');
        if (cleanBase.Length == 0) return string.Empty;
        if (cleanBase.Length > 150) cleanBase = cleanBase[..150].TrimEnd();

        var existing = (await db.SessionPlayers
                .AsNoTracking()
                .Where(player => player.SessionId == sessionId && player.IsPresent)
                .Select(player => player.DisplayName)
                .ToListAsync(cancellationToken))
            .Select(NormalizeText)
            .ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(NormalizeText(cleanBase))) return cleanBase;
        for (var suffix = 2; suffix <= 99; suffix += 1)
        {
            var candidate = $"{cleanBase} #{suffix}";
            if (!existing.Contains(NormalizeText(candidate))) return candidate;
        }
        return $"{cleanBase} #{Guid.NewGuid():N}"[..Math.Min(160, cleanBase.Length + 10)];
    }

    private async Task<BotAnswer> HandleTeamPreferenceAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        ZaloTeamPreferenceCommand? command = null;
        if (ZaloNaturalCommandParser.TryParseTeamPreference(originalQuestion, out var parsed))
            command = parsed;
        var mentionedUsers = ExtractMentionedUsers(incoming);
        command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(mentionedUsers, command) ?? command;
        if (command is null || command.PlayerReferences.Count != 2)
        {
            return new BotAnswer(
                "Mình chưa xác định đủ hai người muốn ở cùng team. Hãy @mention cả hai, ví dụ: @bot @To An muốn chơi chung team với @Anh Duy thứ 6.",
                null,
                decision.Intent,
                aiCalled);
        }

        var selector = NormalizeText(string.Join(' ', new[]
        {
            normalizedQuestion,
            command.SessionReference,
            decision.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var selected = SelectSession(sessions, selector);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại yêu cầu chung team kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        var inputs = command.PlayerReferences.Select((name, index) =>
            new ShareSlotParticipantInput(
                name.Trim().TrimStart('@'),
                command.PlayerZaloUserIds is { Count: > 0 } && index < command.PlayerZaloUserIds.Count
                    ? command.PlayerZaloUserIds[index]
                    : null)).ToList();
        var before = await actionHistory.CaptureAsync(session.Id, cancellationToken);
        var created = await draftService.CreateTeamPreferenceGroupFromBotAsync(
            session.AdminUserId,
            session.Id,
            inputs);
        if (!created.IsSuccess || created.Value is null)
            return new BotAnswer(created.Error ?? "Chưa ghi nhận được yêu cầu chung team.", null, decision.Intent, aiCalled);

        var names = created.Value.PlayerNames;
        await actionHistory.RecordAsync(
            session.Id,
            incoming.SenderId,
            incoming.SenderName,
            "TeamPreference",
            $"Ghi nhận {string.Join(" và ", names)} muốn chung team trong {session.Name}",
            before,
            cancellationToken);
        return new BotAnswer(
            $"Đã ghi nhận {string.Join(" và ", names)} muốn ở cùng team trong {session.Name}. Khi draft, bot sẽ cố xếp hai người chung đội; đây không phải share slot.",
            null,
            decision.Intent,
            aiCalled,
            ProtectedTerms: names.Append(session.Name).ToList());
    }

    private async Task<BotAnswer> ShareSlotAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        ZaloNaturalCommandParser.TryParseShareSlot(originalQuestion, out var fallbackCommand);
        if (fallbackCommand.RequestedPartnerCount == 0 &&
            ZaloBotIntelligence.TryExtractSharePlayerNames(originalQuestion, out var legacyAnchor, out var legacyPartner))
        {
            fallbackCommand = new ZaloShareSlotCommand(legacyAnchor, [legacyPartner], 1);
        }
        var mentionedUsers = ExtractMentionedUsers(incoming);
        ZaloShareSlotCommand? aiCommand = null;
        if (ai.IsConfigured)
        {
            aiCommand = await ai.ParseShareSlotCommandAsync(
                new ZaloNaturalShareContext(
                    originalQuestion,
                    incoming.SenderName,
                    mentionedUsers,
                    sessions.Take(10).Select(session => new ZaloAiSessionReference(session.Id, session.Name, session.StartTime)).ToList()),
                cancellationToken);
            aiCalled |= aiCommand is not null;
        }
        var command = aiCommand ?? (fallbackCommand.RequestedPartnerCount > 0 ? fallbackCommand : null);
        var explicitMentionCommand = ZaloNaturalCommandParser.BindExplicitShareMentions(mentionedUsers, command);
        if (explicitMentionCommand is not null)
        {
            logger.LogInformation(
                "Share slot command bound from explicit mentions Anchor={Anchor} Partners={Partners} AiCommand={AiCommand}",
                explicitMentionCommand.Anchor,
                string.Join(", ", explicitMentionCommand.Partners),
                aiCommand is not null);
            command = explicitMentionCommand;
        }
        if (command is null)
            return new BotAnswer("Mình chưa nhận ra người chính và người chơi chung. Ví dụ: @bot Nick Tran muốn share slot với An; hoặc @bot Nick Tran xin +2 cho An và Bình.", null, decision.Intent, aiCalled);

        var senderAliases = new[] { "tui", "toi", "minh", "em", "anh", "chi", "ban than" };
        var requestedOwnSlot = senderAliases.Contains(NormalizeText(command.Anchor), StringComparer.Ordinal) ||
                               NormalizeText(command.Anchor) == NormalizeText(incoming.SenderName);
        var rawAnchor = requestedOwnSlot
            ? incoming.SenderName
            : command.Anchor;
        var partners = command.Partners
            .Select(name => name.Trim().TrimStart('@'))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (command.RequestedPartnerCount is < 1 or > 2 || partners.Count != command.RequestedPartnerCount)
        {
            return new BotAnswer(
                command.RequestedPartnerCount == 2
                    ? "Lệnh +2 phải có đúng hai người khác nhau. Ví dụ: @bot Nick Tran xin +2 cho An và Bình."
                    : "Lệnh +1 phải có đúng một người chơi chung.",
                null,
                decision.Intent,
                aiCalled);
        }

        var selector = NormalizeText(string.Join(' ', new[]
        {
            normalizedQuestion,
            command.SessionReference,
            decision.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var matchingSessions = sessions
            .Where(session => ResolvePlayerReference(rawAnchor, session.PlayerNames) is not null ||
                              (requestedOwnSlot && session.SenderIsListed))
            .ToList();
        var finishedMatchingSessions = matchingSessions.Where(session => session.Status == SessionStatus.Finished).ToList();
        var selected = finishedMatchingSessions.Count == 1
            ? new SessionSelection(finishedMatchingSessions[0], null)
            : matchingSessions.Count == 1
                ? new SessionSelection(matchingSessions[0], null)
            : SelectSession(sessions, selector);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại đầy đủ lệnh share slot kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var mentionedMembers = await ResolveZaloMembersAsync(
            session,
            mentionedUsers.Select(user => user.ZaloUserId),
            cancellationToken);
        var isSelfServicePreDraft = session.Status != SessionStatus.Finished &&
                                    requestedOwnSlot &&
                                    session.SenderIsListed;
        if (!isSelfServicePreDraft)
        {
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
            if (denial is not null) return denial;
        }
        var anchor = isSelfServicePreDraft && !string.IsNullOrWhiteSpace(session.SenderPlayerName)
            ? session.SenderPlayerName
            : ResolvePlayerReference(rawAnchor, session.PlayerNames);
        if (anchor is null)
            return new BotAnswer($"Không tìm thấy '{rawAnchor}' trong đội hình.", null, decision.Intent, aiCalled);
        var shareBefore = await actionHistory.CaptureAsync(session.Id, cancellationToken);

        if (session.Status != SessionStatus.Finished)
        {
            var participantInputs = partners.Select(partnerName =>
            {
                var existing = ResolvePlayerReference(partnerName, session.PlayerNames);
                var mention = FindMentionedUser(partnerName, mentionedUsers);
                var mentionId = mention?.ZaloUserId;
                mentionedMembers.TryGetValue(NormalizeId(mentionId ?? string.Empty), out var member);
                return new ShareSlotParticipantInput(existing ?? partnerName, mentionId, member?.AvatarUrl);
            }).ToList();
            var shared = await draftService.SharePreDraftSlotAsync(
                session.AdminUserId,
                session.Id,
                anchor,
                participantInputs);
            if (!shared.IsSuccess || shared.Value is null)
                return new BotAnswer(shared.Error ?? "Không cập nhật được share slot trước draft.", null, decision.Intent, aiCalled);
            var result = shared.Value;
            await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
                "ShareSlot", $"Ghép share slot {result.SlotDisplayName} trong {session.Name}", shareBefore, cancellationToken);
            var profileNote = result.NeedsProfileUpdateNames.Count == 0
                ? string.Empty
                : $" Cần cập nhật ít nhất giới tính trước khi draft cho: {string.Join(", ", result.NeedsProfileUpdateNames)}.";
            return new BotAnswer(
                $"Đã ghép {result.SlotDisplayName} thành một share slot của {session.Name}. Danh sách có {result.PresentPlayerCount} người nhưng draft tính {result.EffectiveSlotCount} slot.{profileNote}",
                null,
                decision.Intent,
                aiCalled);
        }

        var completed = new List<PostDraftSharedSlotResult>();
        foreach (var rawPartner in partners)
        {
            var existingPartner = ResolvePlayerReference(rawPartner, session.PlayerNames);
            var partner = existingPartner ?? (NormalizeText(rawPartner) == "ban"
                ? NextExternalShareName(anchor, session.PlayerNames.Concat(completed.Select(item => item.PartnerPlayerName)).ToList())
                : rawPartner);
            var mention = FindMentionedUser(rawPartner, mentionedUsers);
            var mentionId = mention?.ZaloUserId;
            mentionedMembers.TryGetValue(NormalizeId(mentionId ?? string.Empty), out var member);
            var shared = await draftService.SharePostDraftSlotAsync(
                session.AdminUserId,
                session.Id,
                anchor,
                new ShareSlotParticipantInput(partner, mentionId, member?.AvatarUrl));
            if (!shared.IsSuccess || shared.Value is null)
                return new BotAnswer(shared.Error ?? "Không cập nhật được share slot.", null, decision.Intent, aiCalled);
            completed.Add(shared.Value);
        }
        var profileNames = completed.Where(item => item.NeedsProfileUpdate).Select(item => item.PartnerPlayerName).ToList();
        await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
            "ShareSlot", $"Ghép {anchor} share slot với {string.Join(" và ", completed.Select(item => item.PartnerPlayerName))} trong {session.Name}",
            shareBefore, cancellationToken);
        var postDraftProfileNote = profileNames.Count == 0
            ? string.Empty
            : $" Cần cập nhật giới tính cho: {string.Join(", ", profileNames)} nếu draft lại.";
        return new BotAnswer(
            $"Đã ghép {anchor} share slot với {string.Join(" và ", completed.Select(item => item.PartnerPlayerName))} tại {completed[0].TeamName}.{postDraftProfileNote}",
            null,
            decision.Intent,
            aiCalled);
    }

    private static string NextExternalShareName(string anchor, IReadOnlyList<string> playerNames)
    {
        var baseName = $"Bạn share cùng {anchor}";
        var existing = playerNames.Select(NormalizeText).ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(NormalizeText(baseName))) return baseName;
        for (var index = 2; index <= 20; index += 1)
        {
            var candidate = $"{baseName} #{index}";
            if (!existing.Contains(NormalizeText(candidate))) return candidate;
        }
        return $"{baseName} #{Guid.NewGuid().ToString("n")[..4]}";
    }

    private async Task<BotAnswer> SwapTeamPlayersAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        bool aiCalled)
    {
        var sessionsWithBothNames = sessions
            .Where(session => FindMentionedPlayerNames(originalQuestion, session.PlayerNames).Count >= 2)
            .ToList();
        var finishedSessionsWithBothNames = sessionsWithBothNames.Where(session => session.Status == SessionStatus.Finished).ToList();
        SessionSelection selected;
        if (finishedSessionsWithBothNames.Count == 1)
        {
            selected = new SessionSelection(finishedSessionsWithBothNames[0], null);
        }
        else if (sessionsWithBothNames.Count == 1)
        {
            selected = new SessionSelection(sessionsWithBothNames[0], null);
        }
        else
        {
            selected = SelectSession(sessions, normalizedQuestion);
        }
        if (selected.Clarification is not null)
            return new BotAnswer(
                selected.Clarification + " Hãy gửi lại đầy đủ lệnh đổi người kèm ngày hoặc tên trận.",
                null,
                decision.Intent,
                aiCalled);

        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;
        var mentionedNames = FindMentionedPlayerNames(originalQuestion, session.PlayerNames);
        string firstPlayer;
        string secondPlayer;
        if (mentionedNames.Count == 2)
        {
            firstPlayer = mentionedNames[0];
            secondPlayer = mentionedNames[1];
        }
        else if (!ZaloBotIntelligence.TryExtractSwapPlayerNames(originalQuestion, out firstPlayer, out secondPlayer))
        {
            return new BotAnswer(
                "Mình chưa nhận ra đúng hai người cần đổi. Bạn ghi theo mẫu: @bot đổi vị trí Player A với Nick Tran.",
                null,
                decision.Intent,
                aiCalled);
        }

        var swapBefore = await actionHistory.CaptureAsync(session.Id);
        var swapped = await draftService.SwapDraftPlayersAsync(
            session.AdminUserId,
            session.Id,
            firstPlayer,
            secondPlayer);
        if (!swapped.IsSuccess || swapped.Value is null)
            return new BotAnswer(swapped.Error ?? "Không đổi được hai người này.", null, decision.Intent, aiCalled);
        var result = swapped.Value;
        await actionHistory.RecordAsync(session.Id, incoming.SenderId, incoming.SenderName,
            "SwapTeamPlayers", $"Đổi vị trí {result.FirstPlayerName} với {result.SecondPlayerName} trong {session.Name}", swapBefore);
        return new BotAnswer(
            $"Đã đổi {result.FirstPlayerName} từ {result.FirstPreviousTeamName} sang {result.SecondPreviousTeamName}, " +
            $"và {result.SecondPlayerName} từ {result.SecondPreviousTeamName} sang {result.FirstPreviousTeamName}.\n" +
            FormatTeamLineup(session.Name, result.State.TeamPreview),
            teamCards.GetPublicUrl(session.Id),
            decision.Intent,
            aiCalled);
    }

    private async Task<BotAnswer> RebalanceTeamsAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        if (!ZaloBotIntelligence.TryParseTeamPair(originalQuestion, out var firstTeamOrdinal, out var secondTeamOrdinal))
        {
            return new BotAnswer(
                "Mình hiểu bạn muốn cân bằng đội hình nhưng chưa nhận ra đúng hai team. Bạn ghi rõ như: @bot cân bằng team 2 và team 3, hoặc @bot cân bằng team A-C.",
                null,
                decision.Intent,
                aiCalled);
        }

        var selected = await SelectSessionAsync(
            sessions,
            normalizedQuestion,
            connectionId,
            groupId,
            incoming.SenderId,
            decision.Intent,
            cancellationToken);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification, null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        var preview = await draftService.PreviewTeamRebalanceAsync(
            session.AdminUserId,
            session.Id,
            firstTeamOrdinal,
            secondTeamOrdinal,
            cancellationToken);
        if (!preview.IsSuccess || preview.Value is null)
            return new BotAnswer(preview.Error ?? "Không tính được phương án cân bằng.", null, decision.Intent, aiCalled);
        var plan = preview.Value;
        var facts = FormatTeamRebalanceFacts(plan);
        if (plan.Moves.Count == 0)
        {
            return new BotAnswer(
                $"{plan.FirstTeamName} và {plan.SecondTeamName} đã là phương án cân bằng tốt nhất mà vẫn giữ nguyên đội trưởng, share slot và nhóm muốn chơi chung.\n{facts}",
                null,
                decision.Intent,
                aiCalled,
                ProtectedTerms: [facts]);
        }

        await SaveTeamRebalanceConfirmationAsync(
            connectionId,
            groupId,
            incoming.SenderId,
            session.Id,
            plan,
            cancellationToken);
        return new BotAnswer(
            $"Mình đã tính phương án cân bằng {plan.FirstTeamName} và {plan.SecondTeamName}; team còn lại được giữ nguyên.\n{facts}\nNếu thấy ổn, gõ @bot xác nhận để áp dụng hoặc @bot huỷ.",
            null,
            decision.Intent,
            aiCalled,
            ProtectedTerms: [facts, "@bot xác nhận", "@bot huỷ"]);
    }

    private static string FormatTeamRebalanceFacts(TeamRebalancePlan plan)
    {
        var beforeDifference = Math.Abs(plan.FirstBeforeScore - plan.SecondBeforeScore);
        var afterDifference = Math.Abs(plan.FirstAfterScore - plan.SecondAfterScore);
        var lines = new List<string>
        {
            $"Điểm trước: {plan.FirstTeamName} {FormatDraftScore(plan.FirstBeforeScore)} — {plan.SecondTeamName} {FormatDraftScore(plan.SecondBeforeScore)} (lệch {FormatDraftScore(beforeDifference)}).",
            $"Điểm dự kiến: {plan.FirstTeamName} {FormatDraftScore(plan.FirstAfterScore)} — {plan.SecondTeamName} {FormatDraftScore(plan.SecondAfterScore)} (lệch {FormatDraftScore(afterDifference)})."
        };
        if (plan.Moves.Count == 0)
        {
            lines.Add("Không cần chuyển slot nào.");
        }
        else
        {
            lines.Add("Các slot sẽ chuyển (người share vẫn đi cùng nhau):");
            lines.AddRange(plan.Moves.Select(move =>
                $"- {move.SlotDisplayName}: {move.FromTeamName} → {move.ToTeamName} ({FormatDraftScore(move.AverageScore)} điểm)"));
        }
        return string.Join("\n", lines);
    }

    private static string FormatDraftScore(double score) =>
        score.ToString("0.##", CultureInfo.InvariantCulture);

    private static List<string> FindMentionedPlayerNames(
        string question,
        IReadOnlyList<string> playerNames)
    {
        var normalizedQuestion = NormalizeText(question);
        return playerNames
            .Select(name => new { Name = name, Normalized = NormalizeText(name) })
            .Where(item => item.Normalized.Length > 0 && normalizedQuestion.Contains(item.Normalized, StringComparison.Ordinal))
            .GroupBy(item => item.Normalized, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => normalizedQuestion.IndexOf(item.Normalized, StringComparison.Ordinal))
            .Select(item => item.Name)
            .ToList();
    }

    private static string? FindBestMentionedPlayerName(string question, IReadOnlyList<string> playerNames)
    {
        var normalizedQuestion = NormalizeText(question);
        return playerNames
            .Where(name =>
            {
                var normalizedName = NormalizeText(name);
                return normalizedName.Length > 0 && normalizedQuestion.Contains(normalizedName, StringComparison.Ordinal);
            })
            .OrderByDescending(name => NormalizeText(name).Length)
            .FirstOrDefault();
    }

    private static string? ResolvePlayerReference(string reference, IReadOnlyList<string> playerNames)
    {
        var normalizedReference = NormalizeText(reference);
        var exact = playerNames.Where(name => NormalizeText(name) == normalizedReference).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = playerNames
            .Where(name =>
            {
                var normalizedName = NormalizeText(name);
                return normalizedReference.Contains(normalizedName, StringComparison.Ordinal) ||
                       normalizedName.Contains(normalizedReference, StringComparison.Ordinal);
            })
            .OrderByDescending(name => NormalizeText(name).Length)
            .ToList();
        return partial.Count == 0 ? null : partial[0];
    }

    private static string? ExtractProfilePlayerReference(string question)
    {
        var match = Regex.Match(
            question,
            @"^cập\s+nhật(?:\s+(?:thông\s+tin|hồ\s+sơ|trình\s+độ|giới\s+tính))?\s+(?<name>.+?)(?::|,|\s+là\s+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var name = ZaloNaturalCommandParser.RemoveTrailingSessionReference(
            match.Groups["name"].Value,
            out _);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static PlayerProfileValues ParsePlayerProfileValues(string question, string playerReference)
    {
        var separatorIndex = question.IndexOf(':');
        var descriptorSource = separatorIndex >= 0 && separatorIndex + 1 < question.Length
            ? question[(separatorIndex + 1)..]
            : question;
        var descriptor = NormalizeText(descriptorSource);
        if (separatorIndex < 0)
        {
            var normalizedName = NormalizeText(playerReference);
            var nameIndex = descriptor.IndexOf(normalizedName, StringComparison.Ordinal);
            if (nameIndex >= 0) descriptor = descriptor.Remove(nameIndex, normalizedName.Length);
        }
        descriptor = Regex.Replace(
            descriptor,
            @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?![a-z0-9])",
            " ",
            RegexOptions.CultureInvariant);

        PlayerGender? gender = null;
        if (ContainsToken(descriptor, "nu") || ContainsToken(descriptor, "female")) gender = PlayerGender.Female;
        else if (ContainsToken(descriptor, "nam") || ContainsToken(descriptor, "male")) gender = PlayerGender.Male;

        PlayerRole? role = null;
        if (HasAny(descriptor, "chuyen 2", "chuyen hai", "setter")) role = PlayerRole.Setter;
        else if (HasAny(descriptor, "fullstack", "full stack", "toan dien")) role = PlayerRole.FullStack;
        else if (HasAny(descriptor, "phong thu", "danh thu", "libero") || ContainsToken(descriptor, "thu")) role = PlayerRole.Defense;
        else if (HasAny(descriptor, "tan cong", "danh cong", "attack") || ContainsToken(descriptor, "cong")) role = PlayerRole.Attack;
        else if (HasAny(descriptor, "nguoi moi", "role moi", "vi tri moi")) role = PlayerRole.New;

        PlayerLevel? level = null;
        if (HasAny(descriptor, "trung binh", "average") || ContainsToken(descriptor, "tb")) level = PlayerLevel.Average;
        else if (HasAny(descriptor, "trinh do tot", "level tot", "choi tot", "good") || ContainsToken(descriptor, "kha")) level = PlayerLevel.Good;
        else if (HasAny(descriptor, "moi choi", "trinh do moi", "level moi")) level = PlayerLevel.New;
        return new PlayerProfileValues(gender, role, level);
    }

    private static string? ExtractGuestName(string question)
    {
        var friend = Regex.Match(
            question,
            @"bạn\s+của\s+(?<sponsor>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (friend.Success)
        {
            var sponsor = friend.Groups["sponsor"].Value.Trim(' ', ',', '.', ':', ';');
            return sponsor.Length == 0 ? null : $"Bạn của {sponsor}";
        }
        var named = Regex.Match(
            question,
            @"(?:\+1|thêm\s+1|cộng\s+1).*(?:cho|người)\s+(?<guest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return named.Success ? named.Groups["guest"].Value.Trim(' ', ',', '.', ':', ';') : null;
    }

    private static string FormatGender(PlayerGender gender) => gender switch
    {
        PlayerGender.Male => "nam",
        PlayerGender.Female => "nữ",
        _ => "chưa xác định"
    };

    private static string FormatRole(PlayerRole role) => role switch
    {
        PlayerRole.Attack => "công",
        PlayerRole.Defense => "thủ",
        PlayerRole.Setter => "chuyền 2",
        PlayerRole.FullStack => "toàn diện",
        _ => "Người mới"
    };

    private static string FormatLevel(PlayerLevel level) => level switch
    {
        PlayerLevel.Good => "tốt",
        PlayerLevel.Average => "trung bình",
        _ => "Mới"
    };

    private async Task<BotAnswer> HandleReminderCommandAsync(
        ZaloReminderCommand command,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled,
        bool confirmed = false,
        IReadOnlyList<SessionSnapshot>? forcedTargets = null)
    {
        await db.ZaloBotConversationStates
            .Where(state => state.ZaloConnectionId == connectionId &&
                            state.GroupId == groupId &&
                            state.SenderZaloUserId == NormalizeId(incoming.SenderId))
            .ExecuteDeleteAsync(cancellationToken);
        var intent = command.Kind switch
        {
            ZaloReminderCommandKind.Status => ZaloBotIntent.ReminderStatus,
            ZaloReminderCommandKind.Disable => ZaloBotIntent.CancelReminder,
            _ => ZaloBotIntent.ScheduleReminder
        };
        var now = DateTimeOffset.UtcNow;
        var upcoming = sessions
            .Where(session => session.StartTime is not null && session.StartTime > now)
            .OrderBy(session => session.StartTime)
            .ToList();
        if (upcoming.Count == 0)
            return new BotAnswer("Nhóm chưa có trận sắp tới có thời gian cụ thể để lên lịch nhắc.", null, intent, aiCalled);

        var originalReminderQuestion = ExtractQuestion(incoming);
        var deterministicCommand = confirmed
            ? command
            : ZaloNaturalCommandParser.EnrichReminder(originalReminderQuestion, command);
        if (!confirmed && ai.IsConfigured && command.Kind is ZaloReminderCommandKind.Schedule or ZaloReminderCommandKind.Update or ZaloReminderCommandKind.TriggerNow)
        {
            var recentReminderMessages = await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(message => message.ZaloConnectionId == connectionId && message.GroupId == groupId)
                .OrderByDescending(message => message.SentAt)
                .Take(20)
                .OrderBy(message => message.SentAt)
                .Select(message => new ZaloAiMessage(
                    message.IsFromBot ? "assistant" : "user",
                    message.SenderId,
                    message.SenderName,
                    message.Content,
                    message.SentAt))
                .ToListAsync(cancellationToken);
            var extracted = await ai.ParseReminderCommandAsync(
                new ZaloNaturalReminderContext(
                    originalReminderQuestion,
                    incoming.SenderName,
                    sessions.Take(10).Select(session => new ZaloAiSessionReference(session.Id, session.Name, session.StartTime)).ToList(),
                    DateTimeOffset.UtcNow.ToOffset(VietnamOffset),
                    recentReminderMessages),
                cancellationToken);
            if (extracted is not null)
            {
                var includePaymentQr = deterministicCommand.IncludePaymentQr;
                command = extracted with
                {
                    // AI extracts entities and phrasing; the validated router owns the
                    // operation so a model response cannot turn a query into a mutation.
                    Kind = command.Kind,
                    LocalTime = extracted.LocalTime ?? deterministicCommand.LocalTime,
                    DelayMinutes = (extracted.LocalTime ?? deterministicCommand.LocalTime) is not null
                        ? null
                        : extracted.DelayMinutes ?? deterministicCommand.DelayMinutes,
                    ExplicitLocalDate = extracted.ExplicitLocalDate ?? deterministicCommand.ExplicitLocalDate,
                    UseSessionDate = extracted.UseSessionDate || deterministicCommand.UseSessionDate,
                    // Prefer the AI's final wording, but reject a response that merely
                    // repeats the scheduling instruction. Quoted text remains a safe fallback.
                    CustomMessage = SelectReminderMessage(
                        extracted.CustomMessage,
                        deterministicCommand.CustomMessage,
                        originalReminderQuestion),
                    Audience = HasExplicitAllAudience(originalReminderQuestion)
                        ? ZaloReminderAudience.All
                        : extracted.Audience == ZaloReminderAudience.All
                            ? deterministicCommand.Audience
                            : extracted.Audience,
                    OnlyIfMissingSlots = !includePaymentQr &&
                                         (extracted.OnlyIfMissingSlots || deterministicCommand.OnlyIfMissingSlots),
                    SessionReferences = (extracted.SessionReferences ?? [])
                        .Concat(deterministicCommand.SessionReferences ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StopWhenFull = !includePaymentQr &&
                                   (extracted.StopWhenFull || deterministicCommand.StopWhenFull),
                    AllowAfterSessionStart = deterministicCommand.AllowAfterSessionStart,
                    IncludePaymentQr = includePaymentQr
                };
                aiCalled = true;
            }
            else
            {
                command = deterministicCommand;
            }
        }
        else if (!confirmed)
        {
            command = deterministicCommand;
        }
        if (!confirmed &&
            command.Kind == ZaloReminderCommandKind.Update &&
            !HasExplicitReminderMessageChange(originalReminderQuestion))
        {
            command = command with { CustomMessage = null };
        }
        normalizedQuestion = NormalizeText(string.Join(' ', new[] { normalizedQuestion }
            .Concat(command.SessionReferences ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))));

        List<SessionSnapshot> targets;
        var hasForcedTargets = forcedTargets is not null;
        var hasExplicitSelector = hasForcedTargets || HasExplicitSessionSelector(sessions, normalizedQuestion);
        if (hasForcedTargets)
        {
            var forcedIds = forcedTargets!.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            targets = upcoming.Where(session => forcedIds.Contains(session.Id)).ToList();
        }
        else if (hasExplicitSelector)
        {
            targets = upcoming.Where(session => QuestionMatchesSession(normalizedQuestion, session)).ToList();
            if (targets.Count == 0)
            {
                return new BotAnswer(
                    $"Mình không tìm thấy trận đúng ngày/tên đó. Hãy gửi lại cả lệnh kèm một trong các trận: {string.Join(", ", upcoming.Take(5).Select(FormatSessionChoice))}.",
                    null,
                    intent,
                    aiCalled);
            }
            if (targets.Count > 1 && command.SessionReferences is null)
            {
                return new BotAnswer(
                    $"Có nhiều trận khớp: {string.Join(", ", targets.Take(5).Select(FormatSessionChoice))}. Hãy gửi lại cả lệnh kèm ngày cụ thể.",
                    null,
                    intent,
                    aiCalled);
            }
        }
        else
        {
            targets = upcoming;
        }
        if (!hasForcedTargets && !hasExplicitSelector &&
            (command.Kind == ZaloReminderCommandKind.TriggerNow ||
             command.Kind == ZaloReminderCommandKind.Schedule && !command.Repeats))
        {
            targets = [targets.FirstOrDefault(session => session.PlayerCount < session.Capacity) ?? targets[0]];
        }

        if (command.Kind == ZaloReminderCommandKind.Status)
        {
            var statusTargetIds = targets.Select(item => item.Id).ToList();
            var naturalSchedules = await db.ZaloReminderSchedules
                .AsNoTracking()
                .Where(schedule => statusTargetIds.Contains(schedule.SessionId) && schedule.Enabled)
                .OrderBy(schedule => schedule.NextRunAt)
                .ToListAsync(cancellationToken);
            var lines = new List<string>();
            if (naturalSchedules.Count > 0)
            {
                var sessionNames = targets.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
                lines.AddRange(naturalSchedules.Select((schedule, index) =>
                {
                    var recipients = schedule.Audience == ZaloReminderAudience.Roster
                        ? "những người có tên trong danh sách"
                        : "cả nhóm (@all)";
                    var frequency = schedule.Repeats && schedule.IntervalMinutes is not null
                        ? $"lặp lại mỗi {FormatDuration(schedule.IntervalMinutes.Value)}"
                        : "chỉ gửi một lần";
                    var condition = schedule.OnlyIfMissingSlots
                        ? schedule.StopWhenFull || ZaloNaturalCommandParser.RequestsStopWhenFull(schedule.Message)
                            ? ", tự dừng khi đủ slot"
                            : ", chỉ gửi khi vẫn còn thiếu slot"
                        : string.Empty;
                    var attachment = schedule.IncludePaymentQr
                        ? ", gửi kèm QR thanh toán"
                        : string.Empty;
                    var content = FormatReminderContentForDisplay(schedule.Message);
                    return $"{index + 1}. {sessionNames.GetValueOrDefault(schedule.SessionId, schedule.SessionId)} — {FormatVietnamTime(schedule.NextRunAt)}\n" +
                           $"   Gửi cho {recipients}, {frequency}{condition}{attachment}.\n   {content}";
                }));
            }
            var legacyTargets = targets.Where(session => session.ReminderEnabled).ToList();
            foreach (var session in legacyTargets)
            {
                var next = session.NextReminderAt is null
                    ? "đang chờ hệ thống tính lượt đầu"
                    : $"lần kiểm tra kế tiếp khoảng {FormatVietnamTime(session.NextReminderAt.Value)}";
                var repeat = session.ReminderRepeats
                    ? $", lặp mỗi {FormatDuration(session.ReminderIntervalMinutes)}"
                    : ", chỉ gửi một lần";
                lines.Add($"{lines.Count + 1}. {session.Name} — {next}{repeat}, nhắc cả nhóm nếu còn thiếu slot.");
            }
            if (lines.Count == 0)
                return new BotAnswer("Hiện chưa có lịch nhắc nào đang hoạt động cho các trận sắp tới.", null, intent, aiCalled);
            return new BotAnswer(
                $"Bạn đang có {lines.Count} lịch nhắc hoạt động:\n{string.Join("\n", lines)}",
                null,
                intent,
                aiCalled);
        }

        if (command.Kind == ZaloReminderCommandKind.Schedule && command.DelayMinutes is null && command.LocalTime is null)
        {
            return new BotAnswer(
                "Bạn cho mình biết khoảng thời gian nhé. Ví dụ: @bot cứ 6 tiếng nhắc nếu còn thiếu slot; @bot cứ 30 phút nhắc T6 nếu còn thiếu; hoặc @bot nhắc ngay.",
                null,
                intent,
                aiCalled);
        }

        foreach (var target in targets)
        {
            var denial = await GetOperatorDenialAsync(target, incoming.SenderId, intent, aiCalled);
            if (denial is not null) return denial;
        }

        if (command.IncludePaymentQr)
        {
            var missingQr = targets.Where(target => string.IsNullOrWhiteSpace(target.PaymentQrImageUrl)).ToList();
            if (missingQr.Count > 0)
            {
                return new BotAnswer(
                    $"Chưa thể lên lịch gửi QR vì admin chưa cấu hình ảnh thanh toán cho: {string.Join(", ", missingQr.Select(target => target.Name))}.",
                    null,
                    intent,
                    aiCalled);
            }
        }

        if (!confirmed &&
            configuration.GetValue("ZaloBot:MultiReminderEnabled", true) &&
            command.Kind is ZaloReminderCommandKind.Schedule or ZaloReminderCommandKind.Update or ZaloReminderCommandKind.Disable)
        {
            await SaveReminderConfirmationAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                targets,
                command,
                cancellationToken);
            return new BotAnswer(
                BuildReminderConfirmation(command, targets, now),
                null,
                intent,
                aiCalled);
        }

        if (configuration.GetValue("ZaloBot:MultiReminderEnabled", true))
        {
            var reminderBefore = new Dictionary<string, BotSessionStateCapture>(StringComparer.Ordinal);
            foreach (var target in targets)
                reminderBefore[target.Id] = await actionHistory.CaptureAsync(target.Id, cancellationToken);
            var answer = await ApplyNaturalReminderCommandAsync(
                command,
                targets,
                incoming,
                intent,
                aiCalled,
                cancellationToken);
            foreach (var target in targets)
            {
                await actionHistory.RecordAsync(target.Id, incoming.SenderId, incoming.SenderName,
                    $"Reminder{command.Kind}", $"{FormatReminderAction(command.Kind)} cho {target.Name}",
                    reminderBefore[target.Id], cancellationToken);
            }
            return answer;
        }

        var legacyReminderBefore = new Dictionary<string, BotSessionStateCapture>(StringComparer.Ordinal);
        foreach (var target in targets)
            legacyReminderBefore[target.Id] = await actionHistory.CaptureAsync(target.Id, cancellationToken);
        var targetIds = targets.Select(target => target.Id).ToList();
        var trackedSessions = await db.MatchSessions
            .Where(session => targetIds.Contains(session.Id))
            .ToListAsync(cancellationToken);
        var scheduleWasMovedForward = false;
        foreach (var session in trackedSessions)
        {
            switch (command.Kind)
            {
                case ZaloReminderCommandKind.Disable:
                    session.ReminderEnabled = false;
                    session.NextReminderAt = null;
                    break;
                case ZaloReminderCommandKind.TriggerNow:
                    var wasEnabled = session.ReminderEnabled;
                    session.ReminderEnabled = true;
                    if (!wasEnabled) session.ReminderRepeats = false;
                    session.NextReminderAt = now;
                    break;
                default:
                    var intervalMinutes = command.DelayMinutes!.Value;
                    session.ReminderEnabled = true;
                    session.ReminderRepeats = command.Repeats;
                    session.ReminderIntervalMinutes = intervalMinutes;
                    session.ReminderIntervalHours = Math.Clamp((int)Math.Ceiling(intervalMinutes / 60d), 1, 168);
                    var requestedAt = now.AddMinutes(intervalMinutes);
                    if (session.StartTime is not null && requestedAt >= session.StartTime)
                    {
                        session.NextReminderAt = now;
                        scheduleWasMovedForward = true;
                    }
                    else
                    {
                        session.NextReminderAt = requestedAt;
                    }
                    break;
            }
            session.ReminderLeaseToken = null;
            session.ReminderLeaseUntil = null;
            session.ReminderFailureCount = 0;
            session.LastReminderError = null;
            session.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        schedulerTrigger.TryTrigger();
        foreach (var target in targets)
        {
            await actionHistory.RecordAsync(target.Id, incoming.SenderId, incoming.SenderName,
                $"Reminder{command.Kind}", $"{FormatReminderAction(command.Kind)} cho {target.Name}",
                legacyReminderBefore[target.Id], cancellationToken);
        }

        var targetNames = targets.Count == 1 ? targets[0].Name : $"{targets.Count} trận sắp tới";
        var scopeNote = hasExplicitSelector
            ? $" Lệnh này chỉ áp dụng cho {targetNames}; lịch của các trận khác vẫn giữ nguyên nếu đã được bật riêng."
            : " Khi nhiều lịch cùng đến hạn, bot ưu tiên trận gần nhất còn thiếu slot.";
        return command.Kind switch
        {
            ZaloReminderCommandKind.Disable => new BotAnswer(
                $"Đã tắt lịch nhắc cho {targetNames}.", null, intent, aiCalled),
            ZaloReminderCommandKind.TriggerNow => new BotAnswer(
                $"Đã xếp một lượt nhắc ngay cho {targetNames}." + scopeNote + " Trận đủ slot sẽ không bị tag.",
                null,
                intent,
                aiCalled),
            _ => new BotAnswer(
                $"Đã lên lịch cho {targetNames}: lần đầu sau {FormatDuration(command.DelayMinutes!.Value)}" +
                (command.Repeats ? $", sau đó lặp mỗi {FormatDuration(command.DelayMinutes.Value)}." : ", chỉ nhắc một lần.") +
                (scheduleWasMovedForward ? " Có trận diễn ra trước mốc chờ nên bot sẽ kiểm tra trận đó ngay." : string.Empty) +
                scopeNote + " Trận đủ slot sẽ bỏ qua và tự xét lại nếu có người rút vote.",
                null,
                intent,
                aiCalled)
        };
    }

    private async Task<BotAnswer> ApplyNaturalReminderCommandAsync(
        ZaloReminderCommand command,
        IReadOnlyList<SessionSnapshot> targets,
        ZaloIncomingMessageEvent incoming,
        ZaloBotIntent intent,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var targetIds = targets.Select(target => target.Id).ToList();
        if (command.Kind == ZaloReminderCommandKind.Disable)
        {
            var legacyEnabled = await db.MatchSessions
                .CountAsync(session => targetIds.Contains(session.Id) && session.ReminderEnabled, cancellationToken);
            var disabled = await db.ZaloReminderSchedules
                .Where(schedule => targetIds.Contains(schedule.SessionId) && schedule.Enabled)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(schedule => schedule.Enabled, false)
                    .SetProperty(schedule => schedule.LeaseToken, (string?)null)
                    .SetProperty(schedule => schedule.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(schedule => schedule.UpdatedAt, now), cancellationToken);
            await db.MatchSessions
                .Where(session => targetIds.Contains(session.Id))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(session => session.ReminderEnabled, false)
                    .SetProperty(session => session.NextReminderAt, (DateTimeOffset?)null), cancellationToken);
            var totalDisabled = disabled + legacyEnabled;
            return new BotAnswer(
                totalDisabled > 0
                    ? $"Mình đã tắt {totalDisabled} lịch nhắc đang hoạt động. Phạm vi đã kiểm tra: {string.Join(", ", targets.Select(item => item.Name))}."
                    : $"Không có lịch nhắc nào đang hoạt động trong phạm vi: {string.Join(", ", targets.Select(item => item.Name))}.",
                null,
                intent,
                aiCalled);
        }

        if (command.Kind == ZaloReminderCommandKind.Update)
        {
            var question = ZaloBotIntelligence.Normalize(ExtractQuestion(incoming));
            var mentionsRoster = Regex.IsMatch(
                question,
                @"(?:chi\s+nhac|thay\s+vi.*nhac|doi.*(?:thanh|sang)).*(?:nguoi\s+(?:(?:da|tham\s+gia)\s+)?vote|nguoi\s+trong\s+(?:team|doi|danh\s+sach)|nguoi\s+co\s+ten\s+trong\s+danh\s+sach)",
                RegexOptions.CultureInvariant);
            var mentionsAll = Regex.IsMatch(
                question,
                @"(?:chi\s+nhac|doi.*(?:thanh|sang)|chuyen.*(?:thanh|sang)).*(?:ca\s+nhom|moi\s+nguoi|@all)",
                RegexOptions.CultureInvariant);
            var changesAudience = mentionsRoster || mentionsAll;
            var changesMissingCondition = Regex.IsMatch(
                question,
                @"(?:chi\s+nhac|chi\s+gui|bo\s+qua).*(?:thieu\s+(?:nguoi|slot)|chua\s+du|du\s+slot)",
                RegexOptions.CultureInvariant);
            var removesMissingCondition = Regex.IsMatch(
                question,
                @"(?:du\s+hay\s+thieu|khong\s+can\s+thieu|luon\s+nhac|van\s+nhac\s+khi\s+du)",
                RegexOptions.CultureInvariant);
            var changesStopWhenFull = Regex.IsMatch(
                question,
                @"du\s+(?:vote|slot|nguoi).*(?:thoi|dung|ngung)",
                RegexOptions.CultureInvariant);
            var changesMessage = !string.IsNullOrWhiteSpace(command.CustomMessage) && Regex.IsMatch(
                question,
                @"(?:doi|thay|sua|cap\s+nhat)\s+(?:lai\s+)?(?:noi\s+dung|tin\s+nhan)|noi\s+dung\s+(?:thanh|la)",
                RegexOptions.CultureInvariant);

            var activeSchedules = await db.ZaloReminderSchedules
                .Where(schedule => targetIds.Contains(schedule.SessionId) && schedule.Enabled)
                .OrderBy(schedule => schedule.CreatedAt)
                .ToListAsync(cancellationToken);
            var updated = new List<ZaloReminderSchedule>();
            var consolidated = 0;
            var ambiguousTargets = new List<string>();
            foreach (var target in targets)
            {
                var candidates = activeSchedules.Where(schedule => schedule.SessionId == target.Id).ToList();
                var hasScheduleSelector = command.LocalTime is not null || command.ExplicitLocalDate is not null;
                var selectorMatches = candidates.AsEnumerable();
                if (command.LocalTime is not null)
                {
                    selectorMatches = selectorMatches.Where(schedule =>
                        TimeOnly.FromDateTime(schedule.NextRunAt.ToOffset(VietnamOffset).DateTime) == command.LocalTime);
                }
                if (command.ExplicitLocalDate is not null)
                {
                    selectorMatches = selectorMatches.Where(schedule =>
                        DateOnly.FromDateTime(schedule.NextRunAt.ToOffset(VietnamOffset).DateTime) == command.ExplicitLocalDate);
                }
                var matchedCandidates = hasScheduleSelector ? selectorMatches.ToList() : [];
                if (matchedCandidates.Count > 0) candidates = matchedCandidates;
                if (candidates.Count == 0) continue;
                if (matchedCandidates.Count == 0 && candidates.Select(item => item.NextRunAt).Distinct().Count() > 1)
                {
                    ambiguousTargets.Add(target.Name);
                    continue;
                }

                var primary = candidates[0];
                if (changesAudience)
                    primary.Audience = mentionsRoster ? ZaloReminderAudience.Roster : ZaloReminderAudience.All;
                if (changesMissingCondition)
                    primary.OnlyIfMissingSlots = !removesMissingCondition;
                if (changesStopWhenFull)
                    primary.StopWhenFull = command.StopWhenFull;
                if (changesMessage)
                    primary.Message = Clean(command.CustomMessage, 2000);
                if (command.IncludePaymentQr)
                {
                    primary.IncludePaymentQr = true;
                    primary.AllowAfterSessionStart = true;
                    primary.OnlyIfMissingSlots = false;
                    primary.StopWhenFull = false;
                }
                if (command.DelayMinutes is >= 5)
                {
                    primary.Repeats = command.Repeats;
                    primary.IntervalMinutes = command.Repeats ? command.DelayMinutes : null;
                    primary.NextRunAt = now.AddMinutes(command.DelayMinutes.Value);
                }
                else if (command.LocalTime is not null || command.ExplicitLocalDate is not null)
                {
                    var changedDueAt = ComputeReminderDueAt(command, target, now);
                    if (changedDueAt is not null &&
                        (target.StartTime is null || changedDueAt < target.StartTime || primary.AllowAfterSessionStart))
                        primary.NextRunAt = changedDueAt.Value;
                }
                primary.LeaseToken = null;
                primary.LeaseUntil = null;
                primary.UpdatedAt = now;
                updated.Add(primary);

                foreach (var duplicate in candidates.Skip(1).Where(item => item.NextRunAt == primary.NextRunAt))
                {
                    duplicate.Enabled = false;
                    duplicate.LeaseToken = null;
                    duplicate.LeaseUntil = null;
                    duplicate.UpdatedAt = now;
                    consolidated += 1;
                }
            }

            if (updated.Count == 0)
            {
                if (ambiguousTargets.Count > 0)
                {
                    return new BotAnswer(
                        $"Có nhiều lịch nhắc khác giờ cho {string.Join(", ", ambiguousTargets)}. Bạn hãy xem danh sách lịch nhắc rồi nói rõ lịch cũ muốn đổi, ví dụ: “đổi lịch 17:00 của T4 thành 18:00”.",
                        null,
                        intent,
                        aiCalled);
                }
                return new BotAnswer(
                    $"Mình chưa tìm thấy lịch nhắc đang hoạt động khớp với yêu cầu trong: {string.Join(", ", targets.Select(item => item.Name))}. Bạn có thể hỏi “xem danh sách lịch nhắc” để kiểm tra trước.",
                    null,
                    intent,
                    aiCalled);
            }
            await db.SaveChangesAsync(cancellationToken);
            schedulerTrigger.TryTrigger();

            var sessionNames = targets.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
            var updateLines = updated.Select(schedule =>
            {
                var recipients = schedule.Audience == ZaloReminderAudience.Roster
                    ? "những người có tên trong danh sách (vote và share slot)"
                    : "cả nhóm (@all)";
                var frequency = schedule.Repeats && schedule.IntervalMinutes is not null
                    ? $"lặp mỗi {FormatDuration(schedule.IntervalMinutes.Value)}"
                    : "chỉ gửi một lần";
                var content = FormatReminderContentForDisplay(schedule.Message);
                return $"- {sessionNames.GetValueOrDefault(schedule.SessionId, schedule.SessionId)}: {FormatVietnamTime(schedule.NextRunAt)}, gửi cho {recipients}, {frequency}.\n   {content}";
            });
            var consolidationNote = consolidated > 0
                ? $"\nMình cũng đã gộp {consolidated} lịch trùng cùng thời điểm."
                : string.Empty;
            return new BotAnswer(
                $"Mình đã cập nhật {updated.Count} lịch nhắc:\n{string.Join("\n", updateLines)}{consolidationNote}",
                null,
                intent,
                aiCalled);
        }

        var created = new List<(SessionSnapshot Session, DateTimeOffset DueAt)>();
        var skipped = new List<string>();
        foreach (var target in targets)
        {
            var dueAt = command.Kind == ZaloReminderCommandKind.TriggerNow
                ? now
                : ComputeReminderDueAt(command, target, now);
            if (dueAt is null)
            {
                skipped.Add($"{target.Name} (không xác định được thời gian)");
                continue;
            }
            if (target.StartTime is not null && dueAt >= target.StartTime &&
                command.Kind != ZaloReminderCommandKind.TriggerNow &&
                !command.AllowAfterSessionStart)
            {
                skipped.Add($"{target.Name} (giờ nhắc không còn trước giờ trận)");
                continue;
            }

            var message = Clean(command.CustomMessage, 2000);
            var duplicate = await db.ZaloReminderSchedules.AsNoTracking().AnyAsync(schedule =>
                schedule.SessionId == target.Id &&
                schedule.Enabled &&
                schedule.NextRunAt == dueAt &&
                schedule.Message == message &&
                schedule.Audience == command.Audience &&
                schedule.OnlyIfMissingSlots == command.OnlyIfMissingSlots &&
                schedule.StopWhenFull == command.StopWhenFull &&
                schedule.AllowAfterSessionStart == command.AllowAfterSessionStart &&
                schedule.IncludePaymentQr == command.IncludePaymentQr,
                cancellationToken);
            if (!duplicate)
            {
                db.ZaloReminderSchedules.Add(new ZaloReminderSchedule
                {
                    SessionId = target.Id,
                    CreatedBySenderId = NormalizeId(incoming.SenderId),
                    CreatedBySenderName = Clean(incoming.SenderName, 160) ?? "Thành viên Zalo",
                    Message = message,
                    Audience = command.Audience,
                    OnlyIfMissingSlots = command.OnlyIfMissingSlots,
                    StopWhenFull = command.StopWhenFull,
                    AllowAfterSessionStart = command.AllowAfterSessionStart,
                    IncludePaymentQr = command.IncludePaymentQr,
                    Repeats = command.Repeats && command.DelayMinutes is >= 5,
                    IntervalMinutes = command.Repeats ? command.DelayMinutes : null,
                    NextRunAt = dueAt.Value,
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            created.Add((target, dueAt.Value));
        }
        await db.SaveChangesAsync(cancellationToken);
        schedulerTrigger.TryTrigger();

        if (created.Count == 0)
        {
            return new BotAnswer(
                "Chưa tạo được lịch nhắc. " + string.Join("; ", skipped),
                null,
                intent,
                aiCalled);
        }
        var recipients = command.Audience == ZaloReminderAudience.Roster
            ? "những người có tên trong danh sách (gồm người vote và share slot)"
            : "cả nhóm (@all)";
        var timing = created.Count == 1
            ? $"Mình đã hẹn nhắc {recipients} của {created[0].Session.Name} vào {FormatVietnamTime(created[0].DueAt)}."
            : $"Mình đã tạo {created.Count} lịch nhắc cho {recipients}:\n" +
              string.Join("\n", created.Select(item => $"- {item.Session.Name}: {FormatVietnamTime(item.DueAt)}"));
        var content = string.IsNullOrWhiteSpace(command.CustomMessage)
            ? "Nội dung sẽ được bot soạn theo số slot thực tế tại thời điểm gửi."
            : $"Nội dung nhắc: {command.CustomMessage.Trim()}";
        var attachment = command.IncludePaymentQr
            ? " Bot sẽ gửi kèm ảnh QR thanh toán đã cấu hình của trận."
            : string.Empty;
        var frequency = command.Repeats && command.DelayMinutes is not null
            ? $"Sau lần đầu, lịch sẽ lặp lại mỗi {FormatDuration(command.DelayMinutes.Value)}."
            : created.Count == 1
                ? "Lịch này chỉ gửi một lần."
                : "Mỗi lịch chỉ gửi một lần.";
        var condition = command.OnlyIfMissingSlots
            ? command.StopWhenFull
                ? " Khi trận đủ slot, lịch sẽ tự dừng. Lịch cũng tự tắt khi đã tới giờ trận."
                : " Nếu lúc đó trận đã đủ slot, bot sẽ bỏ qua lượt nhắc."
            : string.Empty;
        var skippedText = skipped.Count == 0 ? string.Empty : $"\nKhông tạo được cho: {string.Join("; ", skipped)}";
        return new BotAnswer(
            $"{timing}\n{content}{attachment}\n{frequency}{condition}{skippedText}",
            null,
            intent,
            aiCalled);
    }

    private static DateTimeOffset? ComputeReminderDueAt(
        ZaloReminderCommand command,
        SessionSnapshot target,
        DateTimeOffset utcNow)
    {
        if (command.DelayMinutes is >= 0) return utcNow.AddMinutes(command.DelayMinutes.Value);
        if (command.LocalTime is null) return null;
        var localNow = utcNow.ToOffset(VietnamOffset);
        DateOnly date;
        if (command.ExplicitLocalDate is not null)
        {
            date = command.ExplicitLocalDate.Value;
        }
        else if (command.UseSessionDate && target.StartTime is not null)
        {
            date = DateOnly.FromDateTime(target.StartTime.Value.ToOffset(VietnamOffset).DateTime);
        }
        else
        {
            date = DateOnly.FromDateTime(localNow.Date);
            if (date.ToDateTime(command.LocalTime.Value) <= localNow.DateTime) date = date.AddDays(1);
        }
        var localDateTime = date.ToDateTime(command.LocalTime.Value, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, VietnamOffset).ToUniversalTime();
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes % 60 == 0) return $"{minutes / 60} giờ";
        if (minutes < 60) return $"{minutes} phút";
        return $"{minutes / 60} giờ {minutes % 60} phút";
    }

    private async Task<BotAnswer?> GetOperatorDenialAsync(
        SessionSnapshot session,
        string senderId,
        ZaloBotIntent intent,
        bool aiCalled)
    {
        if (session.OperatorZaloUserIds.Contains(NormalizeId(senderId))) return null;
        var groupRole = await zaloIntegration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            senderId);
        if (groupRole.IsSuccess && groupRole.Value?.CanOperateBot == true) return null;
        if (!groupRole.IsSuccess)
        {
            logger.LogWarning(
                "Could not verify Zalo group role Session={SessionId} Sender={SenderId}: {Error}",
                session.Id,
                NormalizeId(senderId),
                groupRole.Error);
            return new BotAnswer(
                "Mình chưa xác minh được quyền trưởng/phó nhóm từ Zalo lúc này. Bạn thử lại sau hoặc nhờ admin cấp UID trong phần Bot chat & reminder.",
                null,
                intent,
                aiCalled);
        }
        return new BotAnswer(
            "Lệnh này thay đổi dữ liệu nên chỉ trưởng nhóm, phó nhóm hoặc Zalo operator được admin cấp quyền mới dùng được.",
            null,
            intent,
            aiCalled);
    }

    private static string FormatTeamLineup(string sessionName, IReadOnlyList<TeamPreviewResponse> teams)
        => ZaloTeamLineupFormatter.Format(sessionName, teams).Text;

    private async Task<ZaloTeamLineupMessage> BuildTeamLineupMessageAsync(
        string sessionName,
        IReadOnlyList<TeamPreviewResponse> teams,
        bool mentionPlayers,
        CancellationToken cancellationToken)
    {
        if (!mentionPlayers) return ZaloTeamLineupFormatter.Format(sessionName, teams);

        var slotIds = teams
            .SelectMany(team => team.Slots)
            .Select(slot => slot.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (slotIds.Count == 0) return ZaloTeamLineupFormatter.Format(sessionName, teams);

        var links = await db.DraftSlotPlayers
            .AsNoTracking()
            .Where(link => slotIds.Contains(link.DraftSlotId))
            .OrderBy(link => link.DraftSlotId)
            .ThenBy(link => link.RotationOrder)
            .Select(link => new
            {
                link.DraftSlotId,
                link.SessionPlayer.DisplayName,
                ZaloUserId = link.SessionPlayer.PlayerProfile == null
                    ? null
                    : link.SessionPlayer.PlayerProfile.ZaloUserId
            })
            .ToListAsync(cancellationToken);
        var playersBySlot = links
            .GroupBy(link => link.DraftSlotId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZaloTeamMentionPlayer>)group
                    .Select(link => new ZaloTeamMentionPlayer(link.DisplayName, Clean(link.ZaloUserId, 100)))
                    .ToList(),
                StringComparer.Ordinal);
        return ZaloTeamLineupFormatter.Format(sessionName, teams, playersBySlot);
    }

    private async Task<bool> IsAiCallAllowedAsync(string connectionId, string groupId, string senderId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedSender = NormalizeId(senderId);
        var cooldown = Math.Clamp(configuration.GetValue("ZaloBot:AiCooldownSeconds", 10), 0, 300);
        var perMinuteUser = Math.Clamp(configuration.GetValue("ZaloBot:AiPerUserPerMinute", 4), 1, 60);
        var perMinuteGroup = Math.Clamp(configuration.GetValue("ZaloBot:AiPerGroupPerMinute", 20), 1, 300);
        var query = db.ZaloGroupMessages.AsNoTracking().Where(message =>
            message.ZaloConnectionId == connectionId && message.GroupId == groupId && message.AiCalled && message.ReceivedAt >= now.AddMinutes(-1));
        if (await query.CountAsync(cancellationToken) >= perMinuteGroup) return false;
        var userCalls = query.Where(message => message.SenderId == normalizedSender);
        if (await userCalls.CountAsync(cancellationToken) >= perMinuteUser) return false;
        return !await userCalls.AnyAsync(message => message.ReceivedAt >= now.AddSeconds(-cooldown), cancellationToken);
    }

    private async Task<BotAnswer> HandleWaitlistIntentAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        if (decision.Intent == ZaloBotIntent.WaitlistStatus &&
            IsStandaloneWaitlistExplanation(selector) &&
            !HasExplicitSessionSelector(sessions, selector))
        {
            var inviteMinutes = Math.Clamp(configuration.GetValue("ZaloBot:WaitlistInviteMinutes", 15), 5, 120);
            return new BotAnswer(
                $"Waitlist là danh sách chờ cho người muốn tham gia nhưng buổi đã đủ slot. Cách hoạt động:\n" +
                $"1. Bạn nói tự nhiên như `@bot cho tui vào waitlist T6`; bot xếp theo thứ tự đăng ký.\n" +
                "2. Khi có người rút vote hoặc slot trống, hệ thống cập nhật lại số slot rồi gọi người đứng đầu trước.\n" +
                $"3. Người được gọi sẽ được mention và giữ lời mời trong {inviteMinutes} phút; gõ `@bot nhận slot` để vào danh sách chính thức, hoặc `@bot nhường người sau` để bỏ qua.\n" +
                "4. Nếu từ chối hoặc hết thời gian, bot tự chuyển lời mời cho người kế tiếp. Người đã nhận slot sẽ được yêu cầu bổ sung hồ sơ còn thiếu trước khi draft.\n" +
                "Muốn xem trạng thái của một buổi, gõ `@bot xem waitlist T4`, `T6` hoặc tên trận.",
                null,
                decision.Intent,
                aiCalled);
        }
        SessionSnapshot? session = null;
        if (decision.Intent is ZaloBotIntent.WaitlistAccept or ZaloBotIntent.WaitlistDecline)
        {
            var sessionIds = sessions.Select(item => item.Id).ToList();
            var invitedSessionIds = await db.SessionWaitlistEntries.AsNoTracking()
                .Where(item => sessionIds.Contains(item.SessionId) &&
                               item.ZaloUserId == NormalizeId(incoming.SenderId) &&
                               item.Status == SessionWaitlistStatus.Invited &&
                               item.InviteExpiresAt != null && item.InviteExpiresAt > DateTimeOffset.UtcNow)
                .Select(item => item.SessionId)
                .ToListAsync(cancellationToken);
            if (invitedSessionIds.Count == 1)
                session = sessions.Single(item => item.Id == invitedSessionIds[0]);
            else if (invitedSessionIds.Count == 0)
                return new BotAnswer("Bạn chưa có lời mời nhận slot nào đang hoạt động. Có thể xem hàng chờ bằng cách hỏi @bot xem danh sách chờ.", null, decision.Intent, aiCalled);
            else
            {
                var invitedSessions = sessions.Where(item => invitedSessionIds.Contains(item.Id)).ToList();
                var selection = await SelectSessionAsync(invitedSessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
                if (selection.Clarification is not null) return new BotAnswer(selection.Clarification, null, decision.Intent, aiCalled);
                session = selection.Session;
            }
        }
        else
        {
            var selection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (selection.Clarification is not null) return new BotAnswer(selection.Clarification, null, decision.Intent, aiCalled);
            session = selection.Session;
        }
        if (session is null) return new BotAnswer("Mình chưa xác định được buổi cần xử lý waitlist.", null, decision.Intent, aiCalled);

        if (decision.Intent == ZaloBotIntent.WaitlistJoin)
        {
            var mentionedTarget = ExtractMentionedUsers(incoming).FirstOrDefault();
            var delegated = mentionedTarget is not null &&
                            NormalizeId(mentionedTarget.ZaloUserId) != NormalizeId(incoming.SenderId);
            if (delegated)
            {
                var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
                if (denial is not null) return denial;
            }
            var targetId = delegated ? mentionedTarget!.ZaloUserId : incoming.SenderId;
            var targetName = delegated ? mentionedTarget!.DisplayName : incoming.SenderName;
            var result = await waitlists.JoinAsync(session.Id, targetId, targetName,
                incoming.SenderId, incoming.SenderName, cancellationToken);
            return new BotAnswer(result.IsSuccess && result.Value is not null ? result.Value.Message : result.Error ?? "Không thể vào danh sách chờ.", null, decision.Intent, aiCalled);
        }
        if (decision.Intent == ZaloBotIntent.WaitlistLeave)
        {
            var result = await waitlists.LeaveAsync(session.Id, incoming.SenderId, incoming.SenderId,
                incoming.SenderName, cancellationToken);
            return new BotAnswer(result.IsSuccess && result.Value is not null ? result.Value.Message : result.Error ?? "Không thể rời danh sách chờ.", null, decision.Intent, aiCalled);
        }
        if (decision.Intent == ZaloBotIntent.WaitlistAccept)
        {
            var result = await waitlists.AcceptAsync(session.Id, incoming.SenderId, incoming.SenderName, cancellationToken);
            return new BotAnswer(result.IsSuccess && result.Value is not null ? result.Value.Message : result.Error ?? "Không thể nhận slot.", null, decision.Intent, aiCalled);
        }
        if (decision.Intent == ZaloBotIntent.WaitlistDecline)
        {
            var result = await waitlists.DeclineAsync(session.Id, incoming.SenderId, incoming.SenderName, cancellationToken);
            return new BotAnswer(result.IsSuccess && result.Value is not null ? result.Value.Message : result.Error ?? "Không thể nhường slot.", null, decision.Intent, aiCalled);
        }

        var entries = await waitlists.LoadResponsesAsync(session.Id, cancellationToken);
        if (entries.Count == 0)
            return new BotAnswer($"{session.Name} hiện chưa có ai trong danh sách chờ.", null, decision.Intent, aiCalled);
        var lines = entries.Select(entry => entry.Status switch
        {
            SessionWaitlistStatus.Invited => $"{entry.Position}. {entry.DisplayName} — đang được giữ slot tới {FormatVietnamTime(entry.InviteExpiresAt!.Value)}",
            SessionWaitlistStatus.Accepted => $"✓ {entry.DisplayName} — đã nhận slot",
            _ => $"{entry.Position}. {entry.DisplayName} — đang chờ"
        });
        var own = entries.FirstOrDefault(entry => NormalizeId(entry.ZaloUserId) == NormalizeId(incoming.SenderId));
        var ownLine = own is null ? string.Empty : own.Status switch
        {
            SessionWaitlistStatus.Accepted => "\nBạn đã nhận slot và có tên trong danh sách chính thức.",
            SessionWaitlistStatus.Invited => $"\nTới lượt bạn rồi; gõ @bot nhận slot trước {FormatVietnamTime(own.InviteExpiresAt!.Value)}.",
            _ => $"\nBạn đang ở vị trí chờ số {own.Position}."
        };
        return new BotAnswer($"Danh sách chờ {session.Name}:\n{string.Join("\n", lines)}{ownLine}", null, decision.Intent, aiCalled);
    }

    private static bool IsStandaloneWaitlistExplanation(string selector)
    {
        if (!HasAny(selector, "waitlist", "danh sach cho")) return false;
        if (!HasAny(selector, "giai thich", "cach hoat dong", "hoat dong nhu nao", "chi tiet", "la gi", "huong dan")) return false;
        return !HasAny(selector, "hien tai", "co danh sach", "ai dang", "xem", "trang thai", "du 18", "slot trong", "khi co slot");
    }

    private async Task<BotAnswer> HandleSlotTransferAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled,
        bool confirmed = false,
        ZaloSlotTransferCommand? confirmedCommand = null)
    {
        var mentionedUsers = ExtractMentionedUsers(incoming);
        ZaloSlotTransferCommand? command = confirmedCommand;
        if (command is null && ZaloNaturalCommandParser.TryParseSlotTransfer(ExtractQuestion(incoming), out var parsed))
            command = parsed;
        if (command is null && aiCalled && ai.IsConfigured)
        {
            var recentMessages = await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(message => message.ZaloConnectionId == connectionId && message.GroupId == groupId)
                .OrderByDescending(message => message.SentAt)
                .Take(20)
                .OrderBy(message => message.SentAt)
                .Select(message => new ZaloAiMessage(
                    message.IsFromBot ? "assistant" : "user",
                    message.SenderId,
                    message.SenderName,
                    message.Content,
                    message.SentAt))
                .ToListAsync(cancellationToken);
            var extracted = await ai.ParseSlotTransferCommandAsync(
                new ZaloNaturalSlotTransferContext(
                    ExtractQuestion(incoming),
                    incoming.SenderName,
                    mentionedUsers,
                    sessions.Take(10)
                        .Select(session => new ZaloAiSessionReference(session.Id, session.Name, session.StartTime))
                        .ToList(),
                    recentMessages),
                cancellationToken);
            if (extracted is not null)
            {
                command = extracted;
                aiCalled = true;
            }
        }
        command = ZaloNaturalCommandParser.BindExplicitSlotTransferMentions(mentionedUsers, command) ?? command;
        if (command is null)
        {
            return new BotAnswer(
                "Mình chưa xác định được người nhường và người nhận slot. Bạn có thể nói: @bot @NgườiA muốn rút nhường slot cho @NgườiB, kèm thứ hoặc tên trận.",
                null,
                decision.Intent,
                aiCalled);
        }

        var sessionSelector = NormalizeText(string.Join(' ', new[]
        {
            selector,
            command.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var selected = await SelectSessionAsync(
            sessions,
            sessionSelector,
            connectionId,
            groupId,
            incoming.SenderId,
            decision.Intent,
            cancellationToken);
        if (selected.Clarification is not null)
        {
            await SaveSlotTransferSelectionAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                sessions.Where(session => IsUpcoming(session)).Take(8).ToList() is { Count: > 0 } candidates
                    ? candidates
                    : sessions.Take(8).ToList(),
                command,
                cancellationToken);
            return new BotAnswer(selected.Clarification, null, decision.Intent, aiCalled);
        }
        var session = selected.Session;
        if (session is null)
            return new BotAnswer("Mình chưa xác định được trận cần chuyển slot.", null, decision.Intent, aiCalled);

        var fromId = command.FromZaloUserId ?? FindMentionedUser(command.FromPlayer, mentionedUsers)?.ZaloUserId;
        var toId = command.ToZaloUserId ?? FindMentionedUser(command.ToPlayer, mentionedUsers)?.ZaloUserId;
        var senderIsFrom = (fromId is not null && NormalizeId(fromId) == NormalizeId(incoming.SenderId)) ||
                           NormalizeText(command.FromPlayer) == NormalizeText(incoming.SenderName);
        if (!senderIsFrom)
        {
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
            if (denial is not null) return denial;
        }

        var preview = await draftService.PreviewPostDraftSlotTransferAsync(
            session.AdminUserId,
            session.Id,
            command.FromPlayer,
            new ShareSlotParticipantInput(command.ToPlayer, toId),
            cancellationToken);
        if (!preview.IsSuccess || preview.Value is null)
            return new BotAnswer(preview.Error ?? "Không thể kiểm tra yêu cầu chuyển slot.", null, decision.Intent, aiCalled);

        if (!confirmed)
        {
            await SaveSlotTransferConfirmationAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                session.Id,
                command,
                cancellationToken);
            var previewCaptainNote = preview.Value.CaptainTransferred
                ? $" {preview.Value.ToPlayerName} cũng sẽ tiếp quản vai trò đội trưởng của {preview.Value.TeamName}."
                : string.Empty;
            return new BotAnswer(
                $"Mình hiểu là {preview.Value.FromPlayerName} muốn rút slot của {session.Name} và chuyển cho {preview.Value.ToPlayerName} tại {preview.Value.TeamName}.{previewCaptainNote} Đội hình sẽ được cập nhật tại chỗ, không draft lại. Gõ @bot xác nhận để thực hiện hoặc @bot huỷ.",
                null,
                decision.Intent,
                aiCalled,
                ProtectedTerms:
                [
                    preview.Value.FromPlayerName,
                    preview.Value.ToPlayerName,
                    preview.Value.TeamName,
                    session.Name,
                    "@bot xác nhận",
                    "@bot huỷ"
                ]);
        }

        var before = await actionHistory.CaptureAsync(session.Id, cancellationToken);
        var transferMembers = await ResolveZaloMembersAsync(
            session,
            toId is null ? [] : [toId],
            cancellationToken);
        transferMembers.TryGetValue(NormalizeId(toId ?? string.Empty), out var replacementMember);
        var result = await draftService.TransferPostDraftSlotAsync(
            session.AdminUserId,
            session.Id,
            command.FromPlayer,
            new ShareSlotParticipantInput(command.ToPlayer, toId, replacementMember?.AvatarUrl));
        if (!result.IsSuccess || result.Value is null)
            return new BotAnswer(result.Error ?? "Không thể chuyển slot.", null, decision.Intent, aiCalled);
        await actionHistory.RecordAsync(
            session.Id,
            incoming.SenderId,
            incoming.SenderName,
            "SlotTransfer",
            $"Chuyển slot từ {result.Value.FromPlayerName} cho {result.Value.ToPlayerName} trong {session.Name}",
            before,
            cancellationToken);
        var profileNote = result.Value.NeedsProfileUpdate
            ? " Cần cập nhật hồ sơ của người nhận trước khi có thao tác draft lại."
            : string.Empty;
        var captainNote = result.Value.CaptainTransferred
            ? $" {result.Value.ToPlayerName} hiện là đội trưởng mới của {result.Value.TeamName}."
            : string.Empty;
        return new BotAnswer(
            $"Đã cập nhật {session.Name}: {result.Value.FromPlayerName} rút slot, {result.Value.ToPlayerName} nhận slot tại {result.Value.TeamName}.{captainNote} Không draft lại toàn bộ đội hình.{profileNote}",
            null,
            ZaloBotIntent.SlotTransferConfirm,
            aiCalled,
            ProtectedTerms:
            [
                result.Value.FromPlayerName,
                result.Value.ToPlayerName,
                result.Value.TeamName,
                session.Name
            ]);
    }

    private async Task<BotAnswer> HandleRepairShareSlotAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled,
        bool confirmed = false,
        ZaloRepairShareSlotCommand? confirmedCommand = null)
    {
        var command = confirmedCommand;
        if (command is null && !ZaloNaturalCommandParser.TryParseRepairShareSlot(ExtractQuestion(incoming), out command))
        {
            return new BotAnswer(
                "Mình cần biết người đang share sai, người share và người chính mới. Ví dụ: `@bot sửa share slot của Vivian từ Thanh Long sang Vinh cho T4`.",
                null,
                decision.Intent,
                aiCalled);
        }

        var commandSelector = NormalizeText(string.Join(' ', new[]
        {
            selector,
            command.SessionReference,
            decision.SessionReference
        }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var selected = SelectSession(sessions, commandSelector);
        if (selected.Session is null)
        {
            var candidates = sessions.Where(IsUpcoming).Take(8).ToList();
            if (candidates.Count == 0) candidates = sessions.Take(8).ToList();
            await SaveRepairShareSlotSelectionAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                candidates,
                command,
                cancellationToken);
            return new BotAnswer(
                $"Mình cần biết trận nào để sửa. Bạn trả lời bằng thứ, ngày hoặc tên trận: {string.Join(", ", candidates.Take(4).Select(FormatSessionChoice))}.",
                null,
                decision.Intent,
                aiCalled);
        }

        var session = selected.Session;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        if (!confirmed)
        {
            await SaveRepairShareSlotConfirmationAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                session.Id,
                command,
                cancellationToken);
            return new BotAnswer(
                $"Mình hiểu bạn muốn sửa đội hình {session.Name}: tháo {command.Partner} khỏi share slot của {command.WrongAnchor}, rồi ghép {command.Partner} vào slot của {command.CorrectAnchor}. Chưa có dữ liệu nào thay đổi. Gõ `@bot xác nhận` để thực hiện hoặc `@bot huỷ`.",
                null,
                decision.Intent,
                aiCalled);
        }

        var before = await actionHistory.CaptureAsync(session.Id, cancellationToken);
        var repaired = await draftService.RepairPostDraftSharedSlotAsync(
            session.AdminUserId,
            session.Id,
            command.WrongAnchor,
            command.Partner,
            command.CorrectAnchor);
        if (!repaired.IsSuccess || repaired.Value is null)
            return new BotAnswer(repaired.Error ?? "Không thể sửa share slot sau draft.", null, decision.Intent, aiCalled);

        await actionHistory.RecordAsync(
            session.Id,
            incoming.SenderId,
            incoming.SenderName,
            "RepairShareSlot",
            $"Sửa share slot {command.Partner}: {command.WrongAnchor} → {command.CorrectAnchor} trong {session.Name}",
            before,
            cancellationToken);
        return new BotAnswer(
            $"Đã sửa đội hình {session.Name}: {repaired.Value.PartnerPlayerName} đã được chuyển từ slot của {repaired.Value.FromAnchorPlayerName} ({repaired.Value.FromTeamName}) sang slot của {repaired.Value.ToAnchorPlayerName} ({repaired.Value.ToTeamName}). Đã tính lại điểm hai team.",
            null,
            decision.Intent,
            aiCalled);
    }

    private async Task<BotAnswer> HandleActionHistoryIntentAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled)
    {
        var selection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
        if (selection.Clarification is not null) return new BotAnswer(selection.Clarification, null, decision.Intent, aiCalled);
        var session = selection.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;
        if (decision.Intent == ZaloBotIntent.ActionHistory)
        {
            var result = await actionHistory.GetHistoryAsync(session.AdminUserId, session.Id, 8, cancellationToken);
            if (!result.IsSuccess || result.Value is null) return new BotAnswer(result.Error ?? "Không đọc được lịch sử thao tác.", null, decision.Intent, aiCalled);
            if (result.Value.Count == 0) return new BotAnswer($"{session.Name} chưa có thao tác bot nào được ghi vào lịch sử.", null, decision.Intent, aiCalled);
            var lines = result.Value.Select((item, index) =>
                $"{index + 1}. {item.Summary} — {FormatVietnamTime(item.CreatedAt)}" +
                (item.UndoneAt is null ? item.IsUndoable ? " — có thể hoàn tác" : " — chỉ xem" : " — đã hoàn tác"));
            return new BotAnswer($"Các thay đổi backend gần đây của {session.Name}:\n{string.Join("\n", lines)}",
                null, decision.Intent, aiCalled);
        }

        var action = await actionHistory.GetLatestUndoableAsync(session.Id, cancellationToken);
        if (action is null)
            return new BotAnswer($"{session.Name} không có thao tác backend nào đang an toàn để hoàn tác.", null, decision.Intent, aiCalled);
        await SaveUndoConfirmationAsync(connectionId, groupId, incoming.SenderId, session.Id, action.Id, cancellationToken);
        return new BotAnswer(
            $"Mình sẽ khôi phục dữ liệu backend trước thao tác “{action.Summary}” ({FormatVietnamTime(action.CreatedAt)}). " +
            "Việc này không thu hồi tin Zalo. Gõ @bot xác nhận để hoàn tác hoặc @bot huỷ; nếu dữ liệu đã bị thay đổi tiếp, bot sẽ từ chối để tránh ghi đè.",
            null, decision.Intent, aiCalled);
    }

    private async Task<BotAnswer> ExecuteClassifiedIntentAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var selector = NormalizeText(string.Join(' ', new[] { normalizedQuestion, decision.SessionReference }.Where(value => !string.IsNullOrWhiteSpace(value))));
        if (decision.Intent is ZaloBotIntent.SlotTransfer or ZaloBotIntent.SlotTransferConfirm)
            return await HandleSlotTransferAsync(decision, sessions, selector, connectionId, groupId, incoming, cancellationToken, true);
        if (decision.Intent is ZaloBotIntent.WaitlistJoin or ZaloBotIntent.WaitlistLeave or
            ZaloBotIntent.WaitlistStatus or ZaloBotIntent.WaitlistAccept or ZaloBotIntent.WaitlistDecline)
            return await HandleWaitlistIntentAsync(decision, sessions, selector, connectionId, groupId, incoming, cancellationToken, true);
        if (decision.Intent == ZaloBotIntent.RepairShareSlot)
            return await HandleRepairShareSlotAsync(decision, sessions, selector, connectionId, groupId, incoming, cancellationToken, true);
        if (decision.Intent is ZaloBotIntent.ActionHistory or ZaloBotIntent.UndoAction)
            return await HandleActionHistoryIntentAsync(decision, sessions, selector, connectionId, groupId, incoming, cancellationToken, true);
        if (decision.Intent is ZaloBotIntent.ScheduleReminder or ZaloBotIntent.ReminderStatus or ZaloBotIntent.CancelReminder)
        {
            ZaloReminderCommand command;
            if (!ZaloBotIntelligence.TryParseReminderCommand(ExtractQuestion(incoming), out command))
            {
                command = decision.Intent switch
                {
                    ZaloBotIntent.ReminderStatus => new ZaloReminderCommand(ZaloReminderCommandKind.Status, null, true),
                    ZaloBotIntent.CancelReminder => new ZaloReminderCommand(ZaloReminderCommandKind.Disable, null, false),
                    _ => new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, true)
                };
            }
            return await HandleReminderCommandAsync(
                command,
                sessions,
                selector,
                connectionId,
                groupId,
                incoming,
                cancellationToken,
                true);
        }
        if (decision.Intent is ZaloBotIntent.TeamLineup or ZaloBotIntent.TeamImage)
        {
            var teamSelection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (teamSelection.Clarification is not null) return new BotAnswer(teamSelection.Clarification, null, decision.Intent, true);
            var teamSession = teamSelection.Session!;
            var state = await draftService.GetDraftStateAsync(teamSession.AdminUserId, teamSession.Id);
            if (!state.IsSuccess || state.Value is null) return new BotAnswer(state.Error ?? "Không đọc được đội hình.", null, decision.Intent, true);
            var lineup = await BuildTeamLineupMessageAsync(
                teamSession.Name,
                state.Value.TeamPreview,
                ZaloTeamLineupFormatter.WantsPlayerMentions(ExtractQuestion(incoming)),
                cancellationToken);
            return new BotAnswer(
                lineup.Text,
                decision.Intent == ZaloBotIntent.TeamImage ? teamCards.GetPublicUrl(teamSession.Id) : null,
                decision.Intent,
                true,
                Mentions: lineup.Mentions);
        }
        if (decision.Intent == ZaloBotIntent.UpdatePlayerProfile)
            return await UpdatePlayerProfileAsync(decision, sessions, selector, ExtractQuestion(incoming), incoming, true);
        if (decision.Intent == ZaloBotIntent.AddGuestPlayer)
            return await AddGuestPlayerAsync(
                decision,
                sessions,
                selector,
                ExtractQuestion(incoming),
                connectionId,
                groupId,
                incoming,
                cancellationToken,
                true);
        if (decision.Intent == ZaloBotIntent.TeamPreference)
            return await HandleTeamPreferenceAsync(
                decision,
                sessions,
                selector,
                ExtractQuestion(incoming),
                incoming,
                cancellationToken,
                true);
        if (decision.Intent == ZaloBotIntent.ShareSlot)
            return await ShareSlotAsync(decision, sessions, selector, ExtractQuestion(incoming), incoming, cancellationToken, true);
        if (decision.Intent == ZaloBotIntent.IncompleteProfiles)
            return await ListIncompleteProfilesAsync(
                decision,
                sessions,
                selector,
                connectionId,
                groupId,
                incoming,
                cancellationToken,
                true);
        if (decision.Intent == ZaloBotIntent.SyncPoll)
        {
            var syncSelection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (syncSelection.Clarification is not null) return new BotAnswer(syncSelection.Clarification, null, decision.Intent, true);
            var syncSession = syncSelection.Session!;
            var denial = await GetOperatorDenialAsync(syncSession, incoming.SenderId, decision.Intent, true);
            if (denial is not null) return denial;
            var syncBefore = await actionHistory.CaptureAsync(syncSession.Id, cancellationToken);
            var synced = await zaloIntegration.SyncLatestPollAsync(syncSession.AdminUserId, syncSession.Id, selector);
            if (!synced.IsSuccess || synced.Value is null)
                return new BotAnswer(synced.Error ?? "Không đồng bộ được poll.", null, decision.Intent, true);
            await actionHistory.RecordAsync(syncSession.Id, incoming.SenderId, incoming.SenderName,
                "SyncPoll", $"Đồng bộ poll lên danh sách {syncSession.Name}", syncBefore, cancellationToken);
            await waitlists.ProcessVacanciesAsync(syncSession.Id, cancellationToken);
            return new BotAnswer(
                synced.Value.Message + await BuildIncompleteProfilePromptAsync(syncSession, true),
                null,
                decision.Intent,
                true);
        }
        if (decision.Intent == ZaloBotIntent.AutoDraft)
        {
            var draftSelection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (draftSelection.Clarification is not null) return new BotAnswer(draftSelection.Clarification, null, decision.Intent, true);
            var draftSession = draftSelection.Session!;
            var denial = await GetOperatorDenialAsync(draftSession, incoming.SenderId, decision.Intent, true);
            if (denial is not null) return denial;
            var syncError = await SyncPollBeforeDraftAsync(draftSession, selector);
            if (syncError is not null) return new BotAnswer(syncError, null, decision.Intent, true);
            var incompletePrompt = await BuildIncompleteProfilePromptAsync(draftSession, false);
            if (incompletePrompt.Length > 0) return new BotAnswer(incompletePrompt.TrimStart(), null, decision.Intent, true);
            var pendingIntent = draftSession.Status == SessionStatus.Finished
                ? ZaloBotIntent.RedraftConfirm
                : ZaloBotIntent.AutoDraftConfirm;
            await SaveActionConfirmationAsync(connectionId, groupId, incoming.SenderId, draftSession.Id, pendingIntent, cancellationToken);
            return new BotAnswer(
                draftSession.Status == SessionStatus.Finished
                    ? $"⚠️ {draftSession.Name} đã draft xong. Gõ @bot xác nhận draft lại để xoá kết quả hiện tại và chia lại, hoặc @bot huỷ."
                    : $"⚠️ Tự draft sẽ thay đổi đội hình {draftSession.Name}. Gõ @bot xác nhận draft để chạy hoặc @bot huỷ.",
                null,
                decision.Intent,
                true);
        }
        if (decision.Intent == ZaloBotIntent.Redraft)
        {
            var redraftSelection = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
            if (redraftSelection.Clarification is not null) return new BotAnswer(redraftSelection.Clarification, null, decision.Intent, true);
            var redraftSession = redraftSelection.Session!;
            var denial = await GetOperatorDenialAsync(redraftSession, incoming.SenderId, decision.Intent, true);
            if (denial is not null) return denial;
            var incompletePrompt = await BuildIncompleteProfilePromptAsync(redraftSession, false);
            if (incompletePrompt.Length > 0) return new BotAnswer(incompletePrompt.TrimStart(), null, decision.Intent, true);
            await SaveActionConfirmationAsync(connectionId, groupId, incoming.SenderId, redraftSession.Id, ZaloBotIntent.RedraftConfirm, cancellationToken);
            return new BotAnswer($"⚠️ Draft lại sẽ xoá kết quả đội hình {redraftSession.Name}. Gõ @bot xác nhận draft lại để chạy hoặc @bot huỷ.", null, decision.Intent, true);
        }
        if (decision.Intent == ZaloBotIntent.RebalanceTeams)
        {
            return await RebalanceTeamsAsync(
                decision,
                sessions,
                selector,
                ExtractQuestion(incoming),
                connectionId,
                groupId,
                incoming,
                cancellationToken,
                true);
        }
        if (decision.Intent == ZaloBotIntent.SwapTeamPlayers)
        {
            return await SwapTeamPlayersAsync(
                decision,
                sessions,
                selector,
                ExtractQuestion(incoming),
                incoming,
                true);
        }
        if (decision.Intent == ZaloBotIntent.UpcomingSessions)
        {
            var upcoming = sessions.Where(IsUpcoming).Take(5).ToList();
            var lines = upcoming.Select(session => $"- {session.Name}: {(session.StartTime is null ? "chưa chốt giờ" : FormatVietnamTime(session.StartTime.Value))}, {session.PlayerCount}/{session.Capacity} slot");
            return new BotAnswer(upcoming.Count == 0 ? "Hiện chưa có trận sắp tới." : "Các trận sắp tới:\n" + string.Join("\n", lines), null, decision.Intent, true);
        }
        if (decision.Intent == ZaloBotIntent.WeeklySessionCount)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var count = sessions.Count(session => session.StartTime is not null && session.StartTime.Value.ToOffset(VietnamOffset).Date >= monday && session.StartTime.Value.ToOffset(VietnamOffset).Date < monday.AddDays(7));
            return new BotAnswer($"Tuần này nhóm có {count} trận được cấu hình.", null, decision.Intent, true);
        }
        var selected = await SelectSessionAsync(sessions, selector, connectionId, groupId, incoming.SenderId, decision.Intent, cancellationToken);
        if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null, decision.Intent, true);
        var session = selected.Session!;
        return decision.Intent switch
        {
            ZaloBotIntent.SessionSchedule => new BotAnswer(session.StartTime is null ? $"Admin chưa chốt giờ cho {session.Name}." : $"{session.Name} diễn ra lúc {FormatVietnamTime(session.StartTime.Value)}{FormatLocationSuffix(session)}.", null, decision.Intent, true),
            ZaloBotIntent.SelfMembership => new BotAnswer(session.SenderIsListed ? $"Bạn đang có tên trong {session.Name}{FormatScheduleSuffix(session)}." : $"Mình chưa thấy bạn trong danh sách {session.Name}.", null, decision.Intent, true),
            ZaloBotIntent.LocationParking => new BotAnswer($"{session.Name} — địa điểm: {session.Location ?? "chưa cấu hình"} — gửi xe: {session.ParkingInstructions ?? "chưa cấu hình"}.", session.LocationImageUrl, decision.Intent, true),
            ZaloBotIntent.MissingSlots => new BotAnswer(session.PlayerCount >= session.Capacity ? $"{session.Name} đã đủ {session.Capacity} slot." : $"{session.Name} đang có {session.PlayerCount}/{session.Capacity}, còn thiếu {session.Capacity - session.PlayerCount} slot.", null, decision.Intent, true),
            ZaloBotIntent.PaymentQr => new BotAnswer(string.IsNullOrWhiteSpace(session.PaymentQrImageUrl) ? $"Admin chưa cấu hình QR thanh toán cho {session.Name}." : session.PaymentInstructions ?? $"QR thanh toán cho {session.Name}.", session.PaymentQrImageUrl, decision.Intent, true),
            ZaloBotIntent.Roster => new BotAnswer(session.PlayerNames.Count == 0 ? $"{session.Name} chưa có ai trong danh sách." : $"Danh sách {session.Name} ({session.PlayerCount}/{session.Capacity}):\n{string.Join("\n", session.PlayerNames.Select((name, index) => $"{index + 1}. {name}"))}", null, decision.Intent, true),
            _ => new BotAnswer("Mình chưa đủ dữ kiện để trả lời chắc chắn. Bạn thử thêm ngày hoặc tên trận nhé.", null, decision.Intent, true)
        };
    }

    private async Task<List<SessionSnapshot>> LoadSessionSnapshotsAsync(
        IReadOnlyList<string> connectionIds,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        var loadedSessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId != null &&
                              connectionIds.Contains(session.ZaloConnectionId) &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var upcomingCutoff = DateTimeOffset.UtcNow.AddHours(-4);
        var sessions = loadedSessions
            .OrderBy(session => session.StartTime is not null && session.StartTime < upcomingCutoff ? 2 : session.StartTime is null ? 1 : 0)
            .ThenBy(session => session.StartTime)
            .ThenByDescending(session => session.UpdatedAt)
            .ToList();
        var sessionIds = sessions.Select(session => session.Id).ToList();
        var activePlayers = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent)
            .Select(player => new
            {
                player.SessionId,
                player.DisplayName,
                ZaloUserId = player.PlayerProfile == null ? null : player.PlayerProfile.ZaloUserId
            })
            .ToListAsync(cancellationToken);
        var playersBySession = activePlayers
            .GroupBy(player => player.SessionId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var normalizedSenderId = NormalizeId(senderId);
        var imports = await db.PollImports
            .AsNoTracking()
            .Where(import => sessionIds.Contains(import.SessionId))
            .OrderByDescending(import => import.ImportedAt)
            .ToListAsync(cancellationToken);
        var latestPolls = imports.GroupBy(import => import.SessionId)
            .ToDictionary(group => group.Key, group => group.First().PollQuestion);

        return sessions.Select(session => new SessionSnapshot(
            session.Id,
            session.Name,
            session.AdminUserId,
            session.Status,
            ParseOperatorIds(session.BotOperatorZaloUserIdsJson),
            session.StartTime,
            session.Location,
            session.ParkingInstructions,
            session.LocationImageUrl,
            session.PaymentInstructions,
            session.PaymentQrImageUrl,
            session.BotCustomInstructions,
            session.ReminderEnabled,
            session.ReminderIntervalMinutes > 0
                ? session.ReminderIntervalMinutes
                : session.ReminderIntervalHours * 60,
            session.ReminderRepeats,
            session.NextReminderAt,
            playersBySession.GetValueOrDefault(session.Id)?.Count ?? 0,
            session.TeamCount * session.TeamSize,
            playersBySession.GetValueOrDefault(session.Id)?.Any(player => NormalizeId(player.ZaloUserId) == normalizedSenderId) == true,
            playersBySession.GetValueOrDefault(session.Id)?
                .FirstOrDefault(player => NormalizeId(player.ZaloUserId) == normalizedSenderId)?.DisplayName,
            latestPolls.GetValueOrDefault(session.Id),
            playersBySession.GetValueOrDefault(session.Id)?
                .Select(player => player.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? [])).ToList();
    }

    private static SessionSelection SelectSession(IReadOnlyList<SessionSnapshot> sessions, string normalizedQuestion)
    {
        var hasExplicitSelector = HasExplicitSessionSelector(sessions, normalizedQuestion);
        var candidates = hasExplicitSelector ? sessions.ToList() : sessions.Where(IsUpcoming).ToList();
        if (candidates.Count == 0) candidates = sessions.Take(1).ToList();
        var explicitMatches = candidates.Where(session => QuestionMatchesSession(normalizedQuestion, session)).ToList();
        if (explicitMatches.Count == 1) return new SessionSelection(explicitMatches[0], null);
        if (hasExplicitSelector && explicitMatches.Count == 0)
        {
            var available = sessions.Take(4).Select(FormatSessionChoice);
            if (HasAny(normalizedQuestion, "hom nay", "bua nay"))
            {
                var now = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
                var nearest = sessions.FirstOrDefault(IsUpcoming);
                var nearestText = nearest is null
                    ? " Hiện cũng chưa có trận sắp tới nào."
                    : $" Trận gần nhất là {FormatSessionChoice(nearest)}.";
                return new SessionSelection(
                    null,
                    $"Hôm nay là {FormatVietnamDate(now)} và nhóm chưa có trận nào hôm nay.{nearestText}");
            }
            return new SessionSelection(null, $"mình không tìm thấy trận đúng ngày/tên bạn hỏi. Các trận đang có: {string.Join(", ", available)}.");
        }
        if (explicitMatches.Count > 1) candidates = explicitMatches;
        if (candidates.Count == 1) return new SessionSelection(candidates[0], null);

        var choices = candidates.Take(4).Select(FormatSessionChoice);
        return new SessionSelection(null, $"bạn đang hỏi trận nào: {string.Join(", ", choices)}?");
    }

    private static bool QuestionMatchesSession(string question, SessionSnapshot session)
    {
        if (question.Contains(NormalizeText(session.Name), StringComparison.Ordinal)) return true;
        if (session.StartTime is null) return false;
        var local = session.StartTime.Value.ToOffset(VietnamOffset);
        var today = DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date;
        if (HasAny(question, "hom nay", "bua nay") && local.Date == today) return true;
        if (HasAny(question, "ngay mai", "mai nay") && local.Date == today.AddDays(1)) return true;
        foreach (Match dateMatch in Regex.Matches(question, @"(?<!\d)(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?(?!\d)"))
        {
            if (!int.TryParse(dateMatch.Groups[1].Value, out var day) ||
                !int.TryParse(dateMatch.Groups[2].Value, out var month) ||
                day != local.Day || month != local.Month) continue;
            if (!dateMatch.Groups[3].Success) return true;
            var year = int.Parse(dateMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
            if (year == local.Year) return true;
        }
        var dayTokens = local.DayOfWeek switch
        {
            DayOfWeek.Monday => new[] { "t2", "thu 2", "thu hai" },
            DayOfWeek.Tuesday => new[] { "t3", "thu 3", "thu ba" },
            DayOfWeek.Wednesday => new[] { "t4", "thu 4", "thu tu" },
            DayOfWeek.Thursday => new[] { "t5", "thu 5", "thu nam" },
            DayOfWeek.Friday => new[] { "t6", "thu 6", "thu sau" },
            DayOfWeek.Saturday => new[] { "t7", "thu 7", "thu bay" },
            _ => new[] { "cn", "chu nhat" }
        };
        return dayTokens.Any(token => ContainsToken(question, token));
    }

    private static bool IsUpcoming(SessionSnapshot session) =>
        session.StartTime is null || session.StartTime >= DateTimeOffset.UtcNow.AddHours(-4);

    private static string FormatVietnamTime(DateTimeOffset time)
    {
        var local = time.ToOffset(VietnamOffset);
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Monday => "thứ Hai",
            DayOfWeek.Tuesday => "thứ Ba",
            DayOfWeek.Wednesday => "thứ Tư",
            DayOfWeek.Thursday => "thứ Năm",
            DayOfWeek.Friday => "thứ Sáu",
            DayOfWeek.Saturday => "thứ Bảy",
            _ => "Chủ nhật"
        };
        return $"{local:HH:mm} {day} {local:dd/MM}";
    }

    private static string FormatVietnamDate(DateTimeOffset time)
    {
        var local = time.ToOffset(VietnamOffset);
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Monday => "thứ Hai",
            DayOfWeek.Tuesday => "thứ Ba",
            DayOfWeek.Wednesday => "thứ Tư",
            DayOfWeek.Thursday => "thứ Năm",
            DayOfWeek.Friday => "thứ Sáu",
            DayOfWeek.Saturday => "thứ Bảy",
            _ => "Chủ nhật"
        };
        return $"{day} {local:dd/MM/yyyy}";
    }

    private static string FormatShortTime(DateTimeOffset? time) =>
        time is null ? string.Empty : $" ({FormatVietnamTime(time.Value)})";

    private static string FormatScheduleSuffix(SessionSnapshot session)
    {
        var time = session.StartTime is null ? string.Empty : $", trận lúc {FormatVietnamTime(session.StartTime.Value)}";
        var location = string.IsNullOrWhiteSpace(session.Location) ? string.Empty : $" tại {session.Location}";
        return time + location;
    }

    private static string FormatLocationSuffix(SessionSnapshot session) =>
        string.IsNullOrWhiteSpace(session.Location) ? string.Empty : $" tại {session.Location}";

    private static string JoinSessionNames(IReadOnlyList<SessionSnapshot> sessions) =>
        string.Join(", ", sessions.Select(session => session.Name));

    private static string FormatSessionChoice(SessionSnapshot session) =>
        session.StartTime is null ? session.Name : $"{session.Name} ({FormatVietnamTime(session.StartTime.Value)})";

    private static bool HasExplicitSessionSelector(IReadOnlyList<SessionSnapshot> sessions, string question) =>
        HasAny(question, "hom nay", "bua nay", "ngay mai", "mai nay") ||
        Regex.IsMatch(question, @"(?<!\d)\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?(?!\d)") ||
        new[] { "t2", "thu 2", "thu hai", "t3", "thu 3", "thu ba", "t4", "thu 4", "thu tu", "t5", "thu 5", "thu nam", "t6", "thu 6", "thu sau", "t7", "thu 7", "thu bay", "cn", "chu nhat" }
            .Any(token => ContainsToken(question, token)) ||
        sessions.Any(session => question.Contains(NormalizeText(session.Name), StringComparison.Ordinal));

    private async Task<List<ZaloBotLearnedRule>> LoadLearnedRulesAsync(
        IReadOnlyList<string> connectionIds,
        string groupId,
        CancellationToken cancellationToken)
    {
        return await db.ZaloBotLearnedRules
            .AsNoTracking()
            .Where(rule => connectionIds.Contains(rule.ZaloConnectionId) &&
                           rule.GroupId == groupId &&
                           rule.Status == ZaloBotRuleStatus.Approved)
            .OrderByDescending(rule => rule.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<BotAnswer> SaveLearnedRuleAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string trigger,
        string answer,
        CancellationToken cancellationToken)
    {
        var cleanTrigger = Clean(trigger.Trim(' ', '"', '\'', '“', '”', '‘', '’'), 500);
        var cleanAnswer = Clean(answer, 4000);
        var normalizedTrigger = NormalizeRuleText(cleanTrigger ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleanTrigger) || string.IsNullOrWhiteSpace(cleanAnswer) || normalizedTrigger.Length < 2)
        {
            return new BotAnswer("Mình chưa hiểu phần bạn muốn ghi nhớ. Bạn có thể nói tự nhiên như: “từ giờ khi ai hỏi vị trí thì nhắc luôn chỗ gửi xe”.", null);
        }

        var existing = await db.ZaloBotLearnedRules.SingleOrDefaultAsync(rule =>
            rule.ZaloConnectionId == connectionId &&
            rule.GroupId == groupId &&
            rule.NormalizedTrigger == normalizedTrigger,
            cancellationToken);
        if (existing is null)
        {
            db.ZaloBotLearnedRules.Add(new ZaloBotLearnedRule
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                Trigger = cleanTrigger,
                NormalizedTrigger = normalizedTrigger,
                Answer = cleanAnswer,
                CreatedBySenderId = NormalizeId(incoming.SenderId),
                CreatedBySenderName = Clean(incoming.SenderName, 160) ?? "Thành viên Zalo",
                Status = ZaloBotRuleStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Trigger = cleanTrigger;
            existing.Answer = cleanAnswer;
            existing.CreatedBySenderId = NormalizeId(incoming.SenderId);
            existing.CreatedBySenderName = Clean(incoming.SenderName, 160) ?? "Thành viên Zalo";
            existing.Status = ZaloBotRuleStatus.Pending;
            existing.ApprovedAt = null;
            existing.ApprovedByUserId = null;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new BotAnswer($"Mình đã gửi đề xuất ghi nhớ “{cleanTrigger}” để admin duyệt. Rule chỉ có hiệu lực sau khi được duyệt.", null, ZaloBotIntent.GeneralChat);
    }

    private static bool TryParseNaturalLearning(string question, out string trigger, out string answer)
    {
        var patterns = new[]
        {
            @"^\s*(?:từ giờ|tu gio|lần sau|lan sau)\s*(?:,\s*)?(?<trigger>.+?)\s*(?:=>|->)\s*(?<answer>.+?)\s*$",
            @"^\s*(?:từ giờ|tu gio|lần sau|lan sau)\s*(?:,\s*)?(?<trigger>.+?)\s+(?:thì|thi)\s+(?:hãy|hay)?\s*(?:(?:trả lời|tra loi|nói|noi|khen)\s+)?(?<answer>.+?)\s*$",
            @"^\s*(?:nhớ là|nho la|ghi nhớ là|ghi nho la)\s*(?:,\s*)?(?<trigger>.+?)\s+(?:là|la)\s+(?<answer>.+?)\s*$",
            @"^\s*khi ai hỏi\s+(?<trigger>.+?)\s+(?:thì|thi)\s+(?<answer>.+?)\s*$",
            @"^\s*(?:không phải|khong phai)\s+(?<trigger>.+?)\s+(?:mà|ma)\s+(?:là|la)\s+(?<answer>.+?)\s*$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(
                question,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            trigger = match.Groups["trigger"].Value.Trim(' ', '"', '\'', '“', '”', '‘', '’');
            answer = match.Groups["answer"].Value.Trim();
            return !string.IsNullOrWhiteSpace(trigger) && !string.IsNullOrWhiteSpace(answer);
        }

        trigger = string.Empty;
        answer = string.Empty;
        return false;
    }

    private static string RenderLearnedAnswer(string answer, string senderName)
    {
        var safeSenderName = (Clean(senderName, 80) ?? "người đang hỏi").TrimStart('@');
        return Regex.Replace(
            answer,
            @"người\s+(?:đang\s+)?(?:hỏi|nhắn)",
            safeSenderName,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeRuleText(string value)
    {
        var normalized = NormalizeText(value);
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool ContainsToken(string value, string token) =>
        Regex.IsMatch(value, $@"(?<![a-z0-9]){Regex.Escape(token)}(?![a-z0-9])", RegexOptions.CultureInvariant);

    private static string ExtractQuestion(ZaloIncomingMessageEvent incoming)
    {
        var value = incoming.Content ?? string.Empty;
        foreach (var mention in incoming.Mentions
                     .Where(mention => NormalizeId(mention.Uid) == NormalizeId(incoming.BotId))
                     .OrderByDescending(mention => mention.Pos))
        {
            if (mention.Pos >= 0 && mention.Len > 0 && mention.Pos + mention.Len <= value.Length)
            {
                value = value.Remove(mention.Pos, mention.Len);
            }
        }
        value = Regex.Replace(value, @"^\s*@\S+\s*", string.Empty, RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static IReadOnlyList<ZaloMentionedUser> ExtractMentionedUsers(ZaloIncomingMessageEvent incoming)
    {
        var users = new List<ZaloMentionedUser>();
        foreach (var mention in incoming.Mentions
                     .OrderBy(item => item.Pos)
                     .Where(item =>
                     NormalizeId(item.Uid) != NormalizeId(incoming.BotId)))
        {
            if (mention.Pos < 0 || mention.Len <= 0 || mention.Pos + mention.Len > incoming.Content.Length) continue;
            var displayName = incoming.Content.Substring(mention.Pos, mention.Len).Trim().TrimStart('@');
            if (displayName.Length == 0) continue;
            users.Add(new ZaloMentionedUser(NormalizeId(mention.Uid), displayName));
        }
        return users
            .GroupBy(item => item.ZaloUserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, BridgeMember>> ResolveZaloMembersAsync(
        SessionSnapshot session,
        IEnumerable<string> memberIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = memberIds
            .Select(NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return new Dictionary<string, BridgeMember>(StringComparer.Ordinal);

        var result = await zaloIntegration.ResolveMembersAsync(session.AdminUserId, session.Id, ids);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "Could not resolve Zalo member avatars for Session={SessionId}: {Error}",
                session.Id,
                result.Error);
            return new Dictionary<string, BridgeMember>(StringComparer.Ordinal);
        }

        return result.Value
            .GroupBy(member => NormalizeId(member.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static ZaloMentionedUser? FindMentionedUser(
        string playerReference,
        IReadOnlyList<ZaloMentionedUser> mentionedUsers)
    {
        var normalized = NormalizeText(playerReference);
        var exact = mentionedUsers.FirstOrDefault(item => NormalizeText(item.DisplayName) == normalized);
        if (exact is not null) return exact;
        return mentionedUsers
            .Select(item => new { Item = item, Score = ZaloBotIntelligence.TokenSimilarity(item.DisplayName, playerReference) })
            .Where(item => item.Score >= .78)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Item)
            .FirstOrDefault();
    }

    private static string? CombineSettings(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return distinct.Count == 0 ? null : string.Join("\n---\n", distinct);
    }

    private static bool HasAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static string NormalizeText(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == 'đ' ? 'd' : character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static DateTimeOffset ToSafeTimestamp(long unixMs)
    {
        try
        {
            var value = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            return Math.Abs((value - DateTimeOffset.UtcNow).TotalDays) > 30 ? DateTimeOffset.UtcNow : value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static bool IsOptionalHttpUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string? SelectReminderMessage(
        string? aiMessage,
        string? deterministicMessage,
        string originalQuestion)
    {
        foreach (var candidate in new[] { aiMessage, deterministicMessage })
        {
            var cleaned = ZaloNaturalCommandParser.SanitizeReminderMessage(
                Clean(candidate, 2000),
                originalQuestion);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            var normalized = ZaloBotIntelligence.Normalize(cleaned);
            if (normalized.Length < 2 || normalized == ZaloBotIntelligence.Normalize(originalQuestion)) continue;
            if (Regex.IsMatch(
                    normalized,
                    @"(?:tao|dat|hen|len)\s+lich|(?:tag|nhac)\s+(?:moi\s+nguoi|thanh\s+vien)|ngay\s+mai.*\b(?:[0-2]?\d\s*h|gio)\b",
                    RegexOptions.CultureInvariant))
                continue;
            return cleaned.Trim(' ', ',', '.', ':', ';', '"', '\'', '“', '”');
        }
        return null;
    }

    private static string FormatReminderContentForDisplay(string? storedMessage)
    {
        var content = ZaloNaturalCommandParser.SanitizeReminderMessage(storedMessage, storedMessage ?? string.Empty);
        return string.IsNullOrWhiteSpace(content)
            ? "Nội dung: bot sẽ viết theo số slot thực tế lúc gửi."
            : $"Nội dung: {content}";
    }

    private static string FormatReminderAction(ZaloReminderCommandKind kind) => kind switch
    {
        ZaloReminderCommandKind.Disable => "Tắt lịch nhắc",
        ZaloReminderCommandKind.Update => "Cập nhật lịch nhắc",
        ZaloReminderCommandKind.TriggerNow => "Xếp lịch nhắc ngay",
        _ => "Tạo lịch nhắc"
    };

    private static bool HasExplicitAllAudience(string question)
    {
        var normalized = ZaloBotIntelligence.Normalize(question);
        return Regex.IsMatch(
            normalized,
            @"(?:moi\s+nguoi|ca\s+nhom|moi\s+thanh\s+vien|toan\s+bo|tat\s+ca|nguoi\s+trong\s+nhom)",
            RegexOptions.CultureInvariant);
    }

    private static bool HasExplicitReminderMessageChange(string question) =>
        Regex.IsMatch(
            ZaloBotIntelligence.Normalize(question),
            @"(?:doi|thay|sua|cap\s+nhat)\s+(?:lai\s+)?(?:noi\s+dung|tin\s+nhan)|noi\s+dung\s+(?:thanh|la)",
            RegexOptions.CultureInvariant);

    private static string BuildReminderConfirmation(
        ZaloReminderCommand command,
        IReadOnlyList<SessionSnapshot> targets,
        DateTimeOffset now)
    {
        var operation = command.Kind switch
        {
            ZaloReminderCommandKind.Update => "cập nhật",
            ZaloReminderCommandKind.Disable => "tắt",
            _ => "tạo"
        };
        var audience = command.Audience == ZaloReminderAudience.Roster
            ? "những người đã vote hoặc share slot"
            : "cả nhóm (@all)";
        var targetLines = targets.Select(target =>
        {
            var dueAt = command.Kind == ZaloReminderCommandKind.Disable
                ? (DateTimeOffset?)null
                : ComputeReminderDueAt(command, target, now);
            var timing = dueAt is null
                ? "giữ nguyên thời gian hiện tại"
                : FormatVietnamTime(dueAt.Value);
            return $"- {target.Name}: {timing}";
        });
        var frequency = command.Kind == ZaloReminderCommandKind.Disable
            ? "Lịch sẽ không gửi thêm nữa."
            : command.Kind == ZaloReminderCommandKind.Update && command.DelayMinutes is null
                ? "Giữ nguyên tần suất hiện tại."
            : command.Repeats && command.DelayMinutes is not null
                ? $"Sau lần đầu, lặp lại mỗi {FormatDuration(command.DelayMinutes.Value)}."
                : "Chỉ gửi một lần.";
        var content = command.Kind == ZaloReminderCommandKind.Update && string.IsNullOrWhiteSpace(command.CustomMessage)
            ? "Giữ nguyên nội dung hiện tại."
            : string.IsNullOrWhiteSpace(command.CustomMessage)
                ? "Bot sẽ tự soạn lời nhắc phù hợp với mục đích của bạn và dữ liệu trận."
            : $"Nội dung nhắc dự kiến: {command.CustomMessage.Trim()}";
        var condition = command.Kind == ZaloReminderCommandKind.Disable
            ? string.Empty
            : command.Kind == ZaloReminderCommandKind.Update
                ? " Giữ nguyên điều kiện gửi hiện tại."
            : command.OnlyIfMissingSlots
                ? command.StopWhenFull
                    ? " Chỉ gửi khi còn thiếu slot và tự dừng khi đủ; lịch cũng tự tắt khi tới giờ trận."
                    : " Chỉ gửi nếu trận vẫn còn thiếu slot."
                : "";
        var attachment = command.IncludePaymentQr
            ? " Bot sẽ gửi kèm ảnh QR thanh toán đã cấu hình của trận."
            : string.Empty;
        return $"Mình hiểu bạn muốn {operation} lịch nhắc:\n" +
               string.Join("\n", targetLines) +
               $"\nGửi cho: {audience}.\n{content}{attachment}\n{frequency}{condition}\n\nGõ @bot xác nhận để thực hiện hoặc @bot huỷ. Chưa có thay đổi nào được lưu.";
    }

    private static ZaloBotSettingsResponse ToSettings(MatchSession session) => new(
        session.Id,
        session.StartTime,
        session.Location,
        session.ParkingInstructions,
        session.LocationImageUrl,
        session.PaymentInstructions,
        session.PaymentQrImageUrl,
        session.BotEnabled,
        session.BotCustomInstructions,
        ParseOperatorIds(session.BotOperatorZaloUserIdsJson).ToList(),
        session.ReminderEnabled,
        session.ReminderLeadHours,
        session.ReminderIntervalHours,
        session.LastReminderAt,
        session.NextReminderAt,
        session.ReminderRepeats,
        session.ReminderFailureCount,
        session.LastReminderError);

    private static HashSet<string> ParseOperatorIds(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [])
                .Select(NormalizeId)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ZaloBotLearnedRuleResponse ToLearnedRuleResponse(ZaloBotLearnedRule rule) => new(
        rule.Id,
        rule.Trigger,
        rule.Answer,
        rule.Status,
        rule.Priority,
        rule.CreatedBySenderName,
        rule.CreatedAt,
        rule.ApprovedByUserId,
        rule.ApprovedAt,
        rule.ReviewNote);

    private sealed record BotAnswer(
        string Text,
        string? ImageUrl,
        ZaloBotIntent Intent = ZaloBotIntent.Unknown,
        bool AiCalled = false,
        bool TextGeneratedByAi = false,
        IReadOnlyList<BridgeOutgoingMention>? Mentions = null,
        IReadOnlyList<string>? ProtectedTerms = null);
    private sealed record SessionSelection(SessionSnapshot? Session, string? Clarification);
    private sealed record PendingResolution(
        bool Cancelled,
        ZaloBotIntent? Intent,
        SessionSnapshot? Session,
        string? Clarification,
        ZaloReminderCommand? ReminderCommand = null,
        IReadOnlyList<SessionSnapshot>? TargetSessions = null,
        string? ActionHistoryId = null,
        ZaloRepairShareSlotCommand? RepairCommand = null,
        ZaloSlotTransferCommand? TransferCommand = null,
        TeamRebalancePlan? RebalancePlan = null,
        ZaloAddGuestCommand? GuestCommand = null)
    {
        public static PendingResolution None { get; } = new(false, null, null, null);
    }
    private sealed record ReminderConfirmationPayload(
        IReadOnlyList<string> SessionIds,
        ZaloReminderCommand Command);
    private sealed record UndoConfirmationPayload(string SessionId, string ActionId);
    private sealed record RepairShareSlotSelectionPayload(
        IReadOnlyList<string> SessionIds,
        ZaloRepairShareSlotCommand Command);
    private sealed record RepairShareSlotConfirmationPayload(
        string SessionId,
        ZaloRepairShareSlotCommand Command);
    private sealed record SlotTransferSelectionPayload(
        IReadOnlyList<string> SessionIds,
        ZaloSlotTransferCommand Command);
    private sealed record SlotTransferConfirmationPayload(
        string SessionId,
        ZaloSlotTransferCommand Command);
    private sealed record AddGuestSelectionPayload(
        IReadOnlyList<string> SessionIds,
        ZaloAddGuestCommand Command);
    private sealed record TeamRebalanceConfirmationPayload(
        string SessionId,
        TeamRebalancePlan Plan);
    private sealed record PlayerProfileValues(PlayerGender? Gender, PlayerRole? Role, PlayerLevel? Level);
    private sealed record SessionSnapshot(
        string Id,
        string Name,
        string AdminUserId,
        SessionStatus Status,
        IReadOnlySet<string> OperatorZaloUserIds,
        DateTimeOffset? StartTime,
        string? Location,
        string? ParkingInstructions,
        string? LocationImageUrl,
        string? PaymentInstructions,
        string? PaymentQrImageUrl,
        string? CustomInstructions,
        bool ReminderEnabled,
        int ReminderIntervalMinutes,
        bool ReminderRepeats,
        DateTimeOffset? NextReminderAt,
        int PlayerCount,
        int Capacity,
        bool SenderIsListed,
        string? SenderPlayerName,
        string? LatestPoll,
        IReadOnlyList<string> PlayerNames);
}
