using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Operator-confirmed recovery for a stable Zalo UID whose stored display label is stale.
/// The flow is intentionally explicit: a mention supplies the stable UID, the bot shows the
/// old/new labels, and an authorized operator chooses whether to keep or rename it.
/// No UID is ever rebound from one profile to another in this workflow.
/// </summary>
public sealed class ZaloIdentityCorrectionConversation(
    VolleyDraftDbContext db,
    ZaloIntegrationService? integration = null)
{
    internal const string PendingIntent = "IdentityCorrectionChoiceV1";

    public async Task<ZaloIdentityCorrectionConversationResult> TryHandleAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var senderId = NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return ZaloIdentityCorrectionConversationResult.NotHandled;

        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new { item.Id, item.UpdatedAt })
            .ToListAsync(cancellationToken);
        var connection = connectionRows.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (connection is null) return ZaloIdentityCorrectionConversationResult.NotHandled;

        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connection.Id &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);

        var question = ZaloBotService.ExtractQuestion(incoming);
        var correctionCommand = IsCorrectionCommand(question);

        if (state is not null && state.PendingIntent == PendingIntent)
        {
            if (state.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                state = null;
            }
            else if (incoming.MentionedBot && TryParseChoice(question, out var choice))
            {
                return await HandleChoiceAsync(
                    connection.Id,
                    groupId,
                    senderId,
                    incoming,
                    state,
                    choice,
                    cancellationToken);
            }
            else if (correctionCommand)
            {
                db.ZaloBotConversationStates.Remove(state);
                await db.SaveChangesAsync(cancellationToken);
                state = null;
            }
            else if (incoming.MentionedBot)
            {
                var freshIntent = ZaloBotIntelligence.ClassifyDeterministically(
                    ZaloBotIntelligence.Normalize(question)).Intent;
                if (freshIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))
                {
                    db.ZaloBotConversationStates.Remove(state);
                    await db.SaveChangesAsync(cancellationToken);
                    return ZaloIdentityCorrectionConversationResult.NotHandled;
                }

                var payload = Deserialize(state.PendingPayloadJson);
                return payload is null
                    ? ZaloIdentityCorrectionConversationResult.NotHandled
                    : Handled(FormatChoices(payload));
            }
        }

        if (!incoming.MentionedBot || !correctionCommand)
            return ZaloIdentityCorrectionConversationResult.NotHandled;

        var mentioned = ExtractSingleMention(incoming);
        if (mentioned is null)
        {
            return Handled(
                "Để sửa identity an toàn, hãy @mention đúng một người. Ví dụ: `@Npc sửa identity @Thanh Tuyền`. Bot chỉ dùng UID thật từ @mention, không đoán theo tên gõ tay.");
        }

        var profile = await db.PlayerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ZaloUserId == mentioned.Value.ZaloUserId, cancellationToken);
        if (profile is null)
        {
            return Handled(
                $"UID của @{mentioned.Value.DisplayName} chưa gắn với hồ sơ nào, nên không có identity cũ để sửa. Hãy gửi lại yêu cầu share slot và @mention đúng người; bot sẽ giữ UID này làm định danh.");
        }

        if (SameName(profile.DisplayName, mentioned.Value.DisplayName))
        {
            return Handled(
                $"Identity của @{mentioned.Value.DisplayName} đã đúng theo UID này rồi. Không có gì cần sửa; bạn có thể gửi lại yêu cầu share slot.");
        }

        var payloadToSave = new ZaloIdentityCorrectionPayload(
            profile.Id,
            mentioned.Value.ZaloUserId,
            profile.DisplayName,
            mentioned.Value.DisplayName);
        state ??= new ZaloBotConversationState
        {
            ZaloConnectionId = connection.Id,
            GroupId = groupId,
            SenderZaloUserId = senderId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (db.Entry(state).State == EntityState.Detached)
            db.ZaloBotConversationStates.Add(state);
        else if (state.Id is null || !db.ZaloBotConversationStates.Local.Contains(state))
            db.ZaloBotConversationStates.Add(state);

        state.PendingIntent = PendingIntent;
        state.PendingPayloadJson = JsonSerializer.Serialize(payloadToSave);
        state.PreviousCommand = "IdentityCorrection";
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return Handled(FormatChoices(payloadToSave));
    }

    internal static bool TryParseChoice(string? question, out int choice)
    {
        var normalized = ZaloBotIntelligence.Normalize(question ?? string.Empty).Trim(' ', '.', ',', ':', ';');
        choice = normalized switch
        {
            "1" or "giu" or "giu cu" or "giu ten cu" => 1,
            "2" or "doi" or "sua" or "doi identity" or "sua identity" => 2,
            "3" or "huy" or "bo qua" => 3,
            _ => 0
        };
        return choice != 0;
    }

    private async Task<ZaloIdentityCorrectionConversationResult> HandleChoiceAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloBotConversationState state,
        int choice,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize(state.PendingPayloadJson);
        if (payload is null)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return Handled("Yêu cầu sửa identity cũ không còn đọc được. Hãy gửi lại `@Npc sửa identity @TênĐúng`.");
        }

        if (choice == 3)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return Handled("Đã huỷ sửa identity. Không có UID, tên hồ sơ hay share slot nào bị thay đổi.");
        }

        if (choice == 1)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return Handled(
                $"Giữ nguyên identity `{payload.StoredDisplayName}` cho UID này. Không đổi dữ liệu. Nếu bạn muốn share với người khác, hãy @mention lại đúng tài khoản rồi gửi lại lệnh share slot.");
        }

        var authorization = await CanCorrectIdentityAsync(
            connectionId,
            groupId,
            senderId,
            cancellationToken);
        if (authorization != true)
        {
            var reason = authorization is null
                ? "Bot chưa xác minh được quyền quản trị Zalo lúc này."
                : "Bạn không có quyền đổi identity của thành viên khác.";
            return Handled(
                $"{reason} Lựa chọn 2 chỉ dành cho trưởng/phó nhóm hoặc UID operator/admin đã cấu hình. " +
                $"Pending vẫn còn: `@Npc 1` để giữ `{payload.StoredDisplayName}`, `@Npc 2` để thử lại sau khi có quyền, hoặc `@Npc 3` để huỷ.");
        }

        var applied = await ApplyRenameAsync(
            connectionId,
            groupId,
            payload,
            cancellationToken);
        if (!applied.IsSuccess)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return Handled(applied.Error ?? "Không sửa được identity; dữ liệu được giữ nguyên.");
        }

        db.ZaloBotConversationStates.Remove(state);
        await db.SaveChangesAsync(cancellationToken);
        var result = applied.Value!;
        return Handled(
            result.AlreadyApplied
                ? $"Identity UID này đã là `{result.NewDisplayName}` rồi. Bạn có thể gửi lại yêu cầu share slot."
                : $"Đã sửa identity ngay trên Zalo: `{result.OldDisplayName}` → `{result.NewDisplayName}` cho đúng UID đã @mention. " +
                  "UID không bị đổi hay chuyển sang hồ sơ khác. Giờ gửi lại yêu cầu share slot và @mention người này; bot sẽ dùng identity mới.");
    }

    private async Task<bool?> CanCorrectIdentityAsync(
        string connectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .Select(session => new
            {
                session.Id,
                session.AdminUserId,
                session.BotOperatorZaloUserIdsJson,
                session.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return false;

        if (sessions.Any(session => ParseOperatorIds(session.BotOperatorZaloUserIdsJson).Contains(senderId)))
            return true;
        if (integration is null) return false;

        var authoritySession = sessions.OrderByDescending(session => session.UpdatedAt).First();
        var role = await integration.GetGroupRoleAuthorizationAsync(
            authoritySession.AdminUserId,
            authoritySession.Id,
            senderId);
        return role.IsSuccess ? role.Value?.CanOperateBot == true : null;
    }

    private async Task<ServiceResult<ZaloIdentityCorrectionApplied>> ApplyRenameAsync(
        string connectionId,
        string groupId,
        ZaloIdentityCorrectionPayload payload,
        CancellationToken cancellationToken)
    {
        var requestedName = payload.RequestedDisplayName.Trim().TrimStart('@');
        if (requestedName.Length is < 1 or > 160)
            return ServiceResult<ZaloIdentityCorrectionApplied>.Failure(
                StatusCodes.Status400BadRequest,
                "Tên identity mới không hợp lệ.");

        var profile = await db.PlayerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == payload.ProfileId && item.ZaloUserId == payload.ZaloUserId, cancellationToken);
        if (profile is null)
            return ServiceResult<ZaloIdentityCorrectionApplied>.Failure(
                StatusCodes.Status409Conflict,
                "Identity đã thay đổi hoặc UID không còn thuộc hồ sơ cũ. Hãy chạy lại `@Npc sửa identity @TênĐúng` để kiểm tra từ đầu.");
        if (SameName(profile.DisplayName, requestedName))
        {
            return ServiceResult<ZaloIdentityCorrectionApplied>.Success(new(
                profile.DisplayName,
                requestedName,
                true));
        }
        if (!SameName(profile.DisplayName, payload.StoredDisplayName))
            return ServiceResult<ZaloIdentityCorrectionApplied>.Failure(
                StatusCodes.Status409Conflict,
                $"Identity đã đổi từ lúc bot hỏi (hiện là `{profile.DisplayName}`). Không ghi đè thay đổi mới; hãy chạy lại lệnh sửa identity.");

        var sessionIds = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

        var existingNames = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) &&
                             player.IsPresent &&
                             player.PlayerProfileId != payload.ProfileId)
            .Select(player => new { player.SessionId, player.DisplayName })
            .ToListAsync(cancellationToken);
        var duplicate = existingNames.FirstOrDefault(item => SameName(item.DisplayName, requestedName));
        if (duplicate is not null)
        {
            return ServiceResult<ZaloIdentityCorrectionApplied>.Failure(
                StatusCodes.Status409Conflict,
                $"Trong một buổi của nhóm đã có người khác tên `{duplicate.DisplayName}`. Bot không tự merge hai hồ sơ chỉ vì trùng tên; identity chưa thay đổi.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var renamedProfiles = await db.PlayerProfiles
            .Where(item => item.Id == payload.ProfileId &&
                           item.ZaloUserId == payload.ZaloUserId &&
                           item.DisplayName == profile.DisplayName)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.DisplayName, requestedName)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.LastSyncedAt, now), cancellationToken);
        if (renamedProfiles != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<ZaloIdentityCorrectionApplied>.Failure(
                StatusCodes.Status409Conflict,
                "Identity vừa thay đổi bởi thao tác khác. Bot đã dừng để không ghi đè; hãy thử lại.");
        }

        var linkedPlayerIds = await db.SessionPlayers
            .Where(player => sessionIds.Contains(player.SessionId) && player.PlayerProfileId == payload.ProfileId)
            .Select(player => player.Id)
            .ToListAsync(cancellationToken);
        if (linkedPlayerIds.Count > 0)
        {
            await db.SessionPlayers
                .Where(player => linkedPlayerIds.Contains(player.Id))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(player => player.DisplayName, requestedName), cancellationToken);

            var affectedSlots = await db.DraftSlots
                .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => sessionIds.Contains(slot.SessionId) &&
                               slot.Players.Any(link => linkedPlayerIds.Contains(link.SessionPlayerId)))
                .ToListAsync(cancellationToken);
            foreach (var slot in affectedSlots)
            {
                slot.DisplayName = string.Join(" / ", slot.Players
                    .OrderBy(link => link.RotationOrder)
                    .Select(link => link.SessionPlayer.DisplayName));
            }
        }

        if (sessionIds.Count > 0)
        {
            await db.MatchSessions
                .Where(session => sessionIds.Contains(session.Id))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(session => session.UpdatedAt, now), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<ZaloIdentityCorrectionApplied>.Success(new(
            profile.DisplayName,
            requestedName,
            false));
    }

    private static string FormatChoices(ZaloIdentityCorrectionPayload payload) =>
        $"Mình thấy UID của @{payload.RequestedDisplayName} hiện đang lưu identity là `{payload.StoredDisplayName}`. " +
        "Mình chưa đổi gì. Chọn ngay trên Zalo:\n" +
        $"1. Giữ `{payload.StoredDisplayName}` (coi @mention vừa rồi là nhầm tài khoản)\n" +
        $"2. Đổi identity của UID này thành `{payload.RequestedDisplayName}`\n" +
        "3. Huỷ\n" +
        "Trả lời `@Npc 1`, `@Npc 2` hoặc `@Npc 3` trong 5 phút. Lựa chọn 2 cần trưởng/phó nhóm hoặc operator/admin xác nhận.";

    private static bool IsCorrectionCommand(string? question)
    {
        var normalized = ZaloBotIntelligence.Normalize(question ?? string.Empty);
        return Regex.IsMatch(
            normalized,
            @"^(?:(?:sua|doi|cap\s+nhat)\s+(?:identity|dinh\s+danh)|(?:identity|dinh\s+danh)\s+(?:sua|doi|cap\s+nhat))(?=\s|$)",
            RegexOptions.CultureInvariant);
    }

    private static (string ZaloUserId, string DisplayName)? ExtractSingleMention(ZaloIncomingMessageEvent incoming)
    {
        var botId = NormalizeId(incoming.BotId);
        var mentions = incoming.Mentions
            .Where(item => NormalizeId(item.Uid) != botId)
            .OrderBy(item => item.Pos)
            .ToList();
        if (mentions.Count != 1) return null;
        var mention = mentions[0];
        if (mention.Pos < 0 || mention.Len <= 0 || mention.Pos + mention.Len > incoming.Content.Length)
            return null;
        var displayName = incoming.Content.Substring(mention.Pos, mention.Len).Trim().TrimStart('@');
        var uid = NormalizeId(mention.Uid);
        return uid.Length == 0 || displayName.Length == 0 ? null : (uid, displayName);
    }

    private static ZaloIdentityCorrectionPayload? Deserialize(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<ZaloIdentityCorrectionPayload>(json ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static bool SameName(string first, string second) =>
        string.Equals(
            ZaloBotIntelligence.Normalize(first),
            ZaloBotIntelligence.Normalize(second),
            StringComparison.Ordinal);

    private static ZaloIdentityCorrectionConversationResult Handled(string response) => new(true, response);

    private sealed record ZaloIdentityCorrectionPayload(
        string ProfileId,
        string ZaloUserId,
        string StoredDisplayName,
        string RequestedDisplayName);
}

public sealed record ZaloIdentityCorrectionConversationResult(bool Handled, string? Response)
{
    public static ZaloIdentityCorrectionConversationResult NotHandled { get; } = new(false, null);
}

public sealed record ZaloIdentityCorrectionApplied(
    string OldDisplayName,
    string NewDisplayName,
    bool AlreadyApplied);
