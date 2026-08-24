using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string MatchBriefSessionChoiceIntent = "MatchBriefSessionChoice";

    private async Task<bool> HandleMatchBriefQuestionAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloConversationStateV2Snapshot? selectionState,
        DraftAutopilotSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.ZaloConnection)
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
            var stateStore = new ZaloConversationStateV2Store(db);
            var currentState = await stateStore.LoadActiveAsync(groupId, incoming.SenderId, cancellationToken);
            if (currentState is not null &&
                !string.Equals(currentState.Intent, MatchBriefSessionChoiceIntent, StringComparison.Ordinal))
            {
                await SendDraftReplyAsync(
                    connectionId,
                    accountId,
                    botName,
                    groupId,
                    incoming,
                    "Ông đang có một việc bot khác chờ trả lời nên tui không mở thêm lựa chọn T4/T6 rồi làm rối pending đó nha. Xử lý hoặc huỷ việc đang chờ trước, rồi hỏi tình hình lại; tui không đụng dữ liệu hiện tại.",
                    [],
                    "match_brief_pending_conflict",
                    cancellationToken);
                return true;
            }

            await stateStore.SaveActiveAsync(
                groupId,
                ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                MatchBriefSessionChoiceIntent,
                "{}",
                "[]",
                JsonSerializer.Serialize(candidates.Take(8).Select(item => item.Id).ToList()),
                incoming.MessageId,
                incoming.MessageId,
                now.AddMinutes(settings.RequesterConsentMinutes),
                cancellationToken);

            var choices = string.Join(", ", candidates.Take(4).Select(FormatDraftSessionChoice));
            await SendDraftReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                $"Ông muốn xem tình hình kèo nào: {choices}? Trả lời T4/T6, ngày hoặc tên kèo là được, không cần @bot.",
                [],
                "match_brief_session_ambiguous",
                cancellationToken);
            return true;
        }

        if (selectionState is not null)
        {
            await new ZaloConversationStateV2Store(db)
                .CompleteAsync(groupId, ZaloOverbookLogic.NormalizeId(incoming.SenderId), cancellationToken);
        }

        // Match Brief promises current state. If the session is poll-backed, refresh
        // before answering instead of presenting a stale database snapshot as "now".
        var readinessBeforeRefresh = await new ZaloDraftReadinessService(db)
            .BuildAsync(selected.Id, now, cancellationToken);
        if (readinessBeforeRefresh?.HasLinkedPoll == true &&
            (selected.Status is SessionStatus.Setup or SessionStatus.CaptainSelection))
        {
            var synced = await integration.SyncLatestPollAsync(selected.AdminUserId, selected.Id);
            if (!synced.IsSuccess)
            {
                await SendDraftReplyAsync(
                    connectionId,
                    accountId,
                    botName,
                    groupId,
                    incoming,
                    $"Tui chưa refresh được poll của {selected.Name}, nên không lấy snapshot cũ trả như dữ liệu hiện tại nha. Chưa kết luận cần vào web hay không cho tới khi poll sync lại được.",
                    [],
                    "match_brief_poll_refresh_failed",
                    cancellationToken);
                return true;
            }
        }

        var lifecycleResult = await new MatchLifecycleCoordinator(db)
            .GetAsync(selected.AdminUserId, selected.Id, cancellationToken);
        if (!lifecycleResult.IsSuccess || lifecycleResult.Value is null)
        {
            await SendDraftReplyAsync(
                connectionId,
                accountId,
                botName,
                groupId,
                incoming,
                $"Tui chưa dựng được trạng thái an toàn cho {selected.Name}, nên chưa dám đoán. Dữ liệu chưa bị thay đổi.",
                [],
                "match_brief_lifecycle_unavailable",
                cancellationToken);
            return true;
        }

        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var configuredOperators = ParseStringList(selected.BotOperatorZaloUserIdsJson);
        var canOperate = configuredOperators.Contains(senderId, StringComparer.Ordinal);
        if (!canOperate)
        {
            var role = await integration.GetGroupRoleAuthorizationAsync(
                selected.AdminUserId,
                selected.Id,
                senderId);
            canOperate = role.IsSuccess && role.Value?.CanOperateBot == true;
        }

        var lifecycle = lifecycleResult.Value;
        var deepLink = canOperate
            ? new ZaloAdminDeepLinkBuilder(configuration).Build(lifecycle)
            : null;
        var response = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate, deepLink);
        await SendDraftReplyAsync(
            connectionId,
            accountId,
            botName,
            groupId,
            incoming,
            response,
            [],
            lifecycle.NeedsWebsite ? "match_brief_web_exception" : "match_brief_no_web_needed",
            cancellationToken);
        return true;
    }
}
