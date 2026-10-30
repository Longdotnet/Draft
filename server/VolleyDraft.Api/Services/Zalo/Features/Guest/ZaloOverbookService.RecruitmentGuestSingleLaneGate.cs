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
               ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
                   ZaloRecruitmentGuestCommandKind.Add,
                   anchorKind)
            ? ZaloRecruitmentGuestMentionGateDecision.QueueReplyGatedMutation
            : ZaloRecruitmentGuestMentionGateDecision.RequireRecruitmentReply;
    }
}

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Guest signup has exactly one grounded mutation lane: a +1/+2 reply to either
    /// a KeepRecruiting broadcast or a bot guidance message bound to one exact session.
    /// Explicit @Npc guest requests are intercepted here before the legacy bot/AI router
    /// can claim that it changed the roster.
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

        MatchSession? guidanceSession = null;
        if (command is { Kind: ZaloRecruitmentGuestCommandKind.Add })
        {
            var guidanceResolution = await ResolveRecruitmentGuestSessionAsync(
                connection.Id,
                groupId,
                messageId,
                incoming.Content,
                incoming.SenderId,
                command,
                cancellationToken);
            guidanceSession = guidanceResolution.Session;
        }

        var guidance = command?.SponsorReference is { Length: > 0 } sponsorReference
            ? guidanceSession is not null
                ? $"Tui hiểu `+{command.Quantity} bạn @{sponsorReference}` là thêm {command.Quantity} bạn ngoài group do {sponsorReference} bảo lãnh, không phải thêm {sponsorReference} vào roster. Kèo đã xác định là {guidanceSession.Name}; reply ngay tin này `+{command.Quantity} bạn @{sponsorReference}` là tui cộng slot."
                : $"Tui hiểu `+{command.Quantity} bạn @{sponsorReference}` là thêm {command.Quantity} bạn ngoài group do {sponsorReference} bảo lãnh, không phải thêm {sponsorReference} vào roster. Tui chưa xác định chắc kèo nào; gửi lại kèm `hôm nay`, thứ hoặc ngày cụ thể nha."
            : guidanceSession is not null
                ? $"Tui hiểu ông muốn +{command!.Quantity} bạn ngoài group cho {guidanceSession.Name}. Reply ngay tin này `+{command.Quantity}` là tui cộng slot."
                : "Tui chưa cộng slot từ câu này nha. Gửi lại kèm `hôm nay`, thứ hoặc ngày cụ thể để tui khóa đúng kèo trước khi + bạn ngoài group.";

        await SendDeterministicPreRouteResponseAsync(
            connection.Id,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            guidance,
            "RecruitmentGuestReplyRequired",
            cancellationToken,
            guidanceSession is null
                ? null
                : ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(guidanceSession.Id),
            guidanceSession is null
                ? null
                : ZaloRecruitmentGuestGatePolicy.GuestSignupGuidanceReplyOutcome);
        return true;
    }
}
