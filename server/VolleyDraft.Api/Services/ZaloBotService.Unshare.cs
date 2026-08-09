using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    private async Task<BotAnswer> HandleUnshareSlotAsync(
        ZaloIntentDecision decision,
        IReadOnlyList<SessionSnapshot> sessions,
        string selector,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken,
        bool aiCalled,
        bool confirmed = false,
        string? expectedSlotId = null,
        SessionSnapshot? forcedSession = null)
    {
        var sessionIds = sessions.Select(session => session.Id).ToList();
        var senderId = NormalizeId(incoming.SenderId);
        var allSharedSlots = await db.DraftSlots
            .AsNoTracking()
            .Include(slot => slot.Players)
            .ThenInclude(link => link.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .Where(slot => sessionIds.Contains(slot.SessionId) && slot.Type == DraftSlotType.Shared)
            .ToListAsync(cancellationToken);

        var senderSlots = allSharedSlots
            .Where(slot => slot.Players.Any(link =>
                NormalizeId(link.SessionPlayer.PlayerProfile?.ZaloUserId) == senderId))
            .ToList();
        if (senderSlots.Count == 0)
        {
            return new BotAnswer(
                "Mình chưa thấy bạn đang nằm trong shared slot nào của các trận đang bật bot, nên không có gì để tách.",
                null,
                decision.Intent,
                aiCalled);
        }

        var candidateIds = senderSlots.Select(slot => slot.SessionId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var candidates = sessions.Where(session => candidateIds.Contains(session.Id)).ToList();
        SessionSnapshot? session = forcedSession;
        if (session is null)
        {
            var selected = await SelectSessionAsync(
                candidates,
                selector,
                connectionId,
                groupId,
                incoming.SenderId,
                ZaloBotIntent.UnshareSlot,
                cancellationToken);
            if (selected.Clarification is not null)
                return new BotAnswer(selected.Clarification + " Bot vẫn nhớ là bạn muốn tách shared slot.", null, decision.Intent, aiCalled);
            session = selected.Session;
        }
        if (session is null || !candidateIds.Contains(session.Id))
            return new BotAnswer("Mình chưa xác định được shared slot của bạn thuộc trận nào.", null, decision.Intent, aiCalled);

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
        {
            return new BotAnswer(
                $"{session.Name} đã bắt đầu draft hoặc đã kết thúc nên không thể tách shared slot theo kiểu trước draft.",
                null,
                decision.Intent,
                aiCalled,
                ProtectedTerms: [session.Name]);
        }

        var refresh = await RefreshShareSessionAsync(session, groupId, incoming.SenderId, cancellationToken);
        if (refresh.Session is null)
        {
            return new BotAnswer(
                refresh.Error ?? "Chưa đồng bộ được poll mới nhất để kiểm tra tách shared slot.",
                null,
                decision.Intent,
                aiCalled,
                ProtectedTerms: [session.Name]);
        }
        session = refresh.Session;

        var senderIsCurrentVoter = await IsSenderCurrentPollVoterAsync(session, incoming.SenderId, cancellationToken);
        if (!senderIsCurrentVoter)
        {
            var denial = await GetOperatorDenialAsync(session, incoming.SenderId, decision.Intent, aiCalled);
            if (denial is not null)
            {
                return new BotAnswer(
                    $"Mình đã đồng bộ {session.Name} nhưng UID của bạn không còn nằm trong vote hiện tại. Thành viên thường chỉ tự tách shared slot khi chính mình vẫn đang vote option của trận này.",
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }

        var slot = await db.DraftSlots
            .AsNoTracking()
            .Include(item => item.Players)
            .ThenInclude(link => link.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .SingleOrDefaultAsync(item =>
                item.SessionId == session.Id &&
                item.Type == DraftSlotType.Shared &&
                item.Players.Any(link => link.SessionPlayer.PlayerProfile != null &&
                    link.SessionPlayer.PlayerProfile.ZaloUserId != null &&
                    link.SessionPlayer.PlayerProfile.ZaloUserId == incoming.SenderId),
                cancellationToken);
        if (slot is null)
        {
            // UID may be stored with the bridge _0 suffix stripped; resolve in memory as fallback.
            var currentSlots = await db.DraftSlots
                .AsNoTracking()
                .Include(item => item.Players)
                .ThenInclude(link => link.SessionPlayer)
                .ThenInclude(player => player.PlayerProfile)
                .Where(item => item.SessionId == session.Id && item.Type == DraftSlotType.Shared)
                .ToListAsync(cancellationToken);
            slot = currentSlots.SingleOrDefault(item => item.Players.Any(link =>
                NormalizeId(link.SessionPlayer.PlayerProfile?.ZaloUserId) == senderId));
        }
        if (slot is null)
            return new BotAnswer($"Bạn không còn nằm trong shared slot của {session.Name}.", null, decision.Intent, aiCalled);
        if (confirmed && !string.Equals(slot.Id, expectedSlotId, StringComparison.Ordinal))
        {
            return new BotAnswer(
                "Shared slot đã thay đổi sau lúc xem trước nên mình không tách để tránh sửa nhầm. Hãy gửi lại yêu cầu không share nữa.",
                null,
                decision.Intent,
                aiCalled);
        }

        var members = slot.Players
            .Where(link => link.SessionPlayer.IsPresent)
            .OrderBy(link => link.RotationOrder)
            .Select(link => link.SessionPlayer)
            .ToList();
        if (members.Count < 2)
            return new BotAnswer("Shared slot hiện không còn đủ thành viên để tách.", null, decision.Intent, aiCalled);

        var mentioned = ExtractMentionedUsers(incoming);
        if (mentioned.Count > 0)
        {
            var memberIds = members
                .Select(member => NormalizeId(member.PlayerProfile?.ZaloUserId))
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var invalidMention = mentioned.FirstOrDefault(user => !memberIds.Contains(NormalizeId(user.ZaloUserId)));
            if (invalidMention is not null)
            {
                return new BotAnswer(
                    $"{invalidMention.DisplayName} không nằm trong shared slot hiện tại của bạn. Shared slot thật đang là: {string.Join(" / ", members.Select(member => member.DisplayName))}.",
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: members.Select(member => member.DisplayName).Append(invalidMention.DisplayName).ToList());
            }
        }
        else if (members.Count == 2 && ZaloBotIntelligence.Normalize(ExtractQuestion(incoming)).Contains(" voi ", StringComparison.Ordinal))
        {
            var other = members.First(member => NormalizeId(member.PlayerProfile?.ZaloUserId) != senderId);
            if (!ZaloBotIntelligence.Normalize(ExtractQuestion(incoming)).Contains(ZaloBotIntelligence.Normalize(other.DisplayName), StringComparison.Ordinal))
            {
                return new BotAnswer(
                    $"Tên bạn nói không khớp shared slot hiện tại. Bạn đang share với {other.DisplayName} trong {session.Name}.",
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [other.DisplayName, session.Name]);
            }
        }

        var missingCurrentVotes = new List<string>();
        foreach (var member in members)
        {
            var zaloId = NormalizeId(member.PlayerProfile?.ZaloUserId);
            if (zaloId.Length == 0) continue;
            if (zaloId == senderId && !senderIsCurrentVoter) continue; // operator override for own slot.
            if (!await IsSenderCurrentPollVoterAsync(session, zaloId, cancellationToken))
                missingCurrentVotes.Add(member.DisplayName);
        }
        if (missingCurrentVotes.Count > 0)
        {
            return new BotAnswer(
                $"Chưa thể tách thành slot riêng vì {string.Join(", ", missingCurrentVotes)} không còn nằm trong vote hiện tại của {session.Name}. Nếu họ vẫn chơi riêng, hãy vote lại đúng option rồi thử lại để tránh tạo slot ma.",
                null,
                decision.Intent,
                aiCalled,
                ProtectedTerms: missingCurrentVotes.Append(session.Name).ToList());
        }

        var manualMembers = members
            .Where(member => string.IsNullOrWhiteSpace(member.PlayerProfile?.ZaloUserId))
            .Select(member => member.DisplayName)
            .ToList();
        var regularCount = await db.SessionPlayers.AsNoTracking()
            .CountAsync(player => player.SessionId == session.Id && player.IsPresent && !player.IsInsideSharedSlot, cancellationToken);
        var sharedCount = await db.DraftSlots.AsNoTracking()
            .CountAsync(item => item.SessionId == session.Id && item.Type == DraftSlotType.Shared, cancellationToken);
        var effectiveBefore = regularCount + sharedCount;
        var effectiveAfter = effectiveBefore + members.Count - 1;
        var memberNames = string.Join(" / ", members.Select(member => member.DisplayName));
        var manualNote = manualMembers.Count == 0
            ? string.Empty
            : $" {string.Join(", ", manualMembers)} không có UID Zalo nên sau khi tách sẽ được tính là slot thủ công/non-Zalo.";

        if (!confirmed)
        {
            await SaveUnshareSlotConfirmationAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                session.Id,
                slot.Id,
                cancellationToken);
            return new BotAnswer(
                $"Mình hiểu bạn muốn bỏ shared slot {memberNames} của {session.Name}. Hiện nhóm này chiếm 1 slot; sau khi tách sẽ thành {members.Count} slot riêng, tổng slot hiệu lực {effectiveBefore} → {effectiveAfter}/{session.Capacity}.{manualNote} Gõ @bot xác nhận để tách hoặc @bot huỷ.",
                null,
                ZaloBotIntent.UnshareSlot,
                aiCalled,
                ProtectedTerms: members.Select(member => member.DisplayName).Append(session.Name).ToList());
        }

        var before = await actionHistory.CaptureAsync(session.Id, cancellationToken);
        var deleted = await draftService.DeleteSharedSlotAsync(session.AdminUserId, session.Id, slot.Id);
        if (!deleted.IsSuccess)
            return new BotAnswer(deleted.Error ?? "Không tách được shared slot.", null, ZaloBotIntent.UnshareSlotConfirm, aiCalled);
        await actionHistory.RecordAsync(
            session.Id,
            incoming.SenderId,
            incoming.SenderName,
            "UnshareSlot",
            $"Tách shared slot {memberNames} thành {members.Count} slot riêng trong {session.Name}",
            before,
            cancellationToken);
        return new BotAnswer(
            $"Đã tách {memberNames} trong {session.Name}: 1 shared slot → {members.Count} slot riêng. Tổng slot hiệu lực dự kiến {effectiveAfter}/{session.Capacity}.{manualNote}",
            null,
            ZaloBotIntent.UnshareSlotConfirm,
            aiCalled,
            ProtectedTerms: members.Select(member => member.DisplayName).Append(session.Name).ToList());
    }

    private async Task SaveUnshareSlotConfirmationAsync(
        string connectionId,
        string groupId,
        string senderId,
        string sessionId,
        string slotId,
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
        state.PendingIntent = ZaloBotIntent.UnshareSlotConfirm.ToString();
        state.PendingPayloadJson = System.Text.Json.JsonSerializer.Serialize(
            new UnshareSlotConfirmationPayload(sessionId, slotId));
        state.PreviousCommand = ZaloBotIntent.UnshareSlot.ToString();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
