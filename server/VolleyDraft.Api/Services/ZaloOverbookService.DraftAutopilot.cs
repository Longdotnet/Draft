using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string DraftSessionChoiceIntent = "DraftReadinessSessionChoice";
    private const string DraftAutopilotIntent = "DraftAutopilot";

    private sealed record DraftAutopilotSettings(
        bool Enabled,
        bool NaturalReadinessEnabled,
        bool EscalationEnabled,
        bool ProactiveEnabled,
        int SoftNudgeHoursBeforeStart,
        int ApproverNudgeHoursBeforeStart,
        int FallbackApproverMinutes,
        int StopNudgingMinutesBeforeStart,
        int RequestTtlMinutes,
        int RequesterConsentMinutes,
        int TargetedConfirmationMinutes,
        int RecentApproverActivityHours,
        int FallbackActivityHours,
        int MaxApproverTags,
        int MaxSendsPerCycle)
    {
        public static DraftAutopilotSettings FromConfiguration(IConfiguration configuration) => new(
            Enabled: configuration.GetValue("ZaloBot:DraftAutopilot:Enabled", true),
            NaturalReadinessEnabled: configuration.GetValue("ZaloBot:DraftAutopilot:NaturalReadinessEnabled", true),
            EscalationEnabled: configuration.GetValue("ZaloBot:DraftAutopilot:EscalationEnabled", true),
            ProactiveEnabled: configuration.GetValue("ZaloBot:DraftAutopilot:ProactiveEnabled", true),
            SoftNudgeHoursBeforeStart: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:SoftNudgeHoursBeforeStart", 3), 1, 12),
            ApproverNudgeHoursBeforeStart: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:ApproverNudgeHoursBeforeStart", 2), 1, 12),
            FallbackApproverMinutes: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:FallbackApproverMinutes", 40), 10, 180),
            StopNudgingMinutesBeforeStart: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:StopNudgingMinutesBeforeStart", 30), 10, 180),
            RequestTtlMinutes: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:RequestTtlMinutes", 90), 10, 240),
            RequesterConsentMinutes: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:RequesterConsentMinutes", 15), 2, 60),
            TargetedConfirmationMinutes: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:TargetedConfirmationMinutes", 20), 2, 60),
            RecentApproverActivityHours: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:RecentApproverActivityHours", 6), 1, 48),
            FallbackActivityHours: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:FallbackActivityHours", 24), 1, 168),
            MaxApproverTags: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:MaxApproverTags", 2), 1, 3),
            MaxSendsPerCycle: Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:MaxSendsPerCycle", 3), 1, 10));
    }

    private sealed record DraftApproverCandidate(
        string ZaloUserId,
        string DisplayName,
        bool IsCreator,
        DateTimeOffset? LastMessageAt);

    private sealed record DraftApproverResolution(
        bool RoleLookupSucceeded,
        IReadOnlyList<DraftApproverCandidate> Candidates,
        string? Error = null);

    private async Task<bool> TryHandleDraftAutopilotAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled) return false;

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (senderId.Length == 0 || senderId == ZaloOverbookLogic.NormalizeId(incoming.BotId)) return false;

        var escalationStore = new ZaloDraftEscalationStore(db);
        var approverRequest = await escalationStore.LoadActiveForApproverAsync(
            connectionId, groupId, senderId, cancellationToken);
        if (approverRequest is not null && approverRequest.State != ZaloDraftEscalationState.Expired)
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            var targeted = IsTargetedDraftTurn(approverRequest, senderId, quote, settings);
            if (targeted && ZaloDraftConversationPolicy.IsEscalationCancel(incoming.Content))
            {
                await escalationStore.SetStateAsync(
                    approverRequest.Id,
                    ZaloDraftEscalationState.Cancelled,
                    cancellationToken);
                await RemoveDraftPendingAsync(
                    connectionId, groupId, senderId, approverRequest.SessionId, cancellationToken);
                await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    "Ok, tui dừng yêu cầu chốt draft này. Đội hình chưa bị thay đổi.",
                    [], "draft_escalation_cancelled", cancellationToken);
                return true;
            }

            if (targeted && ZaloDraftConversationPolicy.IsWeakConfirmation(incoming.Content))
            {
                await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    "Tui chưa lấy câu đó làm xác nhận draft nha 😆 Nếu muốn chạy thật, nói rõ `draft đi` hoặc `xác nhận draft` giúp tui.",
                    [], "draft_confirmation_weak", cancellationToken);
                return true;
            }

            if (ZaloDraftConversationPolicy.IsStrongDraftConfirmation(incoming.Content))
            {
                if (!targeted) return false;
                return await HandleDraftApprovalAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    approverRequest, settings, escalationStore, cancellationToken);
            }
        }

        // Never let a strong confirmation quoted from another approver's prompt cross
        // sender boundaries. Quote correlation is stronger than the short time window.
        if (ZaloDraftConversationPolicy.IsStrongDraftConfirmation(incoming.Content))
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            if (!string.IsNullOrWhiteSpace(quote.MessageId))
            {
                var active = await escalationStore.LoadActiveAsync(cancellationToken);
                var quotedRequest = active.FirstOrDefault(request =>
                    request.GroupId == groupId &&
                    (string.Equals(request.PrimaryApproverMessageId, quote.MessageId, StringComparison.Ordinal) ||
                     string.Equals(request.SecondaryApproverMessageId, quote.MessageId, StringComparison.Ordinal)));
                if (quotedRequest is not null)
                {
                    await SendDraftReplyAsync(
                        connectionId, accountId, botName, groupId, incoming,
                        "Tin xác nhận này đang chờ đúng người tui vừa gọi. Tui chưa chạy draft để tránh lấy nhầm quyền của người khác.",
                        [], "draft_confirmation_wrong_sender", cancellationToken);
                    return true;
                }
            }
        }

        var requesterRequest = await escalationStore.LoadActiveForRequesterAsync(
            connectionId, groupId, senderId, cancellationToken);
        if (requesterRequest is not null &&
            requesterRequest.State == ZaloDraftEscalationState.AwaitingRequesterConsent)
        {
            if (ZaloDraftConversationPolicy.IsEscalationCancel(incoming.Content))
            {
                await escalationStore.SetStateAsync(
                    requesterRequest.Id,
                    ZaloDraftEscalationState.Cancelled,
                    cancellationToken);
                await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    "Ok, tui không gọi trưởng/phó nữa nha. Khi nào cần cứ hỏi lại đội hình.",
                    [], "draft_escalation_cancelled", cancellationToken);
                return true;
            }

            if (ZaloDraftConversationPolicy.IsEscalationConsent(incoming.Content))
            {
                return await HandleRequesterEscalationConsentAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    requesterRequest, settings, escalationStore, cancellationToken);
            }
        }

        if (!settings.NaturalReadinessEnabled) return false;
        var selectionStore = new ZaloConversationStateV2Store(db);
        var selectionState = await selectionStore.LoadActiveAsync(groupId, senderId, cancellationToken);
        var continuingSelection = selectionState is not null &&
                                  string.Equals(selectionState.Intent, DraftSessionChoiceIntent, StringComparison.Ordinal);
        if (!continuingSelection && !ZaloDraftConversationPolicy.IsReadinessQuestion(incoming.Content))
            return false;

        return await HandleDraftReadinessQuestionAsync(
            connectionId, accountId, botName, groupId, incoming,
            continuingSelection ? selectionState : null,
            settings, escalationStore, cancellationToken);
    }

    private async Task<bool> HandleDraftReadinessQuestionAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloConversationStateV2Snapshot? selectionState,
        DraftAutopilotSettings settings,
        ZaloDraftEscalationStore escalationStore,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        sessions = sessions
            .Where(session => session.StartTime is null || session.StartTime >= now.AddHours(-4))
            .OrderBy(session => session.StartTime ?? DateTimeOffset.MaxValue)
            .ThenByDescending(session => session.UpdatedAt)
            .ToList();
        if (sessions.Count == 0) return false;

        IReadOnlyList<string>? candidateIds = null;
        if (selectionState is not null)
        {
            try
            {
                candidateIds = JsonSerializer.Deserialize<List<string>>(selectionState.CandidateEntitiesJson);
            }
            catch (JsonException)
            {
                candidateIds = null;
            }

            if (candidateIds is { Count: > 0 })
                sessions = sessions.Where(session => candidateIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
        }

        var normalized = ZaloDraftConversationPolicy.Normalize(incoming.Content);
        var references = sessions
            .Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime))
            .ToList();
        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(normalized, references);
        var matches = sessions.Where(session => matchedIds.Contains(session.Id, StringComparer.Ordinal)).ToList();
        MatchSession? selected = matches.Count == 1
            ? matches[0]
            : matches.Count == 0 && sessions.Count == 1
                ? sessions[0]
                : null;

        if (selected is null)
        {
            var candidates = matches.Count > 1 ? matches : sessions;
            var selectionStore = new ZaloConversationStateV2Store(db);
            var currentState = await selectionStore.LoadActiveAsync(groupId, incoming.SenderId, cancellationToken);
            if (currentState is null || string.Equals(currentState.Intent, DraftSessionChoiceIntent, StringComparison.Ordinal))
            {
                await selectionStore.SaveActiveAsync(
                    groupId,
                    ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                    DraftSessionChoiceIntent,
                    "{}",
                    "[]",
                    JsonSerializer.Serialize(candidates.Take(8).Select(item => item.Id).ToList()),
                    incoming.MessageId,
                    incoming.MessageId,
                    now.AddMinutes(settings.RequesterConsentMinutes),
                    cancellationToken);
            }

            var choices = string.Join(", ", candidates.Take(4).Select(FormatDraftSessionChoice));
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                $"Ông hỏi đội hình trận nào: {choices}? Chỉ cần trả lời T4/T6, ngày hoặc tên trận, không cần @bot.",
                [], "draft_session_ambiguous", cancellationToken);
            return true;
        }

        if (selectionState is not null)
        {
            await new ZaloConversationStateV2Store(db)
                .CompleteAsync(groupId, ZaloOverbookLogic.NormalizeId(incoming.SenderId), cancellationToken);
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(selected.Id, now, cancellationToken);
        if (readiness is null) return false;

        if (readiness.State == ZaloDraftReadinessState.AlreadyDrafted)
        {
            await WriteDraftTraceAsync(incoming, groupId, selected.Id, "draft_already_exists", cancellationToken);
            if (botService is not null)
            {
                await botService.HandleIncomingAsync(PromoteToBot(incoming, $"10 {selected.Name}"), cancellationToken);
                return true;
            }

            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                $"{selected.Name} có đội hình rồi nha. Tui chưa chạy draft lại.",
                [], "draft_already_exists", cancellationToken);
            return true;
        }

        if (!readiness.CanEscalate)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                BuildReadinessBlockerText(readiness),
                [], readiness.ReasonCode, cancellationToken);
            return true;
        }

        var roleResolution = await ResolveDraftApproversAsync(selected, settings, cancellationToken);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);

        // Live group role is the authorization source of truth. Message activity is
        // only used below to rank whom the bot should proactively tag.
        var senderAuthorization = await integration.GetGroupRoleAuthorizationAsync(
            selected.AdminUserId, selected.Id, senderId);
        var senderIsLeader = senderAuthorization.IsSuccess &&
                             senderAuthorization.Value?.CanOperateBot == true;

        var consentExpiry = GetRequestExpiry(
            readiness.StartTime, now, settings, settings.RequesterConsentMinutes);
        var request = await escalationStore.CreateOrReuseAsync(
            connectionId,
            groupId,
            selected.Id,
            "Member",
            senderId,
            incoming.SenderName,
            incoming.MessageId,
            readiness.Fingerprint,
            ZaloDraftEscalationState.AwaitingRequesterConsent,
            consentExpiry,
            cancellationToken);

        if (senderIsLeader)
        {
            var approvalExpiry = GetRequestExpiry(
                readiness.StartTime, now, settings, settings.RequestTtlMinutes);
            if (!await SeedDraftConfirmationAsync(
                    connectionId, groupId, senderId, selected.Id,
                    approvalExpiry, cancellationToken,
                    refuseToOverwriteDifferentPending: true))
            {
                await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    "Ông đang có một yêu cầu bot khác chờ xác nhận, nên tui chưa mở thêm lượt draft để khỏi nhập nhằng. Xử lý/huỷ yêu cầu kia rồi hỏi lại đội hình nha.",
                    [], "draft_confirmation_pending_conflict", cancellationToken);
                return true;
            }

            try
            {
                var sent = await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    BuildLeaderReadyText(readiness),
                    [], "draft_ready", cancellationToken);
                await escalationStore.SetPrimaryApproverAsync(
                    request.Id, senderId, sent, now, approvalExpiry, cancellationToken);
            }
            catch
            {
                await RemoveDraftPendingAsync(connectionId, groupId, senderId, selected.Id, cancellationToken);
                throw;
            }
            return true;
        }

        await SendDraftReplyAsync(
            connectionId, accountId, botName, groupId, incoming,
            BuildMemberReadyText(readiness, roleResolution.RoleLookupSucceeded),
            [], "draft_ready", cancellationToken);
        return true;
    }

    private async Task<bool> HandleRequesterEscalationConsentAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloDraftEscalationSnapshot request,
        DraftAutopilotSettings settings,
        ZaloDraftEscalationStore escalationStore,
        CancellationToken cancellationToken)
    {
        if (!settings.EscalationEnabled)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Phần gọi người chốt draft đang tắt trong cấu hình nha.",
                [], "draft_escalation_disabled", cancellationToken);
            return true;
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(request.SessionId, cancellationToken: cancellationToken);
        if (readiness is null || !readiness.CanEscalate)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Superseded, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                readiness is null
                    ? "Tui không còn đọc được kèo này nên chưa gọi ai chốt draft."
                    : BuildReadinessBlockerText(readiness),
                [], readiness?.ReasonCode ?? "draft_blocked_session_missing", cancellationToken);
            return true;
        }

        if (!string.Equals(readiness.Fingerprint, request.RosterFingerprint, StringComparison.Ordinal))
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Superseded, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Danh sách vừa thay đổi từ lúc tui hỏi ông. Tui chưa gọi ai để tránh chốt trên dữ liệu cũ; hỏi lại đội hình giúp tui nha.",
                [], "draft_roster_changed_after_prompt", cancellationToken);
            return true;
        }

        var session = await db.MatchSessions.AsNoTracking()
            .SingleAsync(item => item.Id == request.SessionId, cancellationToken);
        var resolved = await ResolveDraftApproversAsync(session, settings, cancellationToken);
        if (!resolved.RoleLookupSucceeded)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Tui chưa check được quyền trưởng/phó nhóm từ Zalo lúc này nên chưa tag ai. Thử lại một chút sau nha.",
                [], "draft_role_lookup_failed", cancellationToken);
            return true;
        }

        var expiry = GetRequestExpiry(
            readiness.StartTime, DateTimeOffset.UtcNow, settings, settings.RequestTtlMinutes);
        var candidate = await ReserveFirstDraftApproverAsync(
            resolved.Candidates,
            connectionId,
            groupId,
            request.SessionId,
            expiry,
            cancellationToken);
        if (candidate is null)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Các trưởng/phó phù hợp đang có một lượt bot khác chờ xác nhận, nên tui chưa tag chồng thêm lượt draft. Khi pending kia xong thì hỏi lại tui nha.",
                [], "draft_no_eligible_approver", cancellationToken);
            return true;
        }

        var body = BuildApproverPrompt(readiness, request.RequestedBySenderName);
        var outgoing = BuildMentionMessage(
            [candidate.ZaloUserId],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidate.ZaloUserId] = candidate.DisplayName
            },
            body);
        try
        {
            var providerMessageId = await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                outgoing.Message, outgoing.Mentions, "draft_escalation_created", cancellationToken);
            await escalationStore.SetPrimaryApproverAsync(
                request.Id,
                candidate.ZaloUserId,
                providerMessageId,
                DateTimeOffset.UtcNow,
                expiry,
                cancellationToken);
        }
        catch
        {
            await RemoveDraftPendingAsync(
                connectionId, groupId, candidate.ZaloUserId, request.SessionId, cancellationToken);
            throw;
        }
        return true;
    }

    private async Task<bool> HandleDraftApprovalAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloDraftEscalationSnapshot request,
        DraftAutopilotSettings settings,
        ZaloDraftEscalationStore escalationStore,
        CancellationToken cancellationToken)
    {
        if (request.State == ZaloDraftEscalationState.Executing)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Tui đang xử lý lượt chốt draft trước rồi, không chạy thêm lần hai nha.",
                [], "draft_execution_duplicate", cancellationToken);
            return true;
        }

        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Expired, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Yêu cầu chốt draft này hết hạn rồi. Tui chưa chạy gì cả; hỏi lại đội hình để tui kiểm tra dữ liệu mới nha.",
                [], "draft_confirmation_expired", cancellationToken);
            return true;
        }

        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.SessionId, cancellationToken);
        if (session is null)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Superseded, cancellationToken);
            return true;
        }

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var authorization = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId, session.Id, senderId);
        if (!authorization.IsSuccess)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Tui chưa xác minh được quyền trưởng/phó nhóm từ Zalo nên chưa chạy draft. Tui giữ nguyên dữ liệu.",
                [], "draft_role_lookup_failed", cancellationToken);
            return true;
        }
        if (authorization.Value?.CanOperateBot != true)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Quyền trưởng/phó của người xác nhận không còn hợp lệ, nên tui chưa chạy draft.",
                [], "draft_confirmation_wrong_sender", cancellationToken);
            return true;
        }

        var beforeRefresh = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, cancellationToken: cancellationToken);
        if (beforeRefresh?.State == ZaloDraftReadinessState.AlreadyDrafted)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Completed, cancellationToken);
            await RemoveDraftPendingAsync(connectionId, groupId, senderId, session.Id, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                $"{session.Name} vừa có đội hình rồi, nên tui không draft lần hai.",
                [], "draft_already_exists", cancellationToken);
            return true;
        }

        if (beforeRefresh?.HasLinkedPoll == true)
        {
            var synced = await integration.SyncLatestPollAsync(session.AdminUserId, session.Id);
            if (!synced.IsSuccess)
            {
                await SendDraftReplyAsync(
                    connectionId, accountId, botName, groupId, incoming,
                    $"Tui chưa đồng bộ lại được poll của {session.Name}, nên chưa draft trên dữ liệu có thể cũ. Chi tiết: {synced.Error}",
                    [], "draft_blocked_poll_refresh_failed", cancellationToken);
                return true;
            }
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, cancellationToken: cancellationToken);
        if (readiness is null || readiness.State == ZaloDraftReadinessState.AlreadyDrafted)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Completed, cancellationToken);
            await RemoveDraftPendingAsync(connectionId, groupId, senderId, session.Id, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                $"{session.Name} đã được chốt đội hình rồi, tui không chạy trùng.",
                [], "draft_already_exists", cancellationToken);
            return true;
        }

        if (!readiness.CanEscalate ||
            !string.Equals(readiness.Fingerprint, request.RosterFingerprint, StringComparison.Ordinal))
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Superseded, cancellationToken);
            await RemoveDraftPendingAsync(connectionId, groupId, senderId, session.Id, cancellationToken);
            var fingerprintChanged = !string.Equals(
                readiness.Fingerprint, request.RosterFingerprint, StringComparison.Ordinal);
            var reason = fingerprintChanged
                ? "draft_roster_changed_after_prompt"
                : readiness.ReasonCode;
            var text = fingerprintChanged
                ? $"Danh sách {session.Name} vừa đổi từ lúc tui gọi ông. Xác nhận cũ không còn hợp lệ nên tui chưa draft; hỏi lại đội hình để tạo lượt chốt mới nha."
                : BuildReadinessBlockerText(readiness);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                text, [], reason, cancellationToken);
            return true;
        }

        var claim = await escalationStore.TryClaimExecutionAsync(
            request, senderId, readiness.Fingerprint, cancellationToken);
        if (claim is null)
        {
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Có một lượt chốt draft khác vừa thắng trước rồi. Tui không chạy trùng nha.",
                [], "draft_execution_duplicate", cancellationToken);
            return true;
        }
        await WriteDraftTraceAsync(
            incoming, groupId, session.Id, "draft_execution_claimed", cancellationToken);

        if (!await SeedDraftConfirmationAsync(
                connectionId, groupId, senderId, session.Id,
                request.ExpiresAt, cancellationToken,
                refuseToOverwriteDifferentPending: true))
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.ApproverTagged, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Ông đang có một yêu cầu bot khác chờ xác nhận. Tui chưa ghi đè nó để chạy draft; xử lý/huỷ yêu cầu kia rồi nói `draft đi` lại nha.",
                [], "draft_confirmation_pending_conflict", cancellationToken);
            return true;
        }

        if (botService is null)
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.ApproverTagged, cancellationToken);
            await SendDraftReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Draft router hiện chưa sẵn sàng nên tui chưa chạy. Dữ liệu vẫn nguyên.",
                [], "draft_execution_failed", cancellationToken);
            return true;
        }

        try
        {
            // Reuse the existing confirmation router so authorization history,
            // SessionDraftService.AutoRunDraftAsync and its atomic session lease stay
            // the single mutation path.
            await botService.HandleIncomingAsync(
                PromoteToBot(incoming, "xác nhận draft"), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Natural draft approval failed Group={GroupId} Session={SessionId} Sender={SenderId}",
                groupId,
                session.Id,
                senderId);
        }

        var terminalStatus = await db.MatchSessions.AsNoTracking()
            .Where(item => item.Id == session.Id)
            .Select(item => item.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (ZaloDraftApprovalSafety.IsDraftCompleted(terminalStatus))
        {
            await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Completed, cancellationToken);
            await RemoveDraftPendingAsync(connectionId, groupId, senderId, session.Id, cancellationToken);
            await WriteDraftTraceAsync(
                incoming, groupId, session.Id, "draft_execution_completed", cancellationToken);
            return true;
        }

        // Failed/blocked router attempts remain retryable, but still bound to the
        // exact same session and approver. Team rows alone never count as success.
        await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.ApproverTagged, cancellationToken);
        await SeedDraftConfirmationAsync(
            connectionId, groupId, senderId, session.Id,
            request.ExpiresAt, cancellationToken,
            refuseToOverwriteDifferentPending: true);
        await WriteDraftTraceAsync(
            incoming, groupId, session.Id, "draft_execution_failed", cancellationToken);
        return true;
    }

    public async Task<int> ProcessDraftAutopilotDueAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled || !settings.ProactiveEnabled || !settings.EscalationEnabled) return 0;

        var now = DateTimeOffset.UtcNow;
        var all = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item => item.BotEnabled &&
                           item.ZaloConnection != null &&
                           item.ZaloGroupId != null &&
                           (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                           item.StartTime != null)
            .ToListAsync(cancellationToken);
        var candidates = all
            .Where(item => item.StartTime > now.AddMinutes(settings.StopNudgingMinutesBeforeStart) &&
                           item.StartTime <= now.AddHours(settings.SoftNudgeHoursBeforeStart))
            .GroupBy(item => $"{item.ZaloConnectionId}:{item.ZaloGroupId}", StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.StartTime).First())
            .OrderBy(item => item.StartTime)
            .Take(30)
            .ToList();

        var escalationStore = new ZaloDraftEscalationStore(db);
        var sent = 0;
        foreach (var session in candidates)
        {
            if (sent >= settings.MaxSendsPerCycle) break;

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null || !readiness.CanEscalate) continue;

            var request = await escalationStore.LoadForSessionAsync(
                session.ZaloConnectionId!, session.ZaloGroupId!, session.Id, cancellationToken);
            if (request is not null &&
                (request.State is ZaloDraftEscalationState.Completed or ZaloDraftEscalationState.Cancelled) &&
                string.Equals(request.RosterFingerprint, readiness.Fingerprint, StringComparison.Ordinal))
                continue;

            if (request is not null &&
                (request.State is ZaloDraftEscalationState.AwaitingRequesterConsent or
                                  ZaloDraftEscalationState.ProactiveSoft or
                                  ZaloDraftEscalationState.ApproverTagged or
                                  ZaloDraftEscalationState.Executing) &&
                !string.Equals(request.RosterFingerprint, readiness.Fingerprint, StringComparison.Ordinal))
            {
                await escalationStore.SetStateAsync(request.Id, ZaloDraftEscalationState.Superseded, cancellationToken);
                if (request.PrimaryApproverId is not null)
                    await RemoveDraftPendingAsync(
                        session.ZaloConnectionId!, session.ZaloGroupId!, request.PrimaryApproverId,
                        session.Id, cancellationToken);
                if (request.SecondaryApproverId is not null)
                    await RemoveDraftPendingAsync(
                        session.ZaloConnectionId!, session.ZaloGroupId!, request.SecondaryApproverId,
                        session.Id, cancellationToken);
                request = null;
            }

            var untilStart = session.StartTime!.Value - now;
            var directApproverStage = untilStart <= TimeSpan.FromHours(settings.ApproverNudgeHoursBeforeStart);
            if (request is null || request.State is ZaloDraftEscalationState.Expired or ZaloDraftEscalationState.Superseded)
            {
                request = await escalationStore.CreateOrReuseAsync(
                    session.ZaloConnectionId!, session.ZaloGroupId!, session.Id,
                    "Proactive", null, null, null,
                    readiness.Fingerprint,
                    ZaloDraftEscalationState.ProactiveSoft,
                    GetRequestExpiry(readiness.StartTime, now, settings, settings.RequestTtlMinutes),
                    cancellationToken);
            }

            if (!directApproverStage && request.SoftNudgeSentAt is null)
            {
                var text = $"{readiness.SessionName} còn khoảng {FormatRemaining(untilStart)} nữa đánh, hiện {readiness.EffectiveSlotCount}/{readiness.Capacity} slot và dữ liệu đã đủ để draft. Chưa có đội hình nha. Ai cần team sớm cứ hỏi, tui sẽ gọi đúng người có quyền chốt 😆";
                await SendDraftProactiveAsync(
                    session,
                    text,
                    [],
                    $"draft-soft:{session.Id}:{readiness.Fingerprint}",
                    cancellationToken);
                await escalationStore.MarkSoftNudgeAsync(request.Id, now, cancellationToken);
                sent += 1;
                continue;
            }

            if (!directApproverStage) continue;

            if (request.PrimaryApproverId is null)
            {
                var resolved = await ResolveDraftApproversAsync(session, settings, cancellationToken);
                if (!resolved.RoleLookupSucceeded || resolved.Candidates.Count == 0)
                {
                    logger.LogWarning(
                        "Draft autopilot could not select approver Session={SessionId} Reason={Reason}",
                        session.Id,
                        resolved.RoleLookupSucceeded ? "draft_no_eligible_approver" : "draft_role_lookup_failed");
                    continue;
                }

                var expiry = GetRequestExpiry(
                    readiness.StartTime, now, settings, settings.RequestTtlMinutes);
                var primary = await ReserveFirstDraftApproverAsync(
                    resolved.Candidates,
                    session.ZaloConnectionId!,
                    session.ZaloGroupId!,
                    session.Id,
                    expiry,
                    cancellationToken);
                if (primary is null) continue;

                var outgoing = BuildMentionMessage(
                    [primary.ZaloUserId],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [primary.ZaloUserId] = primary.DisplayName
                    },
                    BuildApproverPrompt(readiness, null));
                try
                {
                    var providerId = await SendDraftProactiveAsync(
                        session,
                        outgoing.Message,
                        outgoing.Mentions,
                        $"draft-approver:{session.Id}:{readiness.Fingerprint}:1",
                        cancellationToken);
                    await escalationStore.SetPrimaryApproverAsync(
                        request.Id, primary.ZaloUserId, providerId, now, expiry, cancellationToken);
                }
                catch
                {
                    await RemoveDraftPendingAsync(
                        session.ZaloConnectionId!, session.ZaloGroupId!, primary.ZaloUserId,
                        session.Id, cancellationToken);
                    throw;
                }
                sent += 1;
                continue;
            }

            if (settings.MaxApproverTags < 2 ||
                request.SecondaryApproverId is not null ||
                request.PrimaryNudgeAt is null ||
                now - request.PrimaryNudgeAt < TimeSpan.FromMinutes(settings.FallbackApproverMinutes))
                continue;

            var fallback = await ResolveDraftApproversAsync(session, settings, cancellationToken);
            if (!fallback.RoleLookupSucceeded) continue;
            var secondaryExpiry = GetRequestExpiry(
                readiness.StartTime, now, settings, settings.RequestTtlMinutes);
            var secondary = await ReserveFirstDraftApproverAsync(
                fallback.Candidates,
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                session.Id,
                secondaryExpiry,
                cancellationToken,
                excludedApproverId: request.PrimaryApproverId);
            if (secondary is null) continue;

            var secondaryOutgoing = BuildMentionMessage(
                [secondary.ZaloUserId],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [secondary.ZaloUserId] = secondary.DisplayName
                },
                $"{readiness.SessionName} vẫn chưa có đội hình, còn khoảng {FormatRemaining(untilStart)}. Tui đã gọi một người trước nhưng chưa thấy chốt. Nếu ông đồng ý thì reply `draft đi` nha; tui sẽ kiểm tra lại roster trước khi chạy.");
            try
            {
                var secondaryProviderId = await SendDraftProactiveAsync(
                    session,
                    secondaryOutgoing.Message,
                    secondaryOutgoing.Mentions,
                    $"draft-approver:{session.Id}:{readiness.Fingerprint}:2",
                    cancellationToken);
                await escalationStore.SetSecondaryApproverAsync(
                    request.Id,
                    secondary.ZaloUserId,
                    secondaryProviderId,
                    now,
                    secondaryExpiry,
                    cancellationToken);
            }
            catch
            {
                await RemoveDraftPendingAsync(
                    session.ZaloConnectionId!, session.ZaloGroupId!, secondary.ZaloUserId,
                    session.Id, cancellationToken);
                throw;
            }
            sent += 1;
        }

        return sent;
    }

    private async Task<DraftApproverResolution> ResolveDraftApproversAsync(
        MatchSession session,
        DraftAutopilotSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return new(false, [], "session_not_linked");

        var connection = await db.ZaloConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == session.ZaloConnectionId, cancellationToken);
        if (connection is null) return new(false, [], "connection_missing");

        BridgeGroupRoles roles;
        try
        {
            using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
            roles = await bridge.GetGroupRolesAsync(document.RootElement.Clone(), session.ZaloGroupId);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(
                exception,
                "Could not load live Zalo roles for draft autopilot Session={SessionId}",
                session.Id);
            return new(false, [], exception.Message);
        }

        var creatorId = ZaloOverbookLogic.NormalizeId(roles.CreatorId);
        var roleIds = roles.AdminIds
            .Append(creatorId)
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(id => id.Length > 0 && id != ZaloOverbookLogic.NormalizeId(connection.AccountZaloId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (roleIds.Count == 0) return new(true, []);

        var currentMembers = await db.ZaloGroupMembers.AsNoTracking()
            .Where(member => member.ZaloConnectionId == session.ZaloConnectionId &&
                             member.GroupId == session.ZaloGroupId &&
                             member.IsCurrentMember &&
                             roleIds.Contains(member.ZaloUserId))
            .Select(member => new { member.ZaloUserId, member.DisplayName, member.LastSeenAt })
            .ToListAsync(cancellationToken);
        var currentIds = currentMembers
            .Select(member => ZaloOverbookLogic.NormalizeId(member.ZaloUserId))
            .ToHashSet(StringComparer.Ordinal);

        // The live role API is authoritative for the creator. Deputies need current
        // membership evidence to avoid tagging stale admin IDs from an old directory.
        var eligibleIds = roleIds
            .Where(id => id == creatorId || currentIds.Contains(id))
            .ToList();
        if (eligibleIds.Count == 0) return new(true, []);

        var messages = await db.ZaloGroupMessages.AsNoTracking()
            .Where(message => message.ZaloConnectionId == session.ZaloConnectionId &&
                              message.GroupId == session.ZaloGroupId &&
                              !message.IsFromBot &&
                              eligibleIds.Contains(message.SenderId))
            .Select(message => new { message.SenderId, message.SenderName, message.SentAt })
            .ToListAsync(cancellationToken);
        var latestById = messages
            .GroupBy(message => ZaloOverbookLogic.NormalizeId(message.SenderId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.SentAt).First(),
                StringComparer.Ordinal);
        var memberById = currentMembers
            .GroupBy(member => ZaloOverbookLogic.NormalizeId(member.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.LastSeenAt).First(),
                StringComparer.Ordinal);

        var current = DateTimeOffset.UtcNow;
        var recentCutoff = current.AddHours(-settings.RecentApproverActivityHours);
        var fallbackCutoff = current.AddHours(-settings.FallbackActivityHours);
        var candidates = eligibleIds
            .Select(id =>
            {
                latestById.TryGetValue(id, out var latest);
                memberById.TryGetValue(id, out var member);
                var displayName = !string.IsNullOrWhiteSpace(member?.DisplayName)
                    ? member!.DisplayName
                    : !string.IsNullOrWhiteSpace(latest?.SenderName)
                        ? latest!.SenderName
                        : id;
                return new DraftApproverCandidate(
                    id,
                    displayName,
                    id == creatorId,
                    latest?.SentAt);
            })
            // Activity influences selection order only; it never grants/revokes role.
            .OrderBy(candidate => candidate.LastMessageAt >= recentCutoff ? 0 :
                                  candidate.LastMessageAt >= fallbackCutoff ? 1 :
                                  candidate.IsCreator ? 2 : 3)
            .ThenByDescending(candidate => candidate.LastMessageAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.IsCreator)
            .ToList();
        return new(true, candidates);
    }

    private async Task<DraftApproverCandidate?> ReserveFirstDraftApproverAsync(
        IReadOnlyList<DraftApproverCandidate> candidates,
        string connectionId,
        string groupId,
        string sessionId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken,
        string? excludedApproverId = null)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(excludedApproverId) &&
                string.Equals(candidate.ZaloUserId, excludedApproverId, StringComparison.Ordinal))
                continue;

            if (await SeedDraftConfirmationAsync(
                    connectionId,
                    groupId,
                    candidate.ZaloUserId,
                    sessionId,
                    expiresAt,
                    cancellationToken,
                    refuseToOverwriteDifferentPending: true))
                return candidate;
        }

        return null;
    }

    private async Task<string?> SendDraftReplyAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string text,
        IReadOnlyList<BridgeOutgoingMention> mentions,
        string reason,
        CancellationToken cancellationToken)
    {
        var stored = await EnsureV2IncomingMessageAsync(
            connectionId, groupId, incoming, cancellationToken);
        if (stored.BotReplySentAt is not null) return null;

        var idempotencyKey = $"draft-auto:{reason}:{accountId}:{incoming.MessageId}";
        var send = await bridge.SendGroupMessageAsync(
            accountId, groupId, text, mentions, idempotencyKey: idempotencyKey);
        if (!send.Sent)
            throw new InvalidOperationException("Zalo bridge did not confirm draft-autopilot send.");

        var providerId = NormalizeProviderMessageId(send.MessageId);
        var persistedId = providerId ?? $"local:{idempotencyKey}";
        try
        {
            await EnsureV2OutboundMessageAsync(
                connectionId, groupId, persistedId, accountId, botName, text, cancellationToken);
            if (providerId is not null)
            {
                await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                    connectionId, groupId, providerId, incoming.MessageId, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Draft autopilot send persisted incompletely Group={GroupId} Message={MessageId}",
                groupId,
                incoming.MessageId);
        }

        stored.BotReplySentAt = DateTimeOffset.UtcNow;
        stored.SelectedIntent = DraftAutopilotIntent;
        stored.AiCalled = false;
        stored.ReplyOutcome = "sent";
        stored.ProcessingToken = null;
        await db.SaveChangesAsync(cancellationToken);
        await WriteDraftTraceAsync(
            incoming, groupId, null, reason, cancellationToken, persistedId);
        return providerId;
    }

    private async Task<string?> SendDraftProactiveAsync(
        MatchSession session,
        string text,
        IReadOnlyList<BridgeOutgoingMention> mentions,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (session.ZaloConnection is null ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId) ||
            string.IsNullOrWhiteSpace(session.ZaloConnectionId))
            return null;

        var send = await bridge.SendGroupMessageAsync(
            session.ZaloConnection.AccountZaloId,
            session.ZaloGroupId,
            text,
            mentions,
            idempotencyKey: idempotencyKey);
        if (!send.Sent)
            throw new InvalidOperationException("Zalo bridge did not confirm proactive draft send.");

        var providerId = NormalizeProviderMessageId(send.MessageId);
        var persistedId = providerId ?? $"local:{idempotencyKey}";
        try
        {
            await EnsureV2OutboundMessageAsync(
                session.ZaloConnectionId,
                session.ZaloGroupId,
                persistedId,
                session.ZaloConnection.AccountZaloId,
                session.ZaloConnection.DisplayName,
                text,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not persist proactive draft message Session={SessionId}",
                session.Id);
        }
        return providerId;
    }

    private async Task<bool> SeedDraftConfirmationAsync(
        string connectionId,
        string groupId,
        string approverId,
        string sessionId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken,
        bool refuseToOverwriteDifferentPending = false)
    {
        _ = refuseToOverwriteDifferentPending; // kept for call-site readability/backward compatibility.
        approverId = ZaloOverbookLogic.NormalizeId(approverId);
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new[] { sessionId });
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == approverId,
            cancellationToken);

        // For draft autopilot the safe behavior is stricter than legacy callers: any
        // live pending action may only be reused when it is AutoDraftConfirm for this
        // exact session. Never overwrite T4 with T6, or an unrelated confirmation.
        if (!ZaloDraftApprovalSafety.CanReservePending(state, sessionId, now))
            return false;

        if (state is null)
        {
            var created = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = approverId,
                PendingIntent = ZaloBotIntent.AutoDraftConfirm.ToString(),
                PendingPayloadJson = payload,
                PreviousCommand = ZaloBotIntent.AutoDraft.ToString(),
                ExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.ZaloBotConversationStates.Add(created);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // The composite unique key means another webhook/process reserved this
                // approver first. Do not retry as an overwrite; let the caller choose
                // another approver or wait for the existing pending action to finish.
                db.Entry(created).State = EntityState.Detached;
                return false;
            }
        }

        // Existing rows use an optimistic compare-and-swap on the exact version we
        // inspected. This closes the cross-session race even across API instances: if
        // another process renews/replaces the pending row first, this update affects 0.
        var previousIntent = state.PendingIntent;
        var previousPayload = state.PendingPayloadJson;
        var previousUpdatedAt = state.UpdatedAt;
        var updated = await db.ZaloBotConversationStates
            .Where(item => item.Id == state.Id &&
                           item.PendingIntent == previousIntent &&
                           item.PendingPayloadJson == previousPayload &&
                           item.UpdatedAt == previousUpdatedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.PendingIntent, ZaloBotIntent.AutoDraftConfirm.ToString())
                .SetProperty(item => item.PendingPayloadJson, payload)
                .SetProperty(item => item.PreviousCommand, ZaloBotIntent.AutoDraft.ToString())
                .SetProperty(item => item.ExpiresAt, expiresAt)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return updated == 1;
    }

    private async Task RemoveDraftPendingAsync(
        string connectionId,
        string groupId,
        string approverId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var rows = await db.ZaloBotConversationStates
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           item.SenderZaloUserId == ZaloOverbookLogic.NormalizeId(approverId) &&
                           item.PendingIntent == ZaloBotIntent.AutoDraftConfirm.ToString())
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (ZaloDraftApprovalSafety.PendingTargetsSession(
                    row.PendingIntent, row.PendingPayloadJson, sessionId))
                db.ZaloBotConversationStates.Remove(row);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteDraftTraceAsync(
        ZaloIncomingMessageEvent incoming,
        string groupId,
        string? sessionId,
        string reason,
        CancellationToken cancellationToken,
        string? replyMessageId = null)
    {
        try
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    MessageId: ZaloOverbookLogic.NormalizeId(incoming.MessageId),
                    GroupId: groupId,
                    SenderZaloUserId: ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                    AddressReason: "DraftAutopilot",
                    IntentSource: "DeterministicDraftAutopilot",
                    Intent: DraftAutopilotIntent,
                    Confidence: 1,
                    QuotedMessageId: quote.MessageId,
                    ResolvedSessionId: sessionId,
                    AiCalled: false,
                    ReplyMessageId: replyMessageId,
                    FallbackReason: reason),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not write draft-autopilot trace Reason={Reason} Group={GroupId} Message={MessageId}",
                reason,
                groupId,
                incoming.MessageId);
        }
    }

    private static ZaloIncomingMessageEvent PromoteToBot(
        ZaloIncomingMessageEvent incoming,
        string content)
    {
        var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        return incoming with
        {
            Content = content,
            MentionedBot = true,
            Mentions = botId.Length == 0
                ? incoming.Mentions
                : [new ZaloBridgeMention(botId, 0, 0)]
        };
    }

    private static bool IsTargetedDraftTurn(
        ZaloDraftEscalationSnapshot request,
        string senderId,
        ZaloQuotedSemanticContext quote,
        DraftAutopilotSettings settings)
    {
        var isPrimary = string.Equals(request.PrimaryApproverId, senderId, StringComparison.Ordinal);
        var isSecondary = string.Equals(request.SecondaryApproverId, senderId, StringComparison.Ordinal);
        if (!isPrimary && !isSecondary) return false;

        var expectedMessageId = isPrimary
            ? request.PrimaryApproverMessageId
            : request.SecondaryApproverMessageId;
        if (!string.IsNullOrWhiteSpace(expectedMessageId) &&
            string.Equals(expectedMessageId, quote.MessageId, StringComparison.Ordinal))
            return true;

        var nudgedAt = isPrimary ? request.PrimaryNudgeAt : request.SecondaryNudgeAt;
        return nudgedAt is not null &&
               DateTimeOffset.UtcNow - nudgedAt <=
               TimeSpan.FromMinutes(settings.TargetedConfirmationMinutes);
    }

    private static string BuildMemberReadyText(
        ZaloDraftReadinessSnapshot readiness,
        bool roleLookupSucceeded)
    {
        var timing = readiness.StartTime is null
            ? string.Empty
            : $", còn khoảng {FormatRemaining(readiness.StartTime.Value - DateTimeOffset.UtcNow)} tới giờ đánh";
        var roleNote = roleLookupSucceeded
            ? "Muốn tui gọi một trưởng/phó nhóm hoạt động gần đây để chốt draft luôn không?"
            : "Tui chưa đọc được quyền trưởng/phó Zalo lúc này; ông vẫn có thể hỏi lại sau.";
        return $"{readiness.SessionName} chưa chia team nha. Hiện {readiness.EffectiveSlotCount}/{readiness.Capacity}, hồ sơ đủ{timing}. {roleNote}";
    }

    private static string BuildLeaderReadyText(ZaloDraftReadinessSnapshot readiness)
    {
        var timing = readiness.StartTime is null
            ? string.Empty
            : $", còn khoảng {FormatRemaining(readiness.StartTime.Value - DateTimeOffset.UtcNow)}";
        return $"{readiness.SessionName} chưa chia team. Hiện {readiness.EffectiveSlotCount}/{readiness.Capacity}, dữ liệu ready{timing}. Ông đang có quyền trưởng/phó; nếu muốn chốt thì chỉ cần nói `draft đi`, không cần @bot.";
    }

    private static string BuildApproverPrompt(
        ZaloDraftReadinessSnapshot readiness,
        string? requesterName)
    {
        var askedBy = string.IsNullOrWhiteSpace(requesterName)
            ? string.Empty
            : $" {requesterName.TrimStart('@')} đang hỏi đội hình.";
        var timing = readiness.StartTime is null
            ? string.Empty
            : $" Còn khoảng {FormatRemaining(readiness.StartTime.Value - DateTimeOffset.UtcNow)} tới giờ đánh.";
        return $"{readiness.SessionName} hiện {readiness.EffectiveSlotCount}/{readiness.Capacity}, hồ sơ đủ và chưa có đội hình.{askedBy}{timing} Nếu ông đồng ý thì reply `draft đi` nha; tui sẽ sync và kiểm tra lại roster trước khi chạy.";
    }

    private static string BuildReadinessBlockerText(
        ZaloDraftReadinessSnapshot readiness) => readiness.State switch
    {
        ZaloDraftReadinessState.RosterNotFull =>
            $"{readiness.SessionName} chưa tới lúc chốt draft: hiện {readiness.EffectiveSlotCount}/{readiness.Capacity} slot, còn thiếu {Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount)}. Tui chưa tag trưởng/phó cho một action chắc chắn chưa ready.",
        ZaloDraftReadinessState.RosterOverCapacity =>
            $"{readiness.SessionName} đang {readiness.EffectiveSlotCount}/{readiness.Capacity} slot, tức đang vượt capacity. Tui chưa gọi người chốt draft cho tới khi roster hợp lệ.",
        ZaloDraftReadinessState.MissingProfiles =>
            $"{readiness.SessionName} đủ slot nhưng còn {readiness.MissingProfileCount} hồ sơ chưa đủ: {string.Join(", ", readiness.MissingProfileNames.Take(8))}. Tui chưa tag người chốt vì draft lúc này sẽ bị chặn.",
        ZaloDraftReadinessState.SessionStarted =>
            $"{readiness.SessionName} đã tới/qua giờ bắt đầu rồi nên autopilot không chủ động chạy draft nữa.",
        ZaloDraftReadinessState.MissingStartTime =>
            $"{readiness.SessionName} đủ dữ liệu roster nhưng chưa chốt giờ trận. Tui chưa chủ động gọi người draft khi chưa có mốc thời gian.",
        ZaloDraftReadinessState.NoRoster =>
            $"{readiness.SessionName} chưa có roster để draft.",
        ZaloDraftReadinessState.AlreadyDrafted =>
            $"{readiness.SessionName} có đội hình rồi; tui không draft lại.",
        _ =>
            $"{readiness.SessionName} chưa ở trạng thái an toàn để autopilot draft. Tui giữ nguyên dữ liệu nha."
    };

    private static string FormatDraftSessionChoice(MatchSession session)
    {
        if (session.StartTime is null) return session.Name;
        var local = session.StartTime.Value.ToOffset(TimeSpan.FromHours(7));
        return $"{session.Name} ({local:dd/MM HH:mm})";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "0 phút";
        if (remaining.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} phút";
        var hours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;
        return minutes == 0 ? $"{hours} giờ" : $"{hours} giờ {minutes} phút";
    }

    private static DateTimeOffset GetRequestExpiry(
        DateTimeOffset? startTime,
        DateTimeOffset now,
        DraftAutopilotSettings settings,
        int requestedMinutes)
    {
        var normal = now.AddMinutes(Math.Max(2, requestedMinutes));
        if (startTime is null) return normal;
        var cutoff = startTime.Value.AddMinutes(-settings.StopNudgingMinutesBeforeStart);
        return cutoff > now.AddMinutes(2) && cutoff < normal ? cutoff : normal;
    }
}
