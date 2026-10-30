using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    private static readonly TimeSpan GroupLeaveConfirmationWindow = TimeSpan.FromMinutes(2);

    private async Task<BotAnswer> BuildGroupLeaveCommandAnswerAsync(
        ZaloConnection connection,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloGroupLeaveAction action,
        CancellationToken cancellationToken)
    {
        var senderId = NormalizeId(incoming.SenderId);
        if (action == ZaloGroupLeaveAction.Request)
        {
            if (!await IsGroupLeaveOperatorAsync(connection, groupId, senderId, cancellationToken))
                return new BotAnswer("Lệnh out nhóm chỉ dành cho trưởng hoặc phó nhóm Zalo. Tui chưa rời đâu.", null);

            return new BotAnswer(
                "Tui rời nhóm này là không tự quay lại được đâu nha. Nếu chắc chắn, trong 2 phút hãy nhắn @Bott xác nhận out nhóm (hoặc bot ơi xác nhận out nhóm). Muốn thôi thì nhắn @Bott hủy out nhóm.",
                null,
                ZaloBotIntent.GroupLeaveRequest);
        }

        // Only a successfully answered request, from this same member in this exact
        // group/connection, can authorize a subsequent confirmation.
        var since = DateTimeOffset.UtcNow - GroupLeaveConfirmationWindow;
        var pending = await db.ZaloGroupMessages.AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connection.Id &&
                message.GroupId == groupId &&
                message.SenderId == senderId &&
                message.MessageId != NormalizeId(incoming.MessageId) &&
                message.SelectedIntent == ZaloBotIntent.GroupLeaveRequest.ToString() &&
                message.ReplyOutcome == "sent" &&
                message.BotReplySentAt >= since &&
                !message.IsFromBot)
            .OrderByDescending(message => message.BotReplySentAt)
            .Select(message => message.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (pending is null)
            return new BotAnswer("Chưa có yêu cầu out nhóm nào đang chờ của bạn. Gõ @Bott out nhóm đi để bắt đầu.", null);

        if (action == ZaloGroupLeaveAction.Confirm &&
            !await IsGroupLeaveOperatorAsync(connection, groupId, senderId, cancellationToken))
            return new BotAnswer("Bạn không còn quyền trưởng/phó nhóm nên tui không thực hiện lệnh rời nhóm.", null);

        // Atomic one-shot consume prevents two parallel confirmations from leaving twice.
        var consumed = await db.ZaloGroupMessages
            .Where(message => message.Id == pending &&
                              message.ReplyOutcome == "sent" &&
                              message.BotReplySentAt >= since)
            .ExecuteUpdateAsync(update => update
                .SetProperty(message => message.ReplyOutcome, "group_leave_consumed"), cancellationToken);

        if (consumed != 1)
            return new BotAnswer("Lệnh out nhóm này đã được xử lý rồi. Muốn thử lại hãy gửi yêu cầu mới.", null);

        if (action == ZaloGroupLeaveAction.Cancel)
            return new BotAnswer("Đã hủy lệnh out nhóm. Tui vẫn ở đây nha.", null);

        return new BotAnswer("Oke, tui chào cả nhà nha. Tui out nhóm đây 👋", null, ZaloBotIntent.GroupLeaveConfirm);
    }

    private async Task<bool> IsGroupLeaveOperatorAsync(
        ZaloConnection connection,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var protector = new ZaloCredentialProtector(configuration);
            using var credentials = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
            var roles = await bridge.GetGroupRolesAsync(credentials.RootElement.Clone(), groupId);
            return NormalizeId(roles.CreatorId) == senderId ||
                   roles.AdminIds.Any(id => NormalizeId(id) == senderId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed if provider role information cannot be verified.
            logger.LogWarning(exception,
                "Zalo group leave operator check unavailable Group={GroupId} Sender={SenderId}",
                groupId, senderId);
            return false;
        }
    }

    private async Task ExecuteGroupLeaveAfterGoodbyeAsync(
        ZaloConnection connection,
        string groupId,
        ZaloIncomingMessageEvent incoming)
    {
        try
        {
            var result = await bridge.LeaveGroupAsync(connection.AccountZaloId, groupId, CancellationToken.None);
            if (!result.Left)
                throw new InvalidOperationException("Zalo bridge did not confirm group leave");
            if (!result.Mock)
            {
                var disabled = await db.MatchSessions
                    .Where(session => session.ZaloConnectionId == connection.Id &&
                                      session.ZaloGroupId == groupId &&
                                      session.BotEnabled)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(session => session.BotEnabled, false), CancellationToken.None);
                logger.LogInformation(
                    "Zalo bot left group Account={AccountId} Group={GroupId} Sender={SenderId} DisabledSessions={Count}",
                    connection.AccountZaloId, groupId, incoming.SenderId, disabled);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Zalo bot could not leave group Account={AccountId} Group={GroupId} Message={MessageId}",
                connection.AccountZaloId, groupId, incoming.MessageId);
            // The goodbye already went out. Correct it if the bridge refused the leave.
            try
            {
                await bridge.SendGroupMessageAsync(
                    connection.AccountZaloId,
                    groupId,
                    "Tui chưa out nhóm được vì Zalo báo lỗi. Nhờ trưởng/phó nhóm thử lại sau nha.",
                    [],
                    null,
                    $"{NormalizeId(connection.AccountZaloId)}:{NormalizeId(incoming.MessageId)}:leave-failed");
            }
            catch (Exception notifyException)
            {
                logger.LogWarning(notifyException, "Could not notify failed Zalo group leave Group={GroupId}", groupId);
            }
        }
    }
}
