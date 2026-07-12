using System.Globalization;
using System.Text;
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
        if (!IsOptionalHttpUrl(request.LocationImageUrl))
        {
            return ServiceResult<ZaloBotSettingsResponse>.Failure(StatusCodes.Status400BadRequest, "URL ảnh vị trí phải dùng http hoặc https.");
        }

        session.StartTime = request.StartTime;
        session.Location = Clean(request.Location, 500);
        session.ParkingInstructions = Clean(request.ParkingInstructions, 1000);
        session.LocationImageUrl = Clean(request.LocationImageUrl, 2048);
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

        var response = await BuildAnswerAsync(connectionIds, groupId, incoming, cancellationToken);
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
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var normalizedQuestion = NormalizeText(incoming.Content);
        var sessions = await LoadSessionSnapshotsAsync(connectionIds, groupId, incoming.SenderId, cancellationToken);
        if (sessions.Count == 0)
        {
            return new BotAnswer("Nhóm này chưa có trận nào đang bật bot. Bạn nhờ admin kiểm tra cấu hình nhé.", null);
        }

        if (HasAny(normalizedQuestion, "help", "tro giup", "huong dan", "lenh"))
        {
            return new BotAnswer(
                "Bạn có thể hỏi: “location” hoặc “vị trí”, “tui có trong danh sách không?”, “trận lúc mấy giờ?”, “còn thiếu bao nhiêu slot?”. Nếu có nhiều trận, hãy thêm ngày hoặc tên trận.",
                null);
        }

        if (HasAny(normalizedQuestion, "danh sach", "co ten", "co trong", "duoc vote", "da vote"))
        {
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
            return new BotAnswer(string.Join("; ", statuses) + ".", null);
        }

        if (HasAny(normalizedQuestion, "location", "vi tri", "dia diem", "o dau", "gui xe", "bai xe"))
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

        if (HasAny(normalizedQuestion, "may gio", "luc nao", "khi nao", "thoi gian", "tuan nay"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            return session.StartTime is null
                ? new BotAnswer($"admin chưa chốt giờ cho {session.Name}.", null)
                : new BotAnswer($"{session.Name} diễn ra lúc {FormatVietnamTime(session.StartTime.Value)}{FormatLocationSuffix(session)}.", null);
        }

        if (HasAny(normalizedQuestion, "thieu bao nhieu", "con bao nhieu", "bao nhieu slot", "du slot", "du nguoi"))
        {
            var selected = SelectSession(sessions, normalizedQuestion);
            if (selected.Clarification is not null) return new BotAnswer(selected.Clarification, null);
            var session = selected.Session!;
            var missing = Math.Max(0, session.Capacity - session.PlayerCount);
            return new BotAnswer(missing == 0
                ? $"{session.Name} đã đủ {session.Capacity} slot."
                : $"{session.Name} đang có {session.PlayerCount}/{session.Capacity}, còn thiếu {missing} slot.", null);
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
                session.LatestPoll)).ToList(),
            sessions.Select(session => session.CustomInstructions).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
        return new BotAnswer(await ai.AnswerAsync(aiContext, cancellationToken), null);
    }

    private async Task<List<SessionSnapshot>> LoadSessionSnapshotsAsync(
        IReadOnlyList<string> connectionIds,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId != null &&
                              connectionIds.Contains(session.ZaloConnectionId) &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .OrderBy(session => session.StartTime == null)
            .ThenBy(session => session.StartTime)
            .ThenByDescending(session => session.UpdatedAt)
            .ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(session => session.Id).ToList();
        var playerCounts = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent)
            .GroupBy(player => player.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SessionId, item => item.Count, cancellationToken);
        var senderSessionIds = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) &&
                             player.IsPresent &&
                             player.PlayerProfile != null &&
                             player.PlayerProfile.ZaloUserId == NormalizeId(senderId))
            .Select(player => player.SessionId)
            .ToHashSetAsync(cancellationToken);
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
            session.BotCustomInstructions,
            playerCounts.GetValueOrDefault(session.Id),
            session.TeamCount * session.TeamSize,
            senderSessionIds.Contains(session.Id),
            latestPolls.GetValueOrDefault(session.Id))).ToList();
    }

    private static SessionSelection SelectSession(IReadOnlyList<SessionSnapshot> sessions, string normalizedQuestion)
    {
        var upcoming = sessions.Where(IsUpcoming).ToList();
        if (upcoming.Count == 0) upcoming = sessions.Take(1).ToList();
        var explicitMatches = upcoming.Where(session => QuestionMatchesSession(normalizedQuestion, session)).ToList();
        if (explicitMatches.Count == 1) return new SessionSelection(explicitMatches[0], null);
        if (upcoming.Count == 1) return new SessionSelection(upcoming[0], null);

        var choices = upcoming.Take(4).Select(session =>
            session.StartTime is null ? session.Name : $"{session.Name} ({FormatVietnamTime(session.StartTime.Value)})");
        return new SessionSelection(null, $"bạn đang hỏi trận nào: {string.Join(", ", choices)}?");
    }

    private static bool QuestionMatchesSession(string question, SessionSnapshot session)
    {
        if (question.Contains(NormalizeText(session.Name), StringComparison.Ordinal)) return true;
        if (session.StartTime is null) return false;
        var local = session.StartTime.Value.ToOffset(VietnamOffset);
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
        return dayTokens.Any(token => question.Contains(token, StringComparison.Ordinal)) ||
               question.Contains(local.ToString("dd/MM", CultureInfo.InvariantCulture), StringComparison.Ordinal);
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
        string? CustomInstructions,
        int PlayerCount,
        int Capacity,
        bool SenderIsListed,
        string? LatestPoll);
}
