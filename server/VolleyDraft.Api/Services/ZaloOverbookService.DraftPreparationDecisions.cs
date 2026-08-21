using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDraftPreparationDecisionCommand(
    ZaloDraftPreparationDecisionKind Kind,
    int? RequestedSlotCount = null);

internal static partial class ZaloDraftPreparationDecisionPolicy
{
    private static readonly Regex StopMatch = new(
        @"(?<![a-z0-9])(?:(?:huy|cancel|nghi)\s*(?:keo|san|tran)|(?:keo|san|tran)\s*(?:huy|nghi))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeepRecruiting = new(
        @"(?<![a-z0-9])(?:kiem\s*them|tim\s*them|keu\s*them|reo\s*them|goi\s*them|cho\s*them|doi\s*them|kiem\s*cho\s*du|cho\s*du\s*(?:18|nguoi|slot)|cu\s*kiem\s*them)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlayCurrent = new(
        @"(?<![a-z0-9])(?:(?:chot\s*(?<count1>\d{1,2}))|(?<count2>\d{1,2})\s*(?:van\s*)?(?:danh|choi)(?:\s*(?:nha|di|luon|cung\s*duoc))?|(?:van|cu)\s*(?:danh|choi)(?:\s*(?<count3>\d{1,2}))?|(?:danh|choi)\s*(?<count4>\d{1,2})\s*(?:nguoi|slot)?\s*(?:cung\s*)?(?:duoc|ok))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ZaloDraftPreparationDecisionCommand? TryParse(string? content)
    {
        var normalized = ZaloDraftConversationPolicy.Normalize(content);
        if (normalized.Length == 0) return null;

        // "huy slot" is deliberately NOT a match-level stop. Slot transfer owns it.
        if (!normalized.Contains("huy slot", StringComparison.Ordinal) && StopMatch.IsMatch(normalized))
            return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.StopMatch);
        if (KeepRecruiting.IsMatch(normalized))
            return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.KeepRecruiting);

        var play = PlayCurrent.Match(normalized);
        if (!play.Success) return null;
        foreach (var groupName in new[] { "count1", "count2", "count3", "count4" })
        {
            var group = play.Groups[groupName];
            if (group.Success && int.TryParse(group.Value, out var count))
                return new ZaloDraftPreparationDecisionCommand(
                    ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
                    count);
        }
        return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.PlayCurrentRoster);
    }

    internal static bool CanAutoDraftEvenly(int effectiveSlotCount, int teamCount) =>
        teamCount > 0 &&
        effectiveSlotCount >= teamCount * 2 &&
        effectiveSlotCount % teamCount == 0;
}

