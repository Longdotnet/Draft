from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if new in text:
        print(f"already patched: {path}")
        return
    if old not in text:
        raise SystemExit(f"anchor not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Zalo intent: add explicit unshare intent before normal share parsing.
intel = "server/VolleyDraft.Api/Services/ZaloBotIntelligence.cs"
replace_once(
    intel,
    "    ShareSlot,\n    ShareSlotConfirm,\n    RepairShareSlot,",
    "    ShareSlot,\n    ShareSlotConfirm,\n    UnshareSlot,\n    UnshareSlotConfirm,\n    RepairShareSlot,",
)
replace_once(
    intel,
    "    public static string Normalize(string value)\n",
    '''    public static bool IsUnshareSlotRequest(string value)\n    {\n        var q = Normalize(value).Replace("@", string.Empty, StringComparison.Ordinal);\n        var mentionsShare = Has(q,\n            "share slot", "share", "chung slot", "danh chung slot", "choi chung slot",\n            "slot thay phien", "thay phien");\n        if (!mentionsShare) return false;\n        var stopsSharing = Has(q,\n            "khong share", "ko share", "khong chung slot", "ko chung slot",\n            "khong danh chung slot", "khong choi chung slot", "khong thay phien",\n            "huy share", "bo share", "tach share", "tach slot",\n            "share nua", "chung slot nua", "thay phien nua");\n        return stopsSharing;\n    }\n\n    public static string Normalize(string value)\n''',
)
replace_once(
    intel,
    "        if (ZaloNaturalCommandParser.TryParseRepairShareSlot(value, out _))\n            return new(ZaloBotIntent.RepairShareSlot, .99, q, false, null, \"repair_share_slot_phrase\");",
    "        if (IsUnshareSlotRequest(value))\n            return new(ZaloBotIntent.UnshareSlot, .995, q, false, null, \"unshare_slot_phrase\");\n        if (ZaloNaturalCommandParser.TryParseRepairShareSlot(value, out _))\n            return new(ZaloBotIntent.RepairShareSlot, .99, q, false, null, \"repair_share_slot_phrase\");",
)
replace_once(
    intel,
    "intent is ZaloBotIntent.Unknown or ZaloBotIntent.Help or ZaloBotIntent.AutoDraftConfirm or ZaloBotIntent.RedraftConfirm or ZaloBotIntent.RebalanceTeamsConfirm or ZaloBotIntent.TeamPreferenceConfirm or ZaloBotIntent.ShareSlotConfirm or ZaloBotIntent.RepairShareSlotConfirm or ZaloBotIntent.SlotTransferConfirm or ZaloBotIntent.UndoActionConfirm",
    "intent is ZaloBotIntent.Unknown or ZaloBotIntent.Help or ZaloBotIntent.AutoDraftConfirm or ZaloBotIntent.RedraftConfirm or ZaloBotIntent.RebalanceTeamsConfirm or ZaloBotIntent.TeamPreferenceConfirm or ZaloBotIntent.ShareSlotConfirm or ZaloBotIntent.UnshareSlotConfirm or ZaloBotIntent.RepairShareSlotConfirm or ZaloBotIntent.SlotTransferConfirm or ZaloBotIntent.UndoActionConfirm",
)

# 2) Make ZaloBotService partial and wire unshare into pending + routing.
bot = "server/VolleyDraft.Api/Services/ZaloBotService.cs"
replace_once(bot, "public sealed class ZaloBotService(", "public sealed partial class ZaloBotService(")

replace_once(
    bot,
    '''        if (pending.ShareSlotPlan is not null && pending.Session is not null)\n        {\n            return await ApplyShareSlotPlanAsync(\n                pending.Session,\n                pending.ShareSlotPlan,\n                incoming,\n                activeConnectionId,\n                groupId,\n                false,\n                cancellationToken);\n        }\n''',
    '''        if (pending.ShareSlotPlan is not null && pending.Session is not null)\n        {\n            return await ApplyShareSlotPlanAsync(\n                pending.Session,\n                pending.ShareSlotPlan,\n                incoming,\n                activeConnectionId,\n                groupId,\n                false,\n                cancellationToken);\n        }\n        if (!string.IsNullOrWhiteSpace(pending.UnshareSlotId) && pending.Session is not null)\n        {\n            return await HandleUnshareSlotAsync(\n                new ZaloIntentDecision(\n                    ZaloBotIntent.UnshareSlotConfirm,\n                    1,\n                    pending.Session.Name,\n                    false,\n                    null,\n                    "unshare_slot_confirmation"),\n                [pending.Session],\n                NormalizeText(pending.Session.Name),\n                activeConnectionId,\n                groupId,\n                incoming,\n                cancellationToken,\n                false,\n                confirmed: true,\n                expectedSlotId: pending.UnshareSlotId,\n                forcedSession: pending.Session);\n        }\n''',
)

replace_once(
    bot,
    '''        if (earlyDecision.Intent == ZaloBotIntent.RepairShareSlot)\n        {\n            return await HandleRepairShareSlotAsync(\n                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);\n        }\n''',
    '''        if (earlyDecision.Intent == ZaloBotIntent.UnshareSlot)\n        {\n            return await HandleUnshareSlotAsync(\n                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);\n        }\n        if (earlyDecision.Intent == ZaloBotIntent.RepairShareSlot)\n        {\n            return await HandleRepairShareSlotAsync(\n                earlyDecision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);\n        }\n''',
)

replace_once(
    bot,
    '''        if (decision.Intent == ZaloBotIntent.ShareSlot)\n            return await ShareSlotAsync(decision, sessions, normalizedQuestion, question, activeConnectionId, groupId, incoming, cancellationToken, false);\n\n        if (decision.Intent == ZaloBotIntent.RepairShareSlot)\n''',
    '''        if (decision.Intent == ZaloBotIntent.ShareSlot)\n            return await ShareSlotAsync(decision, sessions, normalizedQuestion, question, activeConnectionId, groupId, incoming, cancellationToken, false);\n\n        if (decision.Intent == ZaloBotIntent.UnshareSlot)\n            return await HandleUnshareSlotAsync(decision, sessions, normalizedQuestion, activeConnectionId, groupId, incoming, cancellationToken, false);\n\n        if (decision.Intent == ZaloBotIntent.RepairShareSlot)\n''',
)

replace_once(
    bot,
    '''        if (decision.Intent == ZaloBotIntent.ShareSlot)\n            return await ShareSlotAsync(decision, sessions, selector, ExtractQuestion(incoming), connectionId, groupId, incoming, cancellationToken, true);\n        if (decision.Intent == ZaloBotIntent.IncompleteProfiles)\n''',
    '''        if (decision.Intent == ZaloBotIntent.ShareSlot)\n            return await ShareSlotAsync(decision, sessions, selector, ExtractQuestion(incoming), connectionId, groupId, incoming, cancellationToken, true);\n        if (decision.Intent == ZaloBotIntent.UnshareSlot)\n            return await HandleUnshareSlotAsync(decision, sessions, selector, connectionId, groupId, incoming, cancellationToken, true);\n        if (decision.Intent == ZaloBotIntent.IncompleteProfiles)\n''',
)

# Pending confirmation for unshare.
replace_once(
    bot,
    '''        if (state.PendingIntent is not null &&\n            (state.PendingIntent == ZaloBotIntent.AutoDraftConfirm.ToString() ||\n             state.PendingIntent == ZaloBotIntent.RedraftConfirm.ToString()))\n''',
    '''        if (state.PendingIntent == ZaloBotIntent.UnshareSlotConfirm.ToString())\n        {\n            UnshareSlotConfirmationPayload? payload;\n            try { payload = JsonSerializer.Deserialize<UnshareSlotConfirmationPayload>(state.PendingPayloadJson); }\n            catch (JsonException) { payload = null; }\n            var actionSession = payload is null ? null : sessions.SingleOrDefault(session => session.Id == payload.SessionId);\n            if (payload is not null && actionSession is not null && ZaloBotIntelligence.IsConfirmation(normalizedQuestion))\n            {\n                db.ZaloBotConversationStates.Remove(state);\n                await db.SaveChangesAsync(cancellationToken);\n                return new PendingResolution(\n                    false,\n                    ZaloBotIntent.UnshareSlotConfirm,\n                    actionSession,\n                    null,\n                    UnshareSlotId: payload.SlotId);\n            }\n            var newIntent = ZaloBotIntelligence.ClassifyDeterministically(normalizedQuestion).Intent;\n            if (newIntent is not (ZaloBotIntent.Unknown or ZaloBotIntent.Help))\n            {\n                db.ZaloBotConversationStates.Remove(state);\n                await db.SaveChangesAsync(cancellationToken);\n                return PendingResolution.None;\n            }\n            return new PendingResolution(false, null, null,\n                "Mình đang chờ xác nhận tách shared slot. Gõ @bot xác nhận để tách thành các slot riêng hoặc @bot huỷ.");\n        }\n        if (state.PendingIntent is not null &&\n            (state.PendingIntent == ZaloBotIntent.AutoDraftConfirm.ToString() ||\n             state.PendingIntent == ZaloBotIntent.RedraftConfirm.ToString()))\n''',
)

replace_once(
    bot,
    '''        ShareSlotConfirmationPlan? ShareSlotPlan = null,\n        ZaloShareSlotCommand? ShareCommand = null)\n''',
    '''        ShareSlotConfirmationPlan? ShareSlotPlan = null,\n        ZaloShareSlotCommand? ShareCommand = null,\n        string? UnshareSlotId = null)\n''',
)
replace_once(
    bot,
    '''    private sealed record ShareSlotConfirmationPayload(\n        string SessionId,\n        ShareSlotConfirmationPlan Plan);\n''',
    '''    private sealed record ShareSlotConfirmationPayload(\n        string SessionId,\n        ShareSlotConfirmationPlan Plan);\n    private sealed record UnshareSlotConfirmationPayload(string SessionId, string SlotId);\n''',
)

# 3) Unshare implementation as a separate partial file.
unshare_path = Path("server/VolleyDraft.Api/Services/ZaloBotService.Unshare.cs")
unshare_path.write_text(r'''using Microsoft.EntityFrameworkCore;
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
''', encoding="utf-8")

# 4) Overbook contract: scope + status poll identity.
contracts = "server/VolleyDraft.Api/Contracts/ZaloOverbookContracts.cs"
replace_once(
    contracts,
    "public sealed record ConfirmZaloOverbookTargetsRequest(IReadOnlyList<string> ZaloUserIds);",
    '''public sealed record ConfirmZaloOverbookTargetsRequest(\n    IReadOnlyList<string> ZaloUserIds,\n    string? ExpectedPollId = null,\n    IReadOnlyList<string>? ExpectedSelectedOptionIds = null);''',
)
replace_once(
    contracts,
    '''    int ReminderCount,\n    DateTimeOffset? LastReminderAt,\n    DateTimeOffset? NextReminderAt,\n    IReadOnlyList<ZaloOverbookVoterResponse> Voters,''',
    '''    int ReminderCount,\n    DateTimeOffset? LastReminderAt,\n    DateTimeOffset? NextReminderAt,\n    string? CurrentPollId,\n    IReadOnlyList<string> CurrentSelectedOptionIds,\n    IReadOnlyList<ZaloOverbookVoterResponse> Voters,''',
)

# 5) Status serializer adds current poll/option scope.
obs = "server/VolleyDraft.Api/Services/ZaloOverbookObservation.cs"
replace_once(
    obs,
    '''        state.ReminderCount,\n        state.LastReminderAt,\n        state.NextReminderAt,\n        voters,''',
    '''        state.ReminderCount,\n        state.LastReminderAt,\n        state.NextReminderAt,\n        state.CurrentPollId,\n        state.CurrentSelectedOptionIds,\n        voters,''',
)

# 6) New scoped confirm / confirm+remind-now implementation.
manual = Path("server/VolleyDraft.Api/Services/ZaloOverbookManualReminder.cs")
manual.write_text(r'''using System.Security.Cryptography;
using System.Text;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    public Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsScopedAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        CancellationToken cancellationToken = default) =>
        ConfirmTargetsCoreAsync(adminUserId, sessionId, request, remindNow: false, cancellationToken);

    public Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsAndRemindNowAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        CancellationToken cancellationToken = default) =>
        ConfirmTargetsCoreAsync(adminUserId, sessionId, request, remindNow: true, cancellationToken);

    private async Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsCoreAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        bool remindNow,
        CancellationToken cancellationToken)
    {
        var owned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (owned is null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");
        if (remindNow && !owned.BotEnabled)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Bot Zalo của trận đang tắt. Bật bot trước khi nhắc ngay.");

        var synced = await integration.SyncLatestPollAsync(adminUserId, sessionId);
        if (!synced.IsSuccess)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                synced.StatusCode,
                synced.Error ?? "Không đồng bộ được poll mới nhất trước khi xác nhận lượt vote dư.");

        OverbookObservation observation;
        try
        {
            await ObserveAsync(sessionId, null, cancellationToken);
            observation = await ReadObservationAsync(sessionId, cancellationToken);
        }
        catch (Exception exception)
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Không thể đồng bộ poll để xác nhận: {exception.Message}");
        }

        if (!MatchesExpectedScope(
                observation.Poll.Id,
                observation.SelectedOptionIds,
                request.ExpectedPollId,
                request.ExpectedSelectedOptionIds))
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status409Conflict,
                "Poll/option của trận đã thay đổi sau lúc bạn mở màn hình. Hãy đồng bộ trạng thái rồi chọn lại người vote dư để tránh mention nhầm trận.");
        }

        var normalized = (request.ZaloUserIds ?? [])
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var validation = ValidateConfirmedTargets(observation, normalized);
        if (validation is not null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, validation);

        var state = await GetOrCreateStateAsync(sessionId, cancellationToken);
        if (remindNow && !state.Enabled)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Hãy bật và lưu cảnh báo vượt slot trước khi dùng “Xác nhận & nhắc ngay”, để các lần nhắc tiếp theo còn được theo dõi đúng lịch.");

        var now = DateTimeOffset.UtcNow;
        ApplyConfirmedTargets(state, normalized, now);
        state.IncidentKey = BuildAdminIncidentKey(
            observation.Poll.Id,
            observation.SelectedOptionIds,
            observation.Poll.UpdatedAtUnixMs,
            observation.Capacity.EffectiveSlotCount,
            normalized);
        await store.SaveAsync(state, cancellationToken);

        if (!remindNow)
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusAsync(observation, state, cancellationToken));

        try
        {
            // Re-read immediately before send. This is intentionally separate from
            // the confirmation read so a vote removed during the click cannot be tagged.
            observation = await ReadObservationAsync(sessionId, cancellationToken);
            if (!MatchesExpectedScope(
                    observation.Poll.Id,
                    observation.SelectedOptionIds,
                    request.ExpectedPollId,
                    request.ExpectedSelectedOptionIds))
            {
                RequireConfirmation(state, "OrderChangedUncertain");
                await store.SaveAsync(state, cancellationToken);
                return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                    StatusCodes.Status409Conflict,
                    "Poll/option vừa thay đổi trước lúc gửi. Bot đã dừng gửi để tránh mention nhầm người; hãy đồng bộ và xác nhận lại.");
            }
            var revalidation = ValidateConfirmedTargets(observation, normalized);
            if (revalidation is not null)
            {
                RequireConfirmation(state, "TargetsNoLongerPresent");
                await store.SaveAsync(state, cancellationToken);
                return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status409Conflict, revalidation);
            }

            var reminderNumber = state.ReminderCount + 1;
            var body = await BuildReminderBodyAsync(owned, state, observation, reminderNumber, cancellationToken);
            var outgoing = BuildMentionMessage(normalized, observation.DisplayNames, body);
            var idempotencyKey = $"overbook:{sessionId}:{state.IncidentKey}:{reminderNumber}";
            await bridge.SendGroupMessageAsync(
                owned.ZaloConnection!.AccountZaloId,
                owned.ZaloGroupId!,
                outgoing.Message,
                outgoing.Mentions,
                idempotencyKey: idempotencyKey);

            state.ReminderCount = reminderNumber;
            state.LastReminderAt = now;
            state.NextReminderAt = reminderNumber >= state.MaxReminders
                ? null
                : now.AddMinutes(state.ReminderIntervalMinutes);
            state.LastError = null;
            await store.SaveAsync(state, cancellationToken);
            await SaveBotMessageAsync(owned, idempotencyKey, outgoing.Message, now, cancellationToken);
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusAsync(observation, state, cancellationToken));
        }
        catch (Exception exception)
        {
            state.LastError = Truncate(exception.Message, 1000);
            state.NextReminderAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60));
            await store.SaveAsync(state, cancellationToken);
            logger.LogWarning(exception, "Could not send manual overbook reminder Session={SessionId}", sessionId);
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Đã xác nhận target nhưng chưa gửi được Zalo: {exception.Message}. Worker sẽ thử lại theo lịch retry.");
        }
    }

    internal static bool MatchesExpectedScope(
        string currentPollId,
        IReadOnlyList<string> currentSelectedOptionIds,
        string? expectedPollId,
        IReadOnlyList<string>? expectedSelectedOptionIds)
    {
        if (string.IsNullOrWhiteSpace(expectedPollId) && expectedSelectedOptionIds is null)
            return true; // backwards compatibility for older clients.
        if (!string.Equals(currentPollId, expectedPollId, StringComparison.Ordinal)) return false;
        var current = currentSelectedOptionIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var expected = (expectedSelectedOptionIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        return current.SetEquals(expected);
    }

    internal static string BuildAdminIncidentKey(
        string pollId,
        IReadOnlyList<string> selectedOptionIds,
        long? pollUpdatedAtUnixMs,
        int effectiveSlotCount,
        IReadOnlyList<string> targets)
    {
        var source = string.Join('|', new[]
        {
            pollId,
            string.Join(',', selectedOptionIds.OrderBy(id => id, StringComparer.Ordinal)),
            pollUpdatedAtUnixMs?.ToString() ?? "0",
            effectiveSlotCount.ToString(),
            string.Join(',', targets.OrderBy(id => id, StringComparer.Ordinal))
        });
        return "admin-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
    }
}
''', encoding="utf-8")

# 7) Route scoped confirm and confirm+remind-now.
program = "server/VolleyDraft.Api/Program.cs"
replace_once(
    program,
    '''        : (await service.ConfirmTargetsAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();\n});\nsessions.MapGet("/{sessionId}/zalo-bot-rules",''',
    '''        : (await service.ConfirmTargetsScopedAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();\n});\nsessions.MapPost("/{sessionId}/zalo-overbook/confirm-and-remind", async (\n    HttpContext httpContext,\n    string sessionId,\n    ConfirmZaloOverbookTargetsRequest request,\n    ZaloOverbookService service,\n    CancellationToken cancellationToken) =>\n{\n    var userId = httpContext.User.GetUserId();\n    return userId is null\n        ? Results.Unauthorized()\n        : (await service.ConfirmTargetsAndRemindNowAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();\n});\nsessions.MapGet("/{sessionId}/zalo-bot-rules",''',
)

# 8) Frontend overbook UI: carry scope and add immediate-send button.
front = "src/components/ZaloOverbookAdminPanel.tsx"
replace_once(
    front,
    '''  nextReminderAt: string | null;\n  voters: OverbookVoter[];''',
    '''  nextReminderAt: string | null;\n  currentPollId: string | null;\n  currentSelectedOptionIds: string[];\n  voters: OverbookVoter[];''',
)
replace_once(
    front,
    '''        body: { zaloUserIds: confirmIds },\n      });''',
    '''        body: {\n          zaloUserIds: confirmIds,\n          expectedPollId: status?.currentPollId ?? null,\n          expectedSelectedOptionIds: status?.currentSelectedOptionIds ?? [],\n        },\n      });''',
)
replace_once(
    front,
    '''  function toggleConfirm(id: string) {\n''',
    '''  async function confirmAndRemindNow() {\n    if (!token || !sessionId || confirmIds.length === 0 || !status) return;\n    setBusy(true);\n    try {\n      const next = await apiFetch<OverbookStatus>(`/sessions/${sessionId}/zalo-overbook/confirm-and-remind`, {\n        method: "POST",\n        token,\n        body: {\n          zaloUserIds: confirmIds,\n          expectedPollId: status.currentPollId,\n          expectedSelectedOptionIds: status.currentSelectedOptionIds,\n        },\n      });\n      applyStatus(next);\n      setMessage("Đã xác nhận và mention nhắc ngay trên Zalo. Lần gửi này được tính là reminder #1.");\n    } catch (error) {\n      setMessage(error instanceof ApiRequestError ? error.message : "Không gửi được cảnh báo Zalo ngay lúc này.");\n    } finally {\n      setBusy(false);\n    }\n  }\n\n  function toggleConfirm(id: string) {\n''',
)
replace_once(
    front,
    '''              <button type="button" onClick={() => void confirmTargets()} disabled={busy || confirmIds.length === 0} style={{ ...buttonStyle, marginTop: 12, background: "#f59e0b", color: "#111827" }}>\n                <ShieldCheck size={16} /> Xác nhận người vote dư\n              </button>\n              <p style={{ marginBottom: 0, color: "#94a3b8", fontSize: 13 }}>Ngoài website, admin/operator có thể mention bot và gõ “xác nhận vote dư” khi group chỉ có một trận đang chờ xác nhận.</p>''',
    '''              <div style={{ display: "flex", gap: 10, flexWrap: "wrap", marginTop: 12 }}>\n                <button type="button" onClick={() => void confirmTargets()} disabled={busy || confirmIds.length === 0} style={{ ...buttonStyle, background: "#f59e0b", color: "#111827" }}>\n                  <ShieldCheck size={16} /> Xác nhận người vote dư\n                </button>\n                <button\n                  type="button"\n                  onClick={() => void confirmAndRemindNow()}\n                  disabled={busy || confirmIds.length === 0 || !status.botEnabled || !status.enabled}\n                  style={{ ...buttonStyle, background: "#ef4444", color: "#fff", opacity: !status.botEnabled || !status.enabled ? 0.55 : 1 }}\n                >\n                  <Bot size={16} /> Xác nhận & nhắc ngay\n                </button>\n              </div>\n              {!status.enabled || !status.botEnabled ? (\n                <p style={{ marginBottom: 0, color: "#fbbf24", fontSize: 13 }}>Muốn nhắc ngay, hãy bật bot Zalo và bật + lưu cảnh báo vượt slot trước. Sau lần gửi ngay, bot tiếp tục dùng khoảng cách nhắc đã cấu hình.</p>\n              ) : null}\n              <p style={{ marginBottom: 0, color: "#94a3b8", fontSize: 13 }}>Ngoài website, admin/operator có thể mention bot và gõ “xác nhận vote dư” khi group chỉ có một trận đang chờ xác nhận.</p>''',
)

# 9) Tests.
Path("server/VolleyDraft.Api.Tests/ZaloUnshareIntentTests.cs").write_text(r'''using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloUnshareIntentTests
{
    [Theory]
    [InlineData("tui không share slot với To An nữa")]
    [InlineData("tui với To An ko share nữa")]
    [InlineData("hủy share slot với To An")]
    [InlineData("tách slot, không thay phiên nữa")]
    public void Unshare_phrases_are_classified_before_normal_share(string text)
    {
        Assert.Equal(ZaloBotIntent.UnshareSlot, ZaloBotIntelligence.ClassifyDeterministically(text).Intent);
    }

    [Fact]
    public void Normal_share_is_still_share_slot()
    {
        Assert.Equal(
            ZaloBotIntent.ShareSlot,
            ZaloBotIntelligence.ClassifyDeterministically("tui muốn share slot với To An").Intent);
    }
}
''', encoding="utf-8")

Path("server/VolleyDraft.Api.Tests/ZaloOverbookScopeTests.cs").write_text(r'''using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOverbookScopeTests
{
    [Fact]
    public void Same_poll_and_same_options_are_accepted_regardless_of_option_order()
    {
        Assert.True(ZaloOverbookService.MatchesExpectedScope(
            "poll-1", ["cn", "t6"], "poll-1", ["t6", "cn"]));
    }

    [Fact]
    public void Different_poll_is_rejected()
    {
        Assert.False(ZaloOverbookService.MatchesExpectedScope(
            "poll-2", ["cn"], "poll-1", ["cn"]));
    }

    [Fact]
    public void Different_selected_options_are_rejected()
    {
        Assert.False(ZaloOverbookService.MatchesExpectedScope(
            "poll-1", ["t6"], "poll-1", ["cn"]));
    }

    [Fact]
    public void Admin_incident_key_is_stable_for_same_poll_snapshot_and_targets()
    {
        var first = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        var second = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Admin_incident_key_changes_when_poll_snapshot_changes()
    {
        var first = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        var second = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1235, 19, ["u19"]);
        Assert.NotEqual(first, second);
    }
}
''', encoding="utf-8")

print("patch complete")
