using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

internal enum ZaloRecruitmentGuestMentionGateDecision
{
    NotApplicable,
    QueueReplyGatedMutation,
    RequireRecruitmentReply
}

internal static class ZaloRecruitmentGuestMentionGatePolicy
{
    internal static ZaloRecruitmentGuestMentionGateDecision Decide(
        bool mentionedBot,
        bool looksLikeAddRequest,
        ZaloRecruitmentGuestCommandKind? parsedKind,
        ZaloRecruitmentGuestReplyAnchorKind anchorKind)
    {
        if (!mentionedBot || !looksLikeAddRequest)
            return ZaloRecruitmentGuestMentionGateDecision.NotApplicable;

        return parsedKind == ZaloRecruitmentGuestCommandKind.Add &&
               anchorKind == ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast
            ? ZaloRecruitmentGuestMentionGateDecision.QueueReplyGatedMutation
            : ZaloRecruitmentGuestMentionGateDecision.RequireRecruitmentReply;
    }
}

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Guest signup has exactly one mutation lane: a +1/+2 reply to a grounded
    /// KeepRecruiting broadcast. Explicit @Npc guest requests are intercepted here
    /// before the legacy bot/AI router can claim that it changed the roster.
    /// </summary>
    private async Task<bool> TryHandleRecruitmentGuestSingleLaneGateAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        if (!incoming.MentionedBot || !ZaloRecruitmentGuestPolicy.LooksLikeAddRequest(incoming.Content))
            return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
        if (accountId.Length == 0 || groupId.Length == 0 || messageId.Length == 0)
            return false;

        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new
            {
                item.Id,
                item.AccountZaloId,
                item.DisplayName,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var connection = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (connection is null) return false;

        var command = ZaloRecruitmentGuestPolicy.TryParse(incoming.Content);
        var parsedKind = command?.Kind;
        var anchorKind = ZaloRecruitmentGuestReplyAnchorKind.None;

        if (parsedKind == ZaloRecruitmentGuestCommandKind.Add)
        {
            var resolution = await ResolveReplyGatedRecruitmentGuestSessionAsync(
                connection.Id,
                groupId,
                messageId,
                ZaloRecruitmentGuestCommandKind.Add,
                cancellationToken);
            anchorKind = resolution.AnchorKind;
        }

        var decision = ZaloRecruitmentGuestMentionGatePolicy.Decide(
            incoming.MentionedBot,
            looksLikeAddRequest: true,
            parsedKind,
            anchorKind);

        if (decision == ZaloRecruitmentGuestMentionGateDecision.QueueReplyGatedMutation)
        {
            // V2 pre-routing already captured ReplyTo topology. Persist the incoming
            // turn, then explicitly release the generic pre-route processing lease so
            // ProcessReplyGatedRecruitmentGuestTurnsDueAsync can own the only mutation.
            // Returning true here prevents legacy AddGuestPlayer/GeneralChat racing it.
            var stored = await EnsureV2IncomingMessageAsync(
                connection.Id,
                groupId,
                incoming,
                cancellationToken);
            if (stored.BotReplySentAt is null)
            {
                stored.ReplyOutcome = null;
                stored.ProcessingStartedAt = null;
                stored.ProcessingToken = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            logger.LogInformation(
                "Queued grounded recruitment guest reply for single mutation lane Group={GroupId} Message={MessageId} Sender={SenderId}",
                groupId,
                messageId,
                incoming.SenderId);
            return true;
        }

        if (decision != ZaloRecruitmentGuestMentionGateDecision.RequireRecruitmentReply)
            return false;

        await SendDeterministicPreRouteResponseAsync(
            connection.Id,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            "Tui chưa cộng slot từ câu này nha. + bạn ngoài group chỉ chạy khi ông reply đúng tin `@all` tuyển người của tui và gửi `+1` hoặc `+2`. Khi mutation DB thành công tui mới báo roster tăng; nói trực tiếp với @Npc sẽ không tự cộng người.",
            "RecruitmentGuestReplyRequired",
            cancellationToken);
        return true;
    }
}