public sealed partial class ZaloOverbookService
{
    private async Task<bool> TryHandleDraftPreparationDecisionAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        if (ambientSettings.ShadowMode) return false;
        var command = ZaloDraftPreparationDecisionPolicy.TryParse(incoming.Content);
        if (command is null) return false;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.ZaloConnection != null &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                (item.StartTime == null ||
                 (item.StartTime >= now.AddHours(-4) && item.StartTime <= now.AddHours(36))))
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return false;

        var normalized = ZaloDraftConversationPolicy.Normalize(incoming.Content);
        var references = sessions
            .Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime))
            .ToList();
        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(normalized, references);
        var matched = sessions.Where(item => matchedIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
        var session = matched.Count == 1
            ? matched[0]
            : matched.Count == 0 && sessions.Count == 1
                ? sessions[0]
                : null;
        if (session is null) return false;

        var role = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            senderId);
        if (!role.IsSuccess || role.Value?.CanOperateBot != true)
        {
            // Leader decisions are authority-bearing state, not crowd sentiment.
            return false;
        }

        var connection = session.ZaloConnection!;
        var actorName = string.IsNullOrWhiteSpace(incoming.SenderName)
            ? senderId
            : incoming.SenderName.Trim();
        var decisionStore = new ZaloDraftPreparationDecisionStore(db);

        if (command.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
        {
            await decisionStore.SetAsync(
                session.Id,
                command.Kind,
                null,
                null,
                senderId,
                actorName,
                incoming.MessageId,
                cancellationToken);
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Ok, tui ghi nhận trưởng/phó chốt dừng kèo {session.Name}. Tui dừng draft reminder cho trận này nha. Tui chưa tự xoá session, poll hay thao tác huỷ sân bên ngoài.",
                [],
                "draft_preparation_stop_match",
                cancellationToken);
            return true;
        }

        var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
        if (!sync.Success)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Tui nghe quyết định rồi nhưng chưa sync được đúng poll của {session.Name} nên chưa dám khóa trạng thái nha 😭 Thử lại sau khi poll đọc được giúp tui.",
                [],
                "draft_preparation_decision_poll_sync_failed",
                cancellationToken);
            return true;
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, now, cancellationToken);
        if (readiness is null) return false;
        var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);

        if (command.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
        {
            if (readiness.EffectiveSlotCount >= readiness.Capacity && activeSlotRisks == 0)
            {
                await decisionStore.ClearAsync(session.Id, cancellationToken);
                await SendDraftReplyAsync(
                    connectionId,
                    connection.AccountZaloId,
                    connection.DisplayName,
                    groupId,
                    incoming,
                    $"Tui vừa sync {session.Name}: đang {readiness.EffectiveSlotCount}/{readiness.Capacity} rồi nha 😆 Hết thiếu người rồi, giờ chỉ còn chốt roster/draft thôi.",
                    [],
                    "draft_preparation_recruitment_already_full",
                    cancellationToken);
                return true;
            }

            await decisionStore.SetAsync(
                session.Id,
                command.Kind,
                null,
                null,
                senderId,
                actorName,
                incoming.MessageId,
                cancellationToken);
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Ok, kèo {session.Name} tiếp tục kiếm thêm nha 👌 Tui vừa sync đang {readiness.EffectiveSlotCount}/{readiness.Capacity}; mấy lượt sau tui canh delta thôi, không tự suy ra huỷ kèo từ số người thiếu.",
                [],
                "draft_preparation_keep_recruiting",
                cancellationToken);
            return true;
        }

        if (activeSlotRisks > 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Tui vừa sync {session.Name}: {readiness.EffectiveSlotCount}/{readiness.Capacity} nhưng đang có {activeSlotRisks} slot báo pass/huỷ chưa sạch. Chốt người thay/poll trước nha, rồi nói lại `vẫn đánh` hoặc `chốt {readiness.EffectiveSlotCount}` giúp tui.",
                [],
                "draft_preparation_play_current_slot_risk",
                cancellationToken);
            return true;
        }

        if (readiness.EffectiveSlotCount > readiness.Capacity)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"{session.Name} đang {readiness.EffectiveSlotCount}/{readiness.Capacity}, còn vượt slot nên tui chưa khóa quyết định chơi roster này nha. Xử lý over-slot trước giúp tui.",
                [],
                "draft_preparation_play_current_over_capacity",
                cancellationToken);
            return true;
        }

        if (command.RequestedSlotCount is { } requested && requested != readiness.EffectiveSlotCount)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Tui vừa sync lại poll: hiện là {readiness.EffectiveSlotCount}/{readiness.Capacity}, không phải {requested} nha 😆 Nếu vẫn chốt roster hiện tại thì nói `chốt {readiness.EffectiveSlotCount}` giúp tui để khỏi khóa nhầm snapshot.",
                [],
                "draft_preparation_play_current_count_mismatch",
                cancellationToken);
            return true;
        }

        if (readiness.EffectiveSlotCount <= 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                $"Poll {session.Name} đang 0/{readiness.Capacity} nên chưa có roster để chốt chơi nha.",
                [],
                "draft_preparation_play_current_empty",
                cancellationToken);
            return true;
        }

        await decisionStore.SetAsync(
            session.Id,
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            readiness.Fingerprint,
            readiness.EffectiveSlotCount,
            senderId,
            actorName,
            incoming.MessageId,
            cancellationToken);

        var rawVsEffective = readiness.PresentPlayerCount == readiness.EffectiveSlotCount
            ? $"{readiness.EffectiveSlotCount} slot"
            : $"{readiness.PresentPlayerCount} người / {readiness.EffectiveSlotCount} effective slot";
        var evenlyDraftable = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(
            readiness.EffectiveSlotCount,
            session.TeamCount);
        var reply = evenlyDraftable
            ? $"Ok chốt kèo hiện tại: {rawVsEffective} → {session.TeamCount} team x{readiness.EffectiveSlotCount / session.TeamCount} 👌 Khi muốn chia nói `draft đi`; tui sẽ sync poll + check fingerprint lần cuối trước khi chạy."
            : $"Ok, tui ghi nhận kèo vẫn chơi với {rawVsEffective} 👌 Nhưng {readiness.EffectiveSlotCount} effective slot chưa chia đều được {session.TeamCount} team. Nếu muốn bot auto-draft thì cần chỉnh shared/rotation hoặc roster về số chia hết cho {session.TeamCount}; tui không tự bẻ roster.";
        await SendDraftReplyAsync(
            connectionId,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            reply,
            [],
            evenlyDraftable
                ? "draft_preparation_play_current_locked"
                : "draft_preparation_play_current_not_even",
            cancellationToken);
        return true;
    }
}
