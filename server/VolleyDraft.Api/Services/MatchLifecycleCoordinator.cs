using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Read-only orchestration view over the existing match automation lanes.
/// It never authorizes or executes a mutation. Its job is to answer, from
/// authoritative backend state: where the match is, who owns the next step,
/// and whether a human truly needs the website.
/// </summary>
public sealed class MatchLifecycleCoordinator(VolleyDraftDbContext db)
{
    public async Task<ServiceResult<MatchLifecycleResponse>> GetAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == sessionId && item.AdminUserId == adminUserId,
                cancellationToken);
        if (session is null)
        {
            return ServiceResult<MatchLifecycleResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy session.");
        }

        return ServiceResult<MatchLifecycleResponse>.Success(
            await BuildAsync(session, cancellationToken));
    }

    public async Task<ServiceResult<IReadOnlyList<MatchLifecycleResponse>>> GetForAdminAsync(
        string adminUserId,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 20);
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(item => item.AdminUserId == adminUserId)
            .OrderBy(item => item.Status == SessionStatus.Cancelled ? 1 : 0)
            .ThenBy(item => item.StartTime == null ? 1 : 0)
            .ThenBy(item => item.StartTime ?? DateTimeOffset.MaxValue)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(count * 2)
            .ToListAsync(cancellationToken);

        // Prefer upcoming/recent matches but keep setup rows visible so the client can
        // see one-time configuration exceptions. Avoid filling the control room with
        // old completed sessions when there are current matches to operate.
        var selected = sessions
            .Where(item =>
                item.Status is not (SessionStatus.Finished or SessionStatus.Cancelled) ||
                item.StartTime is null ||
                item.StartTime >= now.AddDays(-2))
            .Take(count)
            .ToList();
        if (selected.Count == 0)
            selected = sessions.Take(count).ToList();

        var result = new List<MatchLifecycleResponse>(selected.Count);
        foreach (var session in selected)
            result.Add(await BuildAsync(session, cancellationToken));

        return ServiceResult<IReadOnlyList<MatchLifecycleResponse>>.Success(result);
    }

    private async Task<MatchLifecycleResponse> BuildAsync(
        MatchSession session,
        CancellationToken cancellationToken)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;
        var capacity = Math.Max(1, session.TeamCount * session.TeamSize);
        var presentCount = await db.SessionPlayers
            .AsNoTracking()
            .CountAsync(player => player.SessionId == session.Id && player.IsPresent, cancellationToken);

        if (session.Status == SessionStatus.Cancelled)
        {
            return Response(
                session, MatchLifecycleStage.Cancelled, "Đã hủy",
                "Kèo đã hủy, autopilot không còn việc phải làm.",
                "Không cần thao tác.", MatchLifecycleOwner.None,
                false, null, null, presentCount, presentCount, capacity,
                [], 0, null, "session_cancelled", evaluatedAt);
        }

        if (session.Status == SessionStatus.Finished)
        {
            return Response(
                session, MatchLifecycleStage.Drafted, "Đã có team",
                "Draft đã hoàn tất; lifecycle chuyển sang các thay đổi hậu draft.",
                "Bot tiếp tục xử lý pass/claim/đổi slot theo các flow hiện có. Chỉ mở web khi bot báo conflict hoặc cần rollback.",
                MatchLifecycleOwner.ZaloBot,
                false, null, null, presentCount, presentCount, capacity,
                [], 0, null, "draft_completed", evaluatedAt);
        }

        if (session.Status == SessionStatus.Drafting)
        {
            return Response(
                session, MatchLifecycleStage.Drafting, "Đang draft",
                "Draft đang chạy; roster không nên bị sửa giữa chừng.",
                "Tiếp tục draft theo flow hiện tại. Không cần mở thêm màn admin.",
                MatchLifecycleOwner.System,
                false, null, null, presentCount, presentCount, capacity,
                [], 0, null, "draft_in_progress", evaluatedAt);
        }

        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
        {
            return Response(
                session, MatchLifecycleStage.NeedsSetup, "Cần cấu hình một lần",
                "Session chưa gắn Zalo connection/group nên bot chưa thể theo dõi poll và hội thoại.",
                "Liên kết group hoặc bật Auto Session cho group. Sau bước bootstrap này, flow tuần sau có thể bắt đầu từ poll.",
                MatchLifecycleOwner.AdminWebsite,
                true, "auto-session-control", null, presentCount, presentCount, capacity,
                [], 0, null, "zalo_group_not_linked", evaluatedAt);
        }

        if (session.StartTime is null)
        {
            return Response(
                session, MatchLifecycleStage.NeedsSetup, "Thiếu giờ đấu",
                "Chưa có giờ bắt đầu nên bot không thể tính cửa sổ reminder, guest và draft escalation.",
                "Bổ sung giờ đấu hoặc để Auto Session lấy giờ từ poll.",
                MatchLifecycleOwner.AdminWebsite,
                true, "draft-workspace", null, presentCount, presentCount, capacity,
                [], 0, null, "start_time_missing", evaluatedAt);
        }

        if (!session.BotEnabled)
        {
            return Response(
                session, MatchLifecycleStage.NeedsSetup, "Bot đang tắt",
                "Session đã gắn Zalo nhưng bot đang tắt nên lifecycle không thể tự tiếp tục.",
                "Bật bot cho session này nếu muốn dùng zero-web flow.",
                MatchLifecycleOwner.AdminWebsite,
                true, "bot-overbook-control", null, presentCount, presentCount, capacity,
                [], 0, null, "bot_disabled", evaluatedAt);
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, evaluatedAt, cancellationToken);
        if (readiness is null)
        {
            return Response(
                session, MatchLifecycleStage.NeedsAttention, "Không đọc được readiness",
                "Backend chưa dựng được snapshot readiness an toàn cho session này.",
                "Mở session để kiểm tra liên kết Zalo và dữ liệu trận. Bot không tự đoán trạng thái.",
                MatchLifecycleOwner.AdminWebsite,
                true, "draft-workspace", null, presentCount, presentCount, capacity,
                [], 0, null, "readiness_unavailable", evaluatedAt);
        }

        var decision = await new ZaloDraftPreparationDecisionStore(db)
            .GetAsync(session.Id, cancellationToken);
        var leaderDecision = decision?.Kind.ToString();
        var decisionMatchesRoster = decision is not null &&
            (decision.Kind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster || decision.MatchesRoster(readiness));
        if (decision is not null && !decisionMatchesRoster)
            leaderDecision = $"{decision.Kind} (stale)";

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
        {
            return Response(
                session, MatchLifecycleStage.Stopped, "Trưởng/phó đã dừng kèo",
                "Draft-preparation reminders đã có quyết định StopMatch cho session này.",
                "Không tự xóa session. Chỉ thực hiện thao tác hủy/xóa riêng khi organizer thật sự muốn.",
                MatchLifecycleOwner.Leader,
                false, null, null,
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision, "leader_stopped_match", evaluatedAt);
        }

        var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);
        if (activeSlotRisks > 0)
        {
            return Response(
                session, MatchLifecycleStage.ResolvingPassSlots,
                activeSlotRisks == 1 ? "Đang xử lý 1 pass slot" : $"Đang xử lý {activeSlotRisks} pass slot",
                "Có offer pass/claim còn hiệu lực nên roster chưa được coi là sạch để chốt draft.",
                "Để pass-slot/rescue flow xử lý trên Zalo. Chưa cần mở web chỉ vì offer đang mở.",
                MatchLifecycleOwner.ZaloBot,
                false, null, null,
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, activeSlotRisks, leaderDecision,
                "active_pass_slot_risk", evaluatedAt);
        }

        var overbook = await new ZaloOverbookStateStore(db).GetAsync(session.Id, cancellationToken);
        var overbookMatchesRoster = overbook is not null &&
            overbook.LastObservedAt is not null &&
            overbook.EffectiveSlotCount == readiness.EffectiveSlotCount;

        if (readiness.State == ZaloDraftReadinessState.RosterOverCapacity)
        {
            if (overbookMatchesRoster && overbook!.NeedsConfirmation && overbook.ExcessSlotCount > 0)
            {
                return Response(
                    session, MatchLifecycleStage.ResolvingOverbook, "Cần xác nhận dư slot",
                    $"Roster đang {readiness.EffectiveSlotCount}/{readiness.Capacity}; backend không đủ bằng chứng để tự chọn target vượt slot.",
                    "Đây là exception thật: admin xác nhận đúng target dư slot ở khu vực Overbook rồi automation mới tiếp tục.",
                    MatchLifecycleOwner.AdminWebsite,
                    true, "bot-overbook-control", null,
                    readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                    readiness.MissingProfileNames, 0, leaderDecision,
                    "overbook_target_confirmation_required", evaluatedAt);
            }

            if (overbook?.Enabled == true)
            {
                return Response(
                    session, MatchLifecycleStage.ResolvingOverbook, "Bot đang xử lý dư slot",
                    $"Roster đang {readiness.EffectiveSlotCount}/{readiness.Capacity}. Overbook automation đang bật.",
                    "Để worker sync/nhắc theo state hiện tại. Chỉ mở web nếu bot chuyển sang trạng thái cần confirmation.",
                    MatchLifecycleOwner.ZaloBot,
                    false, null, null,
                    readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                    readiness.MissingProfileNames, 0, leaderDecision,
                    "overbook_automation_active", evaluatedAt);
            }

            return Response(
                session, MatchLifecycleStage.ResolvingOverbook, "Dư slot chưa có automation",
                $"Roster đang {readiness.EffectiveSlotCount}/{readiness.Capacity} nhưng Overbook automation chưa bật.",
                "Bật/cấu hình Overbook hoặc xử lý roster thủ công.",
                MatchLifecycleOwner.AdminWebsite,
                true, "bot-overbook-control", null,
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision,
                "overbook_automation_disabled", evaluatedAt);
        }

        if (readiness.MissingProfileCount > 0 &&
            readiness.EffectiveSlotCount >= Math.Min(readiness.Capacity, Math.Max(1, session.TeamCount * 2)))
        {
            return Response(
                session, MatchLifecycleStage.AwaitingProfiles,
                $"Thiếu hồ sơ · {readiness.MissingProfileCount}",
                $"Roster còn {readiness.MissingProfileCount} hồ sơ chưa đủ dữ liệu authoritative: {FormatNames(readiness.MissingProfileNames)}.",
                "Hỏi/cập nhật ngay trong Zalo; không cần mở form web. Draft vẫn bị chặn cho tới khi backend readiness sạch.",
                MatchLifecycleOwner.ZaloBot,
                false, null, $"ai chưa cập nhật hồ sơ {session.Name}",
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision,
                "profiles_incomplete", evaluatedAt);
        }

        if (readiness.State is ZaloDraftReadinessState.RosterNotFull or ZaloDraftReadinessState.NoRoster)
        {
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
            {
                return Response(
                    session, MatchLifecycleStage.Recruiting, "Đang tiếp tục gom người",
                    $"Trưởng/phó đã chọn KeepRecruiting; roster hiện {readiness.EffectiveSlotCount}/{readiness.Capacity} effective slot.",
                    "KeepRecruiting/roster-change coordinator tiếp tục sync và phản ứng với delta. Client không cần canh website.",
                    MatchLifecycleOwner.ZaloBot,
                    false, null, null,
                    readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                    readiness.MissingProfileNames, 0, leaderDecision,
                    "leader_keep_recruiting", evaluatedAt);
            }

            if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster && decisionMatchesRoster)
            {
                var canDivide = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(
                    readiness.EffectiveSlotCount,
                    Math.Max(1, session.TeamCount));
                if (readiness.MissingProfileCount > 0)
                {
                    return Response(
                        session, MatchLifecycleStage.AwaitingProfiles,
                        $"Đã chốt roster · thiếu {readiness.MissingProfileCount} hồ sơ",
                        $"Trưởng/phó đã chốt chơi {readiness.EffectiveSlotCount} effective slot nhưng còn hồ sơ thiếu: {FormatNames(readiness.MissingProfileNames)}.",
                        "Hoàn tất hồ sơ trên Zalo trước. Quyết định PlayCurrentRoster chỉ còn hợp lệ khi fingerprint roster không đổi.",
                        MatchLifecycleOwner.ZaloBot,
                        false, null, $"ai chưa cập nhật hồ sơ {session.Name}",
                        readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                        readiness.MissingProfileNames, 0, leaderDecision,
                        "partial_roster_profiles_incomplete", evaluatedAt);
                }

                if (canDivide)
                {
                    return Response(
                        session, MatchLifecycleStage.ReadyForDraft, "Roster hiện tại đã được chốt",
                        $"Trưởng/phó đã chốt {readiness.EffectiveSlotCount} effective slot; engine có thể chia đều {session.TeamCount} team.",
                        "Nói `draft đi` trên Zalo. Final mutation vẫn re-sync poll, kiểm tra fingerprint, quyền, pass-slot và profile lần cuối.",
                        MatchLifecycleOwner.Leader,
                        false, null, "draft đi",
                        readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                        readiness.MissingProfileNames, 0, leaderDecision,
                        "partial_roster_approved_and_draftable", evaluatedAt);
                }

                return Response(
                    session, MatchLifecycleStage.AwaitingLeaderDecision, "Đã chốt chơi · auto-draft chưa chia đều",
                    $"Trưởng/phó vẫn chọn chơi với {readiness.EffectiveSlotCount} effective slot, nhưng engine không thể chia đều cho {session.TeamCount} team.",
                    "Bot tiếp tục theo dõi roster. Nếu cần, điều chỉnh shared/rotation hoặc roster; đừng hiểu trạng thái này là kèo không thể chơi.",
                    MatchLifecycleOwner.Leader,
                    false, null, null,
                    readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                    readiness.MissingProfileNames, 0, leaderDecision,
                    "partial_roster_approved_not_draftable", evaluatedAt);
            }

            var draftable = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(
                readiness.EffectiveSlotCount,
                Math.Max(1, session.TeamCount));
            var draftability = draftable
                ? $" Số này có thể chia đều {session.TeamCount} team nếu leader muốn chốt roster hiện tại."
                : string.Empty;
            return Response(
                session, MatchLifecycleStage.AwaitingLeaderDecision, "Chờ trưởng/phó chọn hướng",
                $"Roster hiện {readiness.EffectiveSlotCount}/{readiness.Capacity} effective slot.{draftability}",
                "Không suy ra hủy kèo hay tự tuyển thêm chỉ từ số người. Trưởng/phó chọn `kiếm thêm` hoặc chốt roster hiện tại trên Zalo.",
                MatchLifecycleOwner.Leader,
                false, null, null,
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision,
                "partial_roster_needs_leader_decision", evaluatedAt);
        }

        if (readiness.State == ZaloDraftReadinessState.Ready)
        {
            return Response(
                session, MatchLifecycleStage.ReadyForDraft, "Sẵn sàng chốt draft",
                $"Backend readiness sạch: {readiness.EffectiveSlotCount}/{readiness.Capacity} effective slot, hồ sơ đủ và không có pass-slot đang mở.",
                "Trưởng/phó nói `draft đi` trên Zalo. DraftAutopilot vẫn fresh-sync và đi qua confirmation/authorization gate trước mutation.",
                MatchLifecycleOwner.Leader,
                false, null, "draft đi",
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision,
                "draft_ready", evaluatedAt);
        }

        if (readiness.State == ZaloDraftReadinessState.MissingProfiles)
        {
            return Response(
                session, MatchLifecycleStage.AwaitingProfiles,
                $"Thiếu hồ sơ · {readiness.MissingProfileCount}",
                $"Backend readiness đang chặn draft vì hồ sơ: {FormatNames(readiness.MissingProfileNames)}.",
                "Hoàn tất hồ sơ ngay trên Zalo rồi để bot kiểm tra lại.",
                MatchLifecycleOwner.ZaloBot,
                false, null, $"ai chưa cập nhật hồ sơ {session.Name}",
                readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
                readiness.MissingProfileNames, 0, leaderDecision,
                readiness.ReasonCode, evaluatedAt);
        }

        return Response(
            session, MatchLifecycleStage.NeedsAttention, "Cần kiểm tra trạng thái trận",
            $"Readiness đang chặn automation: {readiness.ReasonCode}.",
            "Đây không phải case bot nên đoán. Mở đúng session để kiểm tra trạng thái/draft hiện tại.",
            MatchLifecycleOwner.AdminWebsite,
            true, "draft-workspace", null,
            readiness.PresentPlayerCount, readiness.EffectiveSlotCount, readiness.Capacity,
            readiness.MissingProfileNames, 0, leaderDecision,
            readiness.ReasonCode, evaluatedAt);
    }

    private async Task<int> CountActiveSlotRisksAsync(
        MatchSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return 0;

        var ownerIds = await db.SessionPlayers
            .AsNoTracking()
            .Where(player =>
                player.SessionId == session.Id &&
                player.IsPresent &&
                player.PlayerProfile != null &&
                player.PlayerProfile.ZaloUserId != null)
            .Select(player => player.PlayerProfile!.ZaloUserId!)
            .ToListAsync(cancellationToken);
        ownerIds = ownerIds
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ownerIds.Count == 0) return 0;

        var store = new ZaloOpenSlotOfferStore(db);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ownerId in ownerIds)
        {
            var offers = await store.ListOwnedActiveAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                ownerId,
                cancellationToken);
            foreach (var offer in offers)
            {
                if (string.Equals(offer.SessionId, session.Id, StringComparison.Ordinal))
                    active.Add(offer.Id);
            }
        }
        return active.Count;
    }

    private static MatchLifecycleResponse Response(
        MatchSession session,
        MatchLifecycleStage stage,
        string stageLabel,
        string headline,
        string nextStep,
        MatchLifecycleOwner owner,
        bool needsWebsite,
        string? webTarget,
        string? suggestedZaloCommand,
        int presentPlayerCount,
        int effectiveSlotCount,
        int capacity,
        IReadOnlyList<string> missingProfileNames,
        int activeSlotRiskCount,
        string? leaderDecision,
        string reasonCode,
        DateTimeOffset evaluatedAt) => new(
            session.Id,
            session.Name,
            stage,
            stageLabel,
            headline,
            nextStep,
            owner,
            needsWebsite,
            webTarget,
            suggestedZaloCommand,
            session.StartTime,
            presentPlayerCount,
            effectiveSlotCount,
            capacity,
            missingProfileNames.Count,
            missingProfileNames,
            activeSlotRiskCount,
            leaderDecision,
            reasonCode,
            evaluatedAt);

    private static string FormatNames(IReadOnlyList<string> names) =>
        names.Count == 0
            ? "chưa xác định"
            : string.Join(", ", names.Take(6)) + (names.Count > 6 ? "…" : string.Empty);
}
