using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBotService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    AiAssistantService ai,
    ZaloListenerCoordinator listenerCoordinator)
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

        session.StartTime = request.StartTime;
        session.Location = Clean(request.Location, 500);
        session.ParkingInstructions = Clean(request.ParkingInstructions, 1000);
        session.LocationImageUrl = Clean(request.LocationImageUrl, 2048);
        session.PaymentInstructions = Clean(request.PaymentInstructions, 1000);
        session.PaymentQrImageUrl = Clean(request.PaymentQrImageUrl, 2048);
        session.BotEnabled = request.BotEnabled;
        session.BotCustomInstructions = Clean(request.BotCustomInstructions, 2000);
        session.ReminderEnabled = request.ReminderEnabled;
        session.ReminderLeadHours = Math.Clamp(request.ReminderLeadHours, 1, 336);
        session.ReminderIntervalHours = Math.Clamp(request.ReminderIntervalHours, 1, 168);
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
        return ServiceResult<ZaloBotSettingsResponse>.Success(ToSettings(session));
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
            await db.SaveChangesAsync(cancellationToken);
        }

        var explicitlyMentioned = incoming.MentionedBot && incoming.Mentions.Any(mention =>
            NormalizeId(mention.Uid) == NormalizeId(incoming.BotId));
        if (!explicitlyMentioned || storedMessage.BotReplySentAt is not null) return;

        storedMessage.ReplyAttemptCount += 1;
        await db.SaveChangesAsync(cancellationToken);

        var response = await BuildAnswerAsync(connectionIds, connection.Id, groupId, incoming, cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Text)) return;

        var senderName = (Clean(incoming.SenderName, 50) ?? "bạn").TrimStart('@');
        var mentionLabel = $"@{senderName}";
        var reply = $"{mentionLabel} {response.Text.Trim()}";
        await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            groupId,
            reply,
            [new BridgeOutgoingMention(NormalizeId(incoming.SenderId), 0, mentionLabel.Length)],
            response.ImageUrl);

        storedMessage.BotReplySentAt = DateTimeOffset.UtcNow;

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
        var intent = DetectIntent(question, normalizedQuestion);

        if (intent == BotIntent.LearnRule)
        {
            if (!TryParseLearningCommand(question, out var trigger, out var answer))
            {
                return new BotAnswer("Cú pháp dạy bot chưa đúng. Dùng: @bot học: câu hỏi => câu trả lời", null);
            }
            return await SaveLearnedRuleAsync(
                activeConnectionId,
                groupId,
                incoming,
                trigger,
                answer,
                cancellationToken);
        }

        if (intent == BotIntent.ForgetRule)
        {
            if (!TryParseForgetCommand(question, out var forgottenTrigger))
            {
                return new BotAnswer("Cú pháp xoá ghi nhớ chưa đúng. Dùng: @bot quên: câu hỏi", null);
            }
            return await ForgetLearnedRuleAsync(
                activeConnectionId,
                groupId,
                forgottenTrigger,
                cancellationToken);
        }

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

        if (intent == BotIntent.TrainingHelp)
        {
            return new BotAnswer(
                "Hiện tại bot không tự học chỉ vì bạn đặt câu hỏi. Bot có 2 loại dữ liệu:\n- Dữ liệu trận, sân, giờ, slot và danh sách: lấy trực tiếp từ hệ thống.\n- Ghi nhớ do thành viên dạy rõ ràng trong group.\n\nDạy bot: @bot học: câu hỏi => câu trả lời\nVí dụ: @bot học: ai đẹp trai nhất nhóm => Thanh Long 😄\nSửa: @bot sửa: câu hỏi => câu trả lời\nXoá: @bot quên: câu hỏi",
                null);
        }

        var sessions = await LoadSessionSnapshotsAsync(connectionIds, groupId, incoming.SenderId, cancellationToken);
        if (sessions.Count == 0)
        {
            return new BotAnswer("Nhóm này chưa có trận nào đang bật bot. Bạn nhờ admin kiểm tra cấu hình nhé.", null);
        }

        var learnedRules = await LoadLearnedRulesAsync(connectionIds, groupId, cancellationToken);

        if (HasAny(normalizedQuestion, "help", "tro giup", "huong dan", "lenh"))
        {
            return new BotAnswer(
                "🤖 Menu bot:\n1. Xem giờ và địa điểm trận\n2. Kiểm tra mình có trong danh sách\n3. Xem vị trí và hướng dẫn gửi xe\n4. Xem còn thiếu bao nhiêu slot\n5. Xem các trận sắp tới\n6. Xem QR và hướng dẫn thanh toán\n\nGõ @bot + số, ví dụ: @bot 3 hoặc @bot 6. Nếu có nhiều trận, hãy thêm ngày hoặc tên trận.\n\nDạy bot: @bot học: câu hỏi => câu trả lời\nSửa: @bot sửa: câu hỏi => câu trả lời\nXoá: @bot quên: câu hỏi",
                null);
        }

        if (IsCommand(normalizedQuestion, "5"))
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
                : "Các trận sắp tới:\n" + string.Join("\n", lines), null);
        }

        if (IsCommand(normalizedQuestion, "6") || HasAny(normalizedQuestion, "qr thanh toan", "ma qr", "chuyen khoan", "thanh toan o dau"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (string.IsNullOrWhiteSpace(session.PaymentQrImageUrl))
            {
                return new BotAnswer($"admin chưa cấu hình ảnh QR thanh toán cho {session.Name}.", null);
            }
            var instructions = string.IsNullOrWhiteSpace(session.PaymentInstructions)
                ? $"QR thanh toán cho {session.Name}. Khi chuyển khoản nhớ ghi tên Zalo của bạn nhé."
                : $"{session.Name} — {session.PaymentInstructions}";
            return new BotAnswer(instructions, session.PaymentQrImageUrl);
        }

        if (IsRosterQuestion(normalizedQuestion))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (session.PlayerNames.Count == 0)
            {
                return new BotAnswer($"{session.Name} hiện chưa có ai trong danh sách.", null);
            }
            var players = session.PlayerNames.Select((name, index) => $"{index + 1}. {name}");
            return new BotAnswer(
                $"Danh sách {session.Name} ({session.PlayerCount}/{session.Capacity}):\n{string.Join("\n", players)}",
                null);
        }

        if (IsSelfMembershipQuestion(normalizedQuestion) || IsCommand(normalizedQuestion, "2"))
        {
            if (HasExplicitSessionSelector(sessions, normalizedQuestion))
            {
                var selected = SelectSession(sessions, normalizedQuestion);
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

        if (HasAny(normalizedQuestion, "location", "vi tri", "dia diem", "o dau", "gui xe", "bai xe") || IsCommand(normalizedQuestion, "3"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            if (string.IsNullOrWhiteSpace(session.Location) && string.IsNullOrWhiteSpace(session.ParkingInstructions))
            {
                return new BotAnswer($"admin chưa cấu hình vị trí và chỗ gửi xe cho mình.", null);
            }
            var parts = new List<string> { session.Name };
            if (!string.IsNullOrWhiteSpace(session.Location)) parts.Add($"địa điểm: {session.Location}");
            if (!string.IsNullOrWhiteSpace(session.ParkingInstructions)) parts.Add($"gửi xe: {session.ParkingInstructions}");
            return new BotAnswer(string.Join(" — ", parts) + ".", session.LocationImageUrl);
        }

        if (HasAny(normalizedQuestion, "may gio", "luc nao", "khi nao", "thoi gian", "tuan nay") || IsCommand(normalizedQuestion, "1"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            return session.StartTime is null
                ? new BotAnswer($"admin chưa chốt giờ cho {session.Name}.", null)
                : new BotAnswer($"{session.Name} diễn ra lúc {FormatVietnamTime(session.StartTime.Value)}{FormatLocationSuffix(session)}.", null);
        }

        if (HasAny(normalizedQuestion, "thieu bao nhieu", "con bao nhieu", "bao nhieu slot", "du slot", "du nguoi") || IsCommand(normalizedQuestion, "4"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            var missing = Math.Max(0, session.Capacity - session.PlayerCount);
            return new BotAnswer(missing == 0
                ? $"{session.Name} đã đủ {session.Capacity} slot."
                : $"{session.Name} đang có {session.PlayerCount}/{session.Capacity}, còn thiếu {missing} slot.", null);
        }

        if (HasAny(normalizedQuestion, "quota token", "gioi han token", "gioi han model", "model nao", "model gi"))
        {
            return new BotAnswer(ai.GetPublicModelInfo(), null);
        }

        var learnedRule = learnedRules.FirstOrDefault(rule =>
            rule.NormalizedTrigger == NormalizeRuleText(question));
        if (learnedRule is not null)
        {
            return new BotAnswer(RenderLearnedAnswer(learnedRule.Answer, incoming.SenderName), null);
        }

        var recentMessages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message => connectionIds.Contains(message.ZaloConnectionId) && message.GroupId == groupId)
            .OrderByDescending(message => message.SentAt)
            .Take(20)
            .OrderBy(message => message.SentAt)
            .Select(message => $"{message.SenderName}: {message.Content}")
            .ToListAsync(cancellationToken);
        var aiContext = new ZaloAiContext(
            groupId,
            new ZaloAiSender(NormalizeId(incoming.SenderId), incoming.SenderName),
            incoming.Content,
            recentMessages,
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
        return new BotAnswer(await ai.AnswerAsync(aiContext, cancellationToken), null);
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
            session.StartTime,
            session.Location,
            session.ParkingInstructions,
            session.LocationImageUrl,
            session.PaymentInstructions,
            session.PaymentQrImageUrl,
            session.BotCustomInstructions,
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

    private static bool IsRosterQuestion(string question) =>
        (question.Contains("danh sach", StringComparison.Ordinal) && !IsSelfMembershipQuestion(question)) ||
        HasAny(question,
            "danh sach doi hinh",
            "lay danh sach",
            "gui danh sach",
            "xem danh sach",
            "danh sach hom nay",
            "danh sach bua nay",
            "danh sach ngay",
            "danh sach cn",
            "danh sach chu nhat",
            "co nhung ai",
            "ai tham gia",
            "ai danh");

    private static bool IsTrainingQuestion(string question) =>
        HasAny(question,
            "lam sao train",
            "cach train",
            "train ban",
            "train bot",
            "tu train",
            "dang train",
            "day bot",
            "day ban",
            "dang hoc",
            "hoc thong qua",
            "thong qua user",
            "user dang hoi",
            "van hardcode",
            "hardcode",
            "tu dong hoc",
            "hoc theo",
            "tu hoc",
            "bot hoc nhu the nao",
            "hoc nhu the nao",
            "ghi nho nhu the nao",
            "bot co nho khong",
            "sua cau tra loi");

    private static BotIntent DetectIntent(string question, string normalizedQuestion)
    {
        if (HasLearningCommandPrefix(question)) return BotIntent.LearnRule;
        if (HasForgetCommandPrefix(question)) return BotIntent.ForgetRule;
        if (IsTrainingQuestion(normalizedQuestion)) return BotIntent.TrainingHelp;
        return BotIntent.General;
    }

    private static bool HasLearningCommandPrefix(string question) =>
        Regex.IsMatch(
            question,
            @"^\s*(?:học|hoc|dạy|day|train|sửa|sua|ghi nhớ|ghi nho|learn)\s*[:：]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasForgetCommandPrefix(string question) =>
        Regex.IsMatch(
            question,
            @"^\s*(?:quên|quen|forget|xóa|xoa)\s*[:：]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task<List<ZaloBotLearnedRule>> LoadLearnedRulesAsync(
        IReadOnlyList<string> connectionIds,
        string groupId,
        CancellationToken cancellationToken)
    {
        return await db.ZaloBotLearnedRules
            .AsNoTracking()
            .Where(rule => connectionIds.Contains(rule.ZaloConnectionId) && rule.GroupId == groupId)
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
            return new BotAnswer("Cú pháp dạy bot chưa đúng. Dùng: @bot học: câu hỏi => câu trả lời", null);
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new BotAnswer($"Đã ghi nhớ cho group: khi hỏi “{cleanTrigger}” bot sẽ trả lời theo nội dung bạn vừa dạy.", null);
    }

    private async Task<BotAnswer> ForgetLearnedRuleAsync(
        string connectionId,
        string groupId,
        string trigger,
        CancellationToken cancellationToken)
    {
        var normalizedTrigger = NormalizeRuleText(trigger);
        var existing = await db.ZaloBotLearnedRules.SingleOrDefaultAsync(rule =>
            rule.ZaloConnectionId == connectionId &&
            rule.GroupId == groupId &&
            rule.NormalizedTrigger == normalizedTrigger,
            cancellationToken);
        if (existing is null)
        {
            return new BotAnswer("Mình chưa có ghi nhớ nào khớp câu đó.", null);
        }

        db.ZaloBotLearnedRules.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return new BotAnswer($"Đã quên ghi nhớ “{existing.Trigger}” trong group.", null);
    }

    private static bool TryParseLearningCommand(string question, out string trigger, out string answer)
    {
        var match = Regex.Match(
            question,
            @"^\s*(?:học|hoc|dạy|day|train|sửa|sua|ghi nhớ|ghi nho|learn)\s*[:：]\s*(?<trigger>.+?)\s*(?:=>|->)\s*(?<answer>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        trigger = match.Success ? match.Groups["trigger"].Value : string.Empty;
        answer = match.Success ? match.Groups["answer"].Value : string.Empty;
        return match.Success;
    }

    private static bool TryParseForgetCommand(string question, out string trigger)
    {
        var match = Regex.Match(
            question,
            @"^\s*(?:quên|quen|forget|xóa|xoa)\s*[:：]\s*(?<trigger>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        trigger = match.Success ? match.Groups["trigger"].Value.Trim(' ', '"', '\'', '“', '”', '‘', '’') : string.Empty;
        return match.Success && !string.IsNullOrWhiteSpace(trigger);
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

    private static bool IsSelfMembershipQuestion(string question) =>
        HasAny(question,
            "minh co trong",
            "tui co trong",
            "toi co trong",
            "em co trong",
            "minh co ten",
            "tui co ten",
            "toi co ten",
            "em co ten",
            "co ten minh",
            "co ten tui",
            "co ten toi",
            "co ten em",
            "duoc vote",
            "da vote");

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

    private static bool IsCommand(string value, string command)
    {
        if (value.Equals(command, StringComparison.Ordinal)) return true;
        if (!value.StartsWith(command + " ", StringComparison.Ordinal)) return false;
        var remainder = value[(command.Length + 1)..].TrimStart();
        return !Regex.IsMatch(
            remainder,
            @"^(?:[+\-*/x×=]|cong(?:\s|$)|tru(?:\s|$)|nhan(?:\s|$)|chia(?:\s|$))",
            RegexOptions.CultureInvariant);
    }

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

    private enum BotIntent
    {
        General,
        TrainingHelp,
        LearnRule,
        ForgetRule
    }

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
        session.ReminderEnabled,
        session.ReminderLeadHours,
        session.ReminderIntervalHours,
        session.LastReminderAt);

    private sealed record BotAnswer(string Text, string? ImageUrl);
    private sealed record SessionSelection(SessionSnapshot? Session, string? Clarification);
    private sealed record SessionSnapshot(
        string Id,
        string Name,
        DateTimeOffset? StartTime,
        string? Location,
        string? ParkingInstructions,
        string? LocationImageUrl,
        string? PaymentInstructions,
        string? PaymentQrImageUrl,
        string? CustomInstructions,
        int PlayerCount,
        int Capacity,
        bool SenderIsListed,
        string? LatestPoll,
        IReadOnlyList<string> PlayerNames);
}
