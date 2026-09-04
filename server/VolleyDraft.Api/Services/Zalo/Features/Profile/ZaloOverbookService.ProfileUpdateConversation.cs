using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloProfileUpdateConversationPayload(
    IReadOnlyList<string> SessionIds,
    string TargetZaloUserId,
    string TargetDisplayName,
    PlayerGender? Gender,
    PlayerRole? Role,
    PlayerLevel? Level,
    string SourceMessageId);

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Owns explicit profile mutations and their short session-selection follow-ups before
    /// read-only Match Brief or generic AI routing can consume the turn. The durable pending
    /// state stores typed mutation arguments; AI remains available for ordinary conversation,
    /// but it is never the memory or authority layer for a write operation.
    /// </summary>
    private async Task<bool> TryHandleProfileUpdateConversationAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var accountId = NormalizeProfileConversationId(incoming.AccountId);
        var groupId = NormalizeProfileConversationId(incoming.GroupId);
        var senderId = NormalizeProfileConversationId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return false;

        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session =>
                               session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new { item.Id, item.UpdatedAt })
            .ToListAsync(cancellationToken);
        var connectionId = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(connectionId)) return false;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.ZaloConnection)
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled &&
                              session.Status != SessionStatus.Finished)
            .ToListAsync(cancellationToken);
        sessions = sessions
            .Where(session => session.StartTime is null || session.StartTime >= now.AddHours(-4))
            .OrderBy(session => session.StartTime ?? DateTimeOffset.MaxValue)
            .ThenByDescending(session => session.UpdatedAt)
            .ToList();
        if (sessions.Count == 0) return false;

        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);
        if (state is not null && state.ExpiresAt <= now)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            state = null;
        }

        var freshDecision = ZaloBotIntelligence.ClassifyDeterministically(incoming.Content);
        if (state?.PendingIntent == ZaloBotIntent.UpdatePlayerProfile.ToString())
        {
            // A complete new profile command supersedes the old clarification just like
            // every other explicit new intent. Otherwise let the pending state interpret
            // only a genuine selector/cancel turn and never swallow unrelated chat.
            if (freshDecision.Intent == ZaloBotIntent.UpdatePlayerProfile && freshDecision.Confidence >= .85)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                state = null;
            }
            else
            {
                var disposition = ZaloPendingTurnPolicy.ClassifySessionTurn(
                    state.PendingIntent,
                    incoming.Content,
                    incoming.MentionedBot,
                    freshDecision.Intent == ZaloBotIntent.Unknown ? null : freshDecision.Intent.ToString(),
                    freshDecision.Confidence);
                switch (disposition)
                {
                    case ZaloPendingTurnDisposition.CancelPending:
                        db.ZaloBotConversationStates.Remove(state);
                        await db.SaveChangesAsync(cancellationToken);
                        await SendProfileUpdateReplyAsync(
                            sessions[0],
                            incoming,
                            "Ok, tui huỷ yêu cầu cập nhật hồ sơ đang chờ. Chưa có dữ liệu nào bị đổi.",
                            "cancelled",
                            cancellationToken);
                        return true;
                    case ZaloPendingTurnDisposition.SwitchToNewIntent:
                        db.ZaloBotConversationStates.Remove(state);
                        await db.SaveChangesAsync(cancellationToken);
                        return false;
                    case ZaloPendingTurnDisposition.IgnoreCurrentTurn:
                        return false;
                    case ZaloPendingTurnDisposition.ContinuePending:
                        return await ContinueProfileUpdateConversationAsync(
                            state,
                            sessions,
                            incoming,
                            cancellationToken);
                }
            }
        }

        if (freshDecision.Intent != ZaloBotIntent.UpdatePlayerProfile)
            return false;

        // An explicit profile mutation is a topic switch. Do not leave an unrelated
        // legacy pending state around to consume the next short answer after this turn.
        if (state is not null)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
        }

        var personMentions = incoming.Mentions
            .Where(mention => NormalizeProfileConversationId(mention.Uid) != NormalizeProfileConversationId(incoming.BotId))
            .GroupBy(mention => NormalizeProfileConversationId(mention.Uid), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (personMentions.Count == 0)
        {
            // Preserve name-only legacy behavior. This pre-route owns only stable
            // structured UID identity; generic routing may still resolve typed names.
            return false;
        }
        if (personMentions.Count != 1)
        {
            await SendProfileUpdateReplyAsync(
                sessions[0],
                incoming,
                "Mỗi lần chỉ cập nhật một người. Hãy @mention đúng một thành viên rồi gửi lại.",
                "multiple_targets",
                cancellationToken);
            return true;
        }

        var targetMention = personMentions[0];
        var targetUid = NormalizeProfileConversationId(targetMention.Uid);
        var targetDisplay = ExtractProfileMentionLabel(incoming.Content, targetMention);
        if (targetDisplay.Length == 0) targetDisplay = "thành viên được tag";

        var parsed = ParseExplicitProfileUpdateValues(incoming.Content);
        if (parsed.HasConflict)
        {
            await SendProfileUpdateReplyAsync(
                sessions[0],
                incoming,
                "Tui thấy có hơn một giá trị cùng loại trong lệnh cập nhật nên chưa dám ghi. Mỗi lần chọn một giới tính, một vị trí và một trình độ nha.",
                "profile_conflict",
                cancellationToken);
            return true;
        }
        if (parsed.Gender is null && parsed.Role is null && parsed.Level is null)
        {
            await SendProfileUpdateReplyAsync(
                sessions[0],
                incoming,
                "Tui chưa nhận ra thông tin hồ sơ. Dùng nam/nữ; công/thủ/chuyền 2/toàn diện; trình độ mới/trung bình/tốt.",
                "profile_values_missing",
                cancellationToken);
            return true;
        }

        var targetRows = await LoadProfileUpdateTargetsAsync(sessions, targetUid, cancellationToken);
        if (targetRows.Count == 0)
        {
            await SendProfileUpdateReplyAsync(
                sessions[0],
                incoming,
                $"Tui nhận đúng UID của {targetDisplay} nhưng người này không nằm trong roster các trận đang bật bot, nên chưa cập nhật gì.",
                "target_not_in_roster",
                cancellationToken);
            return true;
        }
        var duplicateSession = targetRows
            .GroupBy(row => row.SessionId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSession is not null)
        {
            var conflictedSession = sessions.First(item => item.Id == duplicateSession.Key);
            await SendProfileUpdateReplyAsync(
                conflictedSession,
                incoming,
                $"UID của {targetDisplay} đang trỏ tới nhiều player trong {conflictedSession.Name}. Tui chặn cập nhật để tránh ghi nhầm người.",
                "identity_conflict",
                cancellationToken);
            return true;
        }

        var targetSessionIds = targetRows.Select(row => row.SessionId).ToHashSet(StringComparer.Ordinal);
        var targetSessions = sessions.Where(session => targetSessionIds.Contains(session.Id)).ToList();
        var authorizedSessions = new List<MatchSession>();
        foreach (var session in targetSessions)
        {
            if (await CanOperateProfileUpdateAsync(session, senderId))
                authorizedSessions.Add(session);
        }
        if (authorizedSessions.Count == 0)
        {
            await SendProfileUpdateReplyAsync(
                targetSessions[0],
                incoming,
                "Lệnh này sửa hồ sơ của người khác nên chỉ admin/trưởng nhóm/phó nhóm hoặc UID được cấp quyền bot mới thực hiện được.",
                "unauthorized",
                cancellationToken);
            return true;
        }

        var references = authorizedSessions
            .Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime))
            .ToList();
        var matchedIds = ZaloConversationCore.ResolveSessionReference(incoming.Content, references, now);
        var selectedSessions = authorizedSessions
            .Where(session => matchedIds.Contains(session.Id, StringComparer.Ordinal))
            .ToList();
        if (selectedSessions.Count == 0 && authorizedSessions.Count == 1)
            selectedSessions = [authorizedSessions[0]];

        var payload = new ZaloProfileUpdateConversationPayload(
            authorizedSessions.Select(session => session.Id).ToList(),
            targetUid,
            targetDisplay,
            parsed.Gender,
            parsed.Role,
            parsed.Level,
            incoming.MessageId);

        if (selectedSessions.Count == 1)
        {
            return await ApplyProfileUpdateConversationAsync(
                selectedSessions[0],
                payload,
                incoming,
                cancellationToken);
        }

        await SaveProfileUpdateConversationAsync(
            connectionId,
            groupId,
            senderId,
            payload,
            cancellationToken);
        var choices = string.Join(", ", authorizedSessions.Take(4).Select(FormatProfileSessionChoice));
        await SendProfileUpdateReplyAsync(
            authorizedSessions[0],
            incoming,
            $"Tui nhớ lệnh cập nhật {targetDisplay} rồi. Ông muốn áp dụng cho trận nào: {choices}? Chỉ cần trả lời T6/T7/CN, thứ, ngày hoặc tên trận; không cần gõ lại lệnh.",
            "session_clarification",
            cancellationToken);
        return true;
    }

    private async Task<bool> ContinueProfileUpdateConversationAsync(
        ZaloBotConversationState state,
        IReadOnlyList<MatchSession> sessions,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        ZaloProfileUpdateConversationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ZaloProfileUpdateConversationPayload>(state.PendingPayloadJson);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null || payload.SessionIds.Count == 0)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var candidates = sessions
            .Where(session => payload.SessionIds.Contains(session.Id, StringComparer.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var matchedIds = ZaloConversationCore.ResolveSessionReference(
            incoming.Content,
            candidates.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList(),
            DateTimeOffset.UtcNow);
        var matches = candidates.Where(session => matchedIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            var choices = string.Join(", ", candidates.Take(4).Select(FormatProfileSessionChoice));
            await SendProfileUpdateReplyAsync(
                candidates[0],
                incoming,
                $"Tui vẫn nhớ đang cập nhật {payload.TargetDisplayName}, nhưng chưa xác định đúng một trận. Trả lời bằng T6/T7/CN, thứ, ngày hoặc tên trận: {choices}; hoặc gõ huỷ.",
                "session_still_ambiguous",
                cancellationToken);
            return true;
        }

        // Re-check authority and identity at execution time. Pending state carries
        // intent arguments, never permission or a stale player object.
        var selected = matches[0];
        if (!await CanOperateProfileUpdateAsync(selected, incoming.SenderId))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            await SendProfileUpdateReplyAsync(
                selected,
                incoming,
                "Quyền thao tác của ông không còn hợp lệ cho trận này nên tui dừng cập nhật. Dữ liệu chưa bị đổi.",
                "authorization_changed",
                cancellationToken);
            return true;
        }

        db.ZaloBotConversationStates.Remove(state);
        await db.SaveChangesAsync(cancellationToken);
        return await ApplyProfileUpdateConversationAsync(selected, payload, incoming, cancellationToken);
    }

    private async Task<bool> ApplyProfileUpdateConversationAsync(
        MatchSession session,
        ZaloProfileUpdateConversationPayload payload,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var currentRows = await LoadProfileUpdateTargetsAsync([session], payload.TargetZaloUserId, cancellationToken);
        if (currentRows.Count != 1)
        {
            await SendProfileUpdateReplyAsync(
                session,
                incoming,
                currentRows.Count == 0
                    ? $"{payload.TargetDisplayName} không còn nằm trong roster {session.Name}, nên tui không cập nhật dữ liệu cũ."
                    : $"UID của {payload.TargetDisplayName} đang trỏ tới nhiều player trong {session.Name}; tui chặn cập nhật để tránh ghi nhầm.",
                "target_changed",
                cancellationToken);
            return true;
        }

        var target = currentRows[0];
        var history = new ZaloBotActionHistoryService(db, NullLogger<ZaloBotActionHistoryService>.Instance);
        var before = await history.CaptureAsync(session.Id, cancellationToken);
        var updated = await new SessionDraftService(db).UpdatePlayerProfileFromBotAsync(
            session.AdminUserId,
            session.Id,
            target.DisplayName,
            payload.Gender,
            payload.Role,
            payload.Level,
            payload.TargetZaloUserId,
            target.SessionPlayerId);
        if (!updated.IsSuccess || updated.Value is null)
        {
            await SendProfileUpdateReplyAsync(
                session,
                incoming,
                updated.Error ?? "Backend vừa chặn cập nhật hồ sơ để giữ an toàn dữ liệu. Tui chưa thay đổi gì.",
                "backend_rejected",
                cancellationToken);
            return true;
        }

        var player = updated.Value;
        await history.RecordAsync(
            session.Id,
            incoming.SenderId,
            incoming.SenderName,
            "UpdatePlayerProfile",
            $"Cập nhật hồ sơ {player.DisplayName} trong {session.Name} qua hội thoại Zalo",
            before,
            cancellationToken);
        var remaining = await new SessionDraftService(db).GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
        var remainingText = remaining.IsSuccess && remaining.Value is { Count: > 0 }
            ? $" Còn hồ sơ thiếu: {string.Join(", ", remaining.Value.Take(10).Select(item => item.DisplayName))}."
            : " Hồ sơ trận này đã đủ điều kiện để draft.";
        await SendProfileUpdateReplyAsync(
            session,
            incoming,
            $"Đã cập nhật {player.DisplayName} cho {session.Name}: {FormatProfileGender(player.Gender)}, {FormatProfileRole(player.Role)}, {FormatProfileLevel(player.Level)}.{remainingText}",
            "updated",
            cancellationToken);
        return true;
    }

    private async Task<List<ProfileUpdateTargetRow>> LoadProfileUpdateTargetsAsync(
        IReadOnlyList<MatchSession> sessions,
        string targetZaloUserId,
        CancellationToken cancellationToken)
    {
        var sessionIds = sessions.Select(session => session.Id).ToList();
        var rows = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent && player.PlayerProfile != null)
            .Select(player => new ProfileUpdateTargetRow(
                player.SessionId,
                player.Id,
                player.DisplayName,
                player.PlayerProfile!.ZaloUserId))
            .ToListAsync(cancellationToken);
        var normalizedTarget = NormalizeProfileConversationId(targetZaloUserId);
        return rows
            .Where(row => NormalizeProfileConversationId(row.ZaloUserId) == normalizedTarget)
            .ToList();
    }

    private async Task<bool> CanOperateProfileUpdateAsync(MatchSession session, string senderId)
    {
        var normalizedSender = NormalizeProfileConversationId(senderId);
        if (ParseStringList(session.BotOperatorZaloUserIdsJson)
            .Select(NormalizeProfileConversationId)
            .Contains(normalizedSender, StringComparer.Ordinal))
            return true;

        var role = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            normalizedSender);
        return role.IsSuccess && role.Value?.CanOperateBot == true;
    }

    private async Task SaveProfileUpdateConversationAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloProfileUpdateConversationPayload payload,
        CancellationToken cancellationToken)
    {
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = senderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = ZaloBotIntent.UpdatePlayerProfile.ToString();
        state.PendingPayloadJson = JsonSerializer.Serialize(payload);
        state.PreviousCommand = ZaloBotIntent.UpdatePlayerProfile.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15),
            1,
            120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SendProfileUpdateReplyAsync(
        MatchSession session,
        ZaloIncomingMessageEvent incoming,
        string text,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId)) return;
        var key = $"profile-update-conversation:{incoming.MessageId}:{outcome}";
        var send = await bridge.SendGroupMessageAsync(
            session.ZaloConnection.AccountZaloId,
            session.ZaloGroupId,
            text,
            [],
            idempotencyKey: key);
        if (!send.Sent)
            throw new InvalidOperationException("Zalo bridge did not confirm profile-update conversation send.");
        var messageId = string.IsNullOrWhiteSpace(send.MessageId) ? key : send.MessageId!;
        await SaveBotMessageAsync(session, messageId, text, DateTimeOffset.UtcNow, cancellationToken);
    }

    private static ZaloNaturalProfileValues ParseExplicitProfileUpdateValues(string? content)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(
            content,
            missingGender: true,
            missingRole: true,
            missingLevel: true,
            repliedToPrompt: true);
        if (parsed.Role is null && !parsed.HasConflict && ZaloFlexibleProfileReplySemantics.AcceptsAnyPosition(content))
        {
            parsed = parsed with
            {
                Role = PlayerRole.FullStack,
                HasRecognizedValue = true,
                LooksLikeProfileAnswer = true
            };
        }
        return parsed;
    }

    private static string ExtractProfileMentionLabel(string? content, ZaloBridgeMention mention)
    {
        var value = content ?? string.Empty;
        if (mention.Pos >= 0 && mention.Len > 0 && mention.Pos + mention.Len <= value.Length)
            return value.Substring(mention.Pos, mention.Len).Trim().TrimStart('@');
        return string.Empty;
    }

    private static string NormalizeProfileConversationId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string FormatProfileSessionChoice(MatchSession session)
    {
        if (session.StartTime is null) return session.Name;
        var local = session.StartTime.Value.ToOffset(TimeSpan.FromHours(7));
        return $"{session.Name} {local:dd/MM HH:mm}";
    }

    private static string FormatProfileGender(PlayerGender gender) => gender switch
    {
        PlayerGender.Male => "nam",
        PlayerGender.Female => "nữ",
        _ => "chưa rõ giới tính"
    };

    private static string FormatProfileRole(PlayerRole role) => role switch
    {
        PlayerRole.Attack => "công",
        PlayerRole.Defense => "thủ",
        PlayerRole.Setter => "chuyền 2",
        PlayerRole.FullStack => "toàn diện",
        _ => "người mới"
    };

    private static string FormatProfileLevel(PlayerLevel level) => level switch
    {
        PlayerLevel.Good => "tốt",
        PlayerLevel.Average => "trung bình",
        _ => "mới"
    };

    private sealed record ProfileUpdateTargetRow(
        string SessionId,
        string SessionPlayerId,
        string DisplayName,
        string? ZaloUserId);
}
