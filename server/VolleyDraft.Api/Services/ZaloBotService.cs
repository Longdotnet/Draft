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
        if (string.IsNullOrWhiteSpace(response.Text))
        {
            await FinishMessageAsync(storedMessage.Id, processingToken, response.Intent, response.AiCalled, "no_reply", cancellationToken);
            return;
        }

        var senderName = (Clean(incoming.SenderName, 50) ?? "bạn").TrimStart('@');
        var mentionLabel = $"@{senderName}";
        var reply = $"{mentionLabel} {response.Text.Trim()}";
        try
        {
            await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                groupId,
                reply,
                [new BridgeOutgoingMention(NormalizeId(incoming.SenderId), 0, mentionLabel.Length)],
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

        var pending = await ResolvePendingConversationAsync(activeConnectionId, groupId, incoming.SenderId, normalizedQuestion, sessions, cancellationToken);
        if (pending.Cancelled)
        {
            return new BotAnswer("Đã huỷ câu hỏi đang chờ. Bạn có thể hỏi câu khác nhé.", null, ZaloBotIntent.GeneralChat);
        }
        if (pending.Clarification is not null)
        {
            return new BotAnswer(pending.Clarification, null, ZaloBotIntent.Unknown);
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
                " \n🤖 Menu bot:\n1. Xem giờ và địa điểm trận\n2. Kiểm tra mình có trong danh sách\n3. Xem vị trí và hướng dẫn gửi xe\n4. Xem còn thiếu bao nhiêu slot\n5. Xem các trận sắp tới\n6. Xem QR và hướng dẫn thanh toán\n7. Xem danh sách 3 team\n8. Đồng bộ người đã vote lên web (có quyền)\n9. Tự chạy draft/khui túi (có quyền + xác nhận)\n10. Gửi ảnh card 3 team\n\nLịch nhắc (có quyền):\n- @bot cứ 6 tiếng nhắc nếu còn thiếu slot\n- @bot cứ 30 phút nhắc T6 nếu còn thiếu\n- @bot nhắc ngay / xem lịch nhắc / tắt nhắc CN\n\nNgười có quyền gồm trưởng nhóm, phó nhóm và UID được admin cấp. Nếu có nhiều trận, hãy thêm ngày hoặc tên trận.",
                null,
                ZaloBotIntent.Help);
        }

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
            var text = FormatTeamLineup(session.Name, state.Value.TeamPreview);
            var imageUrl = decision.Intent == ZaloBotIntent.TeamImage ? teamCards.GetPublicUrl(session.Id) : null;
            return new BotAnswer(text, imageUrl, decision.Intent);
        }

        if (decision.Intent == ZaloBotIntent.UpdatePlayerProfile)
            return await UpdatePlayerProfileAsync(decision, sessions, normalizedQuestion, question, incoming, false);

        if (decision.Intent == ZaloBotIntent.AddGuestPlayer)
            return await AddGuestPlayerAsync(decision, sessions, normalizedQuestion, question, incoming, false);

        if (decision.Intent == ZaloBotIntent.ShareSlot)
            return await ShareSlotAsync(decision, sessions, normalizedQuestion, question, incoming, false);

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
            var synced = await zaloIntegration.SyncLatestPollAsync(session.AdminUserId, session.Id, question);
            if (!synced.IsSuccess || synced.Value is null)
                return new BotAnswer(synced.Error ?? "Không đồng bộ được poll.", null, decision.Intent);
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
            var drafted = await draftService.AutoRunDraftAsync(selected.AdminUserId, selected.Id, isRedraft);
            if (!drafted.IsSuccess || drafted.Value is null)
                return new BotAnswer($"Không thể tự draft: {drafted.Error}", null, decision.Intent);
            return new BotAnswer(
                $"Đã {(isRedraft ? "draft lại" : "tự draft")} xong {selected.Name}.\n{FormatTeamLineup(selected.Name, drafted.Value.TeamPreview)}",
                teamCards.GetPublicUrl(selected.Id),
                decision.Intent);
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
        return new BotAnswer(await ai.AnswerAsync(aiContext, cancellationToken), null, ZaloBotIntent.GeneralChat, true);
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
        var sessionsWithPlayer = sessions
            .Where(session => FindBestMentionedPlayerName(originalQuestion, session.PlayerNames) is not null)
            .ToList();
        var selected = sessionsWithPlayer.Count == 1
            ? new SessionSelection(sessionsWithPlayer[0], null)
            : SelectSession(sessions, normalizedQuestion);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại lệnh cập nhật kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;

        var playerReference = FindBestMentionedPlayerName(originalQuestion, session.PlayerNames) ?? ExtractProfilePlayerReference(originalQuestion);
        if (string.IsNullOrWhiteSpace(playerReference))
            return new BotAnswer("Mình chưa nhận ra tên người cần cập nhật. Ví dụ: @bot cập nhật Nick Tran: nam, công, trung bình.", null, decision.Intent, aiCalled);
        var parsed = ParsePlayerProfileValues(originalQuestion, playerReference);
        if (parsed.Gender is null && parsed.Role is null && parsed.Level is null)
            return new BotAnswer("Mình chưa nhận ra thông tin hồ sơ. Dùng giới tính nam/nữ; vị trí công/thủ/chuyền 2/toàn diện; trình độ tốt/trung bình/mới.", null, decision.Intent, aiCalled);

        var updated = await draftService.UpdatePlayerProfileFromBotAsync(
            session.AdminUserId,
            session.Id,
            playerReference,
            parsed.Gender,
            parsed.Role,
            parsed.Level);
        if (!updated.IsSuccess || updated.Value is null)
            return new BotAnswer(updated.Error ?? "Không cập nhật được hồ sơ.", null, decision.Intent, aiCalled);
        var player = updated.Value;
        var remaining = await draftService.GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
        var remainingText = remaining.IsSuccess && remaining.Value is { Count: > 0 }
            ? $" Còn hồ sơ chưa xác nhận: {string.Join(", ", remaining.Value.Take(10).Select(item => item.DisplayName))}."
            : " Hồ sơ đã đủ điều kiện để draft.";
        return new BotAnswer(
            $"Đã cập nhật {player.DisplayName}: {FormatGender(player.Gender)}, {FormatRole(player.Role)}, {FormatLevel(player.Level)}.{remainingText}",
            null,
            decision.Intent,
            aiCalled);
    }

    private async Task<BotAnswer> AddGuestPlayerAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        bool aiCalled)
    {
        var selected = SelectSession(sessions, normalizedQuestion);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại lệnh +1 kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;
        var guestName = ExtractGuestName(originalQuestion);
        if (string.IsNullOrWhiteSpace(guestName))
            return new BotAnswer("Mình chưa nhận ra khách của ai. Ví dụ: @bot +1 số lượng vote cho bạn của Nick Tran.", null, decision.Intent, aiCalled);
        var added = await draftService.AddGuestPlayerFromBotAsync(session.AdminUserId, session.Id, guestName);
        if (!added.IsSuccess || added.Value is null)
            return new BotAnswer(added.Error ?? "Không +1 được người chơi.", null, decision.Intent, aiCalled);
        var result = added.Value;
        var divisible = result.PresentPlayerCount % result.TeamCount == 0;
        var countText = divisible
            ? $"Tổng hiện tại {result.PresentPlayerCount}, đã chia hết cho {result.TeamCount} team."
            : $"Tổng hiện tại {result.PresentPlayerCount}, chưa chia hết cho {result.TeamCount} team.";
        return new BotAnswer(
            $"Đã +1 {result.Player.DisplayName} trên web. {countText} " +
            $"Khách chưa có hồ sơ; cập nhật ít nhất giới tính bằng `@bot cập nhật {result.Player.DisplayName}: nam` hoặc `nữ` trước khi draft.",
            null,
            decision.Intent,
            aiCalled);
    }

    private async Task<BotAnswer> ShareSlotAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string normalizedQuestion,
        string originalQuestion,
        ZaloIncomingMessageEvent incoming,
        bool aiCalled)
    {
        if (!ZaloBotIntelligence.TryExtractSharePlayerNames(originalQuestion, out var rawAnchor, out var rawPartner))
            return new BotAnswer("Mình chưa nhận ra hai người cần share. Ví dụ: @bot Nick Tran muốn share slot với Thanh Tuyền.", null, decision.Intent, aiCalled);
        var matchingSessions = sessions
            .Where(session => ResolvePlayerReference(rawAnchor, session.PlayerNames) is not null)
            .ToList();
        var finishedMatchingSessions = matchingSessions.Where(session => session.Status == SessionStatus.Finished).ToList();
        var selected = finishedMatchingSessions.Count == 1
            ? new SessionSelection(finishedMatchingSessions[0], null)
            : matchingSessions.Count == 1
                ? new SessionSelection(matchingSessions[0], null)
            : SelectSession(sessions, normalizedQuestion);
        if (selected.Clarification is not null)
            return new BotAnswer(selected.Clarification + " Hãy gửi lại đầy đủ lệnh share slot kèm ngày hoặc tên trận.", null, decision.Intent, aiCalled);
        var session = selected.Session!;
        var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
        if (denial is not null) return denial;
        var anchor = ResolvePlayerReference(rawAnchor, session.PlayerNames);
        if (anchor is null)
            return new BotAnswer($"Không tìm thấy '{rawAnchor}' trong đội hình.", null, decision.Intent, aiCalled);
        var existingPartner = ResolvePlayerReference(rawPartner, session.PlayerNames);
        var partner = existingPartner ?? (NormalizeText(rawPartner) == "ban"
            ? NextExternalShareName(anchor, session.PlayerNames)
            : rawPartner);
        var shared = await draftService.SharePostDraftSlotAsync(session.AdminUserId, session.Id, anchor, partner);
        if (!shared.IsSuccess || shared.Value is null)
            return new BotAnswer(shared.Error ?? "Không cập nhật được share slot.", null, decision.Intent, aiCalled);
        var result = shared.Value;
        var profileNote = result.NeedsProfileUpdate
            ? " Hồ sơ người mới chưa có giới tính; cần cập nhật trước nếu sau này draft lại."
            : string.Empty;
        return new BotAnswer(
            $"Đã cập nhật {result.AnchorPlayerName} và {result.PartnerPlayerName} share slot tại {result.TeamName}.{profileNote}",
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

        var swapped = await draftService.SwapDraftPlayersAsync(
            session.AdminUserId,
            session.Id,
            firstPlayer,
            secondPlayer);
        if (!swapped.IsSuccess || swapped.Value is null)
            return new BotAnswer(swapped.Error ?? "Không đổi được hai người này.", null, decision.Intent, aiCalled);
        var result = swapped.Value;
        return new BotAnswer(
            $"Đã đổi {result.FirstPlayerName} từ {result.FirstPreviousTeamName} sang {result.SecondPreviousTeamName}, " +
            $"và {result.SecondPlayerName} từ {result.SecondPreviousTeamName} sang {result.FirstPreviousTeamName}.\n" +
            FormatTeamLineup(session.Name, result.State.TeamPreview),
            teamCards.GetPublicUrl(session.Id),
            decision.Intent,
            aiCalled);
    }

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
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static PlayerProfileValues ParsePlayerProfileValues(string question, string playerReference)
    {
        var descriptor = NormalizeText(question);
        var normalizedName = NormalizeText(playerReference);
        var nameIndex = descriptor.IndexOf(normalizedName, StringComparison.Ordinal);
        if (nameIndex >= 0) descriptor = descriptor.Remove(nameIndex, normalizedName.Length);

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
        bool aiCalled)
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

        List<SessionSnapshot> targets;
        var hasExplicitSelector = HasExplicitSessionSelector(sessions, normalizedQuestion);
        if (hasExplicitSelector)
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
            if (targets.Count > 1)
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
        if (!hasExplicitSelector &&
            (command.Kind == ZaloReminderCommandKind.TriggerNow ||
             command.Kind == ZaloReminderCommandKind.Schedule && !command.Repeats))
        {
            targets = [targets.FirstOrDefault(session => session.PlayerCount < session.Capacity) ?? targets[0]];
        }

        if (command.Kind == ZaloReminderCommandKind.Status)
        {
            var statusLines = targets.Select(session =>
            {
                if (!session.ReminderEnabled) return $"- {session.Name}: đang tắt";
                var next = session.NextReminderAt is null
                    ? "đang chờ hệ thống tính lượt đầu"
                    : $"lần kiểm tra kế tiếp khoảng {FormatVietnamTime(session.NextReminderAt.Value)}";
                var repeat = session.ReminderRepeats
                    ? $", lặp mỗi {FormatDuration(session.ReminderIntervalMinutes)}"
                    : ", chỉ một lần";
                return $"- {session.Name}: {next}{repeat}";
            });
            return new BotAnswer(
                "Lịch nhắc hiện tại:\n" + string.Join("\n", statusLines) +
                "\nBot chỉ @all cho trận gần nhất còn thiếu slot; trận đã đủ sẽ được bỏ qua.",
                null,
                intent,
                aiCalled);
        }

        if (command.Kind == ZaloReminderCommandKind.Schedule && command.DelayMinutes is null)
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

        var targetNames = targets.Count == 1 ? targets[0].Name : $"{targets.Count} trận sắp tới";
        return command.Kind switch
        {
            ZaloReminderCommandKind.Disable => new BotAnswer(
                $"Đã tắt lịch nhắc cho {targetNames}.", null, intent, aiCalled),
            ZaloReminderCommandKind.TriggerNow => new BotAnswer(
                $"Đã xếp một lượt nhắc ngay cho {targetNames}. Trận đủ slot sẽ không bị tag; nếu nhiều trận thì bot ưu tiên trận gần nhất còn thiếu.",
                null,
                intent,
                aiCalled),
            _ => new BotAnswer(
                $"Đã lên lịch cho {targetNames}: lần đầu sau {FormatDuration(command.DelayMinutes!.Value)}" +
                (command.Repeats ? $", sau đó lặp mỗi {FormatDuration(command.DelayMinutes.Value)}." : ", chỉ nhắc một lần.") +
                (scheduleWasMovedForward ? " Có trận diễn ra trước mốc chờ nên bot sẽ kiểm tra trận đó ngay." : string.Empty) +
                " Bot chỉ @all cho trận gần nhất còn thiếu slot; trận đủ sẽ bỏ qua và tự xét lại nếu có người rút vote.",
                null,
                intent,
                aiCalled)
        };
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
    {
        if (teams.Count == 0 || teams.All(team => team.Slots.Count == 0))
            return $"{sessionName} chưa có kết quả chia team. Dùng lệnh 9 nếu bạn là operator và muốn tự chạy draft.";
        var sections = teams.Take(3).Select(team =>
        {
            var captain = string.IsNullOrWhiteSpace(team.CaptainName) ? "chưa chọn captain" : $"captain {team.CaptainName}";
            var players = team.Slots.Count == 0
                ? "chưa có thành viên"
                : string.Join(", ", team.Slots.Select(slot => slot.DisplayName));
            return $"{team.TeamName} ({captain}): {players}";
        });
        return $"Đội hình {sessionName}:\n" + string.Join("\n", sections);
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
            return new BotAnswer(
                FormatTeamLineup(teamSession.Name, state.Value.TeamPreview),
                decision.Intent == ZaloBotIntent.TeamImage ? teamCards.GetPublicUrl(teamSession.Id) : null,
                decision.Intent,
                true);
        }
        if (decision.Intent == ZaloBotIntent.UpdatePlayerProfile)
            return await UpdatePlayerProfileAsync(decision, sessions, selector, ExtractQuestion(incoming), incoming, true);
        if (decision.Intent == ZaloBotIntent.AddGuestPlayer)
            return await AddGuestPlayerAsync(decision, sessions, selector, ExtractQuestion(incoming), incoming, true);
        if (decision.Intent == ZaloBotIntent.ShareSlot)
            return await ShareSlotAsync(decision, sessions, selector, ExtractQuestion(incoming), incoming, true);
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
            var synced = await zaloIntegration.SyncLatestPollAsync(syncSession.AdminUserId, syncSession.Id, selector);
            if (!synced.IsSuccess || synced.Value is null)
                return new BotAnswer(synced.Error ?? "Không đồng bộ được poll.", null, decision.Intent, true);
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
        bool AiCalled = false);
    private sealed record SessionSelection(SessionSnapshot? Session, string? Clarification);
    private sealed record PendingResolution(bool Cancelled, ZaloBotIntent? Intent, SessionSnapshot? Session, string? Clarification)
    {
        public static PendingResolution None { get; } = new(false, null, null, null);
    }
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
        string? LatestPoll,
        IReadOnlyList<string> PlayerNames);
}
