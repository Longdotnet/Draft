using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloMemberIntelligenceBotService
{
    private const string PendingJoinPagination = "MemberJoin:Pagination";

    private sealed record MembershipPaginationPayload(
        int Days,
        DateTimeOffset AsOf,
        int Page,
        int PageSize,
        int TotalPages);

    private async Task<ZaloMemberBotAnswer> ExecuteMembershipIntentAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloBotIntent intent,
        string question,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var membership = new ZaloMembershipHistoryService(db);
        var senderId = NormalizeId(incoming.SenderId);

        if (intent == ZaloBotIntent.GetMemberJoinDate)
        {
            var normalized = ZaloBotIntelligence.Normalize(question);
            var asksSelf = SelfReferenceRegex().IsMatch(normalized);
            var mentioned = incoming.Mentions
                .Select(item => NormalizeId(item.Uid))
                .Where(id => id.Length > 0 && id != NormalizeId(incoming.BotId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            string targetId;
            if (mentioned.Count == 1)
                targetId = mentioned[0];
            else if (asksSelf)
                targetId = senderId;
            else
                return new ZaloMemberBotAnswer(
                    "Bạn muốn hỏi ngày vào nhóm của ai? Nếu hỏi chính mình, nói @bot tui vào nhóm ngày nào?; nếu hỏi người khác thì @mention người đó.",
                    intent,
                    aiCalled);

            if (!string.Equals(targetId, senderId, StringComparison.Ordinal))
            {
                var denial = await GetOperatorDenialAsync(
                    connectionId, groupId, senderId, intent, cancellationToken);
                if (denial is not null) return denial;
            }

            var evidence = await membership.GetCurrentJoinAsync(
                connectionId, groupId, targetId, cancellationToken);
            if (evidence is null)
            {
                var current = await db.ZaloGroupMembers.AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.ZaloConnectionId == connectionId &&
                        item.GroupId == groupId &&
                        item.ZaloUserId == targetId &&
                        item.IsCurrentMember,
                        cancellationToken);
                var name = current?.DisplayName ?? (targetId == senderId ? incoming.SenderName : "thành viên này");
                return new ZaloMemberBotAnswer(
                    $"Mình chưa có bằng chứng đủ để xác định {name} vào nhóm ngày nào. Ngày bot đồng bộ/thấy thành viên lần đầu không được dùng thay cho ngày tham gia thật.",
                    intent,
                    aiCalled,
                    [name]);
            }

            var rejoin = evidence.IsRejoin ? " Đây là lần vào lại hiện tại." : string.Empty;
            return new ZaloMemberBotAnswer(
                $"{evidence.DisplayName} vào nhóm ngày {FormatDate(evidence.JoinedAt)} theo sự kiện thành viên của Zalo.{rejoin}",
                intent,
                aiCalled,
                [evidence.DisplayName, FormatDate(evidence.JoinedAt)]);
        }

        var days = ExtractRecentJoinDays(question);
        if (days is null)
            return new ZaloMemberBotAnswer(
                "Bạn muốn xem người mới vào trong bao nhiêu ngày? Ví dụ: @bot những ai vào nhóm trong 45 ngày gần đây?",
                intent,
                aiCalled);
        if (days is < 1 or > 3650)
            return new ZaloMemberBotAnswer(
                "Số ngày cần là số nguyên từ 1 đến 3650; mình không tự sửa một khoảng thời gian không hợp lệ.",
                intent,
                aiCalled);

        var asOf = DateTimeOffset.UtcNow;
        return await BuildRecentJoinPageAsync(
            connectionId, groupId, intent, days.Value, asOf, 1, 10, aiCalled,
            senderId, cancellationToken);
    }

    private async Task<ZaloMemberBotAnswer?> HandleMembershipPendingAsync(
        ZaloBotConversationState state,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken)
    {
        if (ZaloBotIntelligence.IsCancel(question))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return new ZaloMemberBotAnswer(
                "Đã đóng danh sách người mới vào nhóm đang xem.",
                ZaloBotIntent.GeneralChat);
        }

        var fresh = ZaloBotIntelligence.ClassifyDeterministically(question);
        if (fresh.Intent is ZaloBotIntent.ListRecentlyJoinedMembers or ZaloBotIntent.GetMemberJoinDate)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        MembershipPaginationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MembershipPaginationPayload>(state.PendingPayloadJson);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is not null &&
            TryResolvePage(question, payload.Page, payload.TotalPages, out var page))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return await BuildRecentJoinPageAsync(
                connectionId,
                groupId,
                ZaloBotIntent.ListRecentlyJoinedMembers,
                payload.Days,
                payload.AsOf,
                page,
                payload.PageSize,
                false,
                NormalizeId(incoming.SenderId),
                cancellationToken);
        }

        db.ZaloBotConversationStates.Remove(state);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private async Task<ZaloMemberBotAnswer> BuildRecentJoinPageAsync(
        string connectionId,
        string groupId,
        ZaloBotIntent intent,
        int days,
        DateTimeOffset asOf,
        int page,
        int pageSize,
        bool aiCalled,
        string senderId,
        CancellationToken cancellationToken)
    {
        var denial = await GetOperatorDenialAsync(
            connectionId, groupId, senderId, intent, cancellationToken);
        if (denial is not null) return denial;

        var membership = new ZaloMembershipHistoryService(db);
        var result = await membership.QueryRecentCurrentMembersAsync(
            connectionId, groupId, days, asOf, page, pageSize, cancellationToken);
        var totalPages = result.TotalItems == 0
            ? 1
            : (int)Math.Ceiling((double)result.TotalItems / pageSize);
        page = Math.Clamp(page, 1, totalPages);

        string text;
        if (result.Items.Count == 0)
        {
            text = result.TotalItems == 0
                ? $"Trong {days} ngày gần đây, mình chưa có thành viên hiện tại nào có sự kiện vào nhóm được xác minh."
                : $"Trang {page} không còn thành viên để hiển thị.";
        }
        else
        {
            var firstOrdinal = (page - 1) * pageSize + 1;
            var lines = result.Items.Select((item, index) =>
                $"{firstOrdinal + index}. {item.DisplayName} — {FormatDate(item.JoinedAt)}{(item.IsRejoin ? " (vào lại)" : string.Empty)}");
            text =
                $"Mình xác định được {result.TotalItems} thành viên hiện còn trong nhóm đã vào trong {days} ngày gần đây (tính tới {FormatDateTime(result.AsOf)}):\n" +
                string.Join("\n", lines);
            if (page < totalPages)
                text += "\n\nGõ @bot tiếp để xem trang sau.";
        }

        if (result.UnknownCurrentJoinDateCount > 0 || result.HasKnownCoverageGap)
        {
            text +=
                $"\n\nLưu ý dữ liệu: {result.UnknownCurrentJoinDateCount} thành viên hiện tại chưa có ngày tham gia được xác minh" +
                (result.HasKnownCoverageGap ? "; lịch sử membership trước thời điểm listener theo dõi có thể chưa đầy đủ." : ".");
        }

        if (totalPages > 1)
        {
            var existing = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderZaloUserId == senderId,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                existing = new ZaloBotConversationState
                {
                    ZaloConnectionId = connectionId,
                    GroupId = groupId,
                    SenderZaloUserId = senderId,
                    CreatedAt = now
                };
                db.ZaloBotConversationStates.Add(existing);
            }
            existing.PendingIntent = PendingJoinPagination;
            existing.PendingPayloadJson = JsonSerializer.Serialize(
                new MembershipPaginationPayload(days, asOf, page, pageSize, totalPages));
            existing.PreviousCommand = intent.ToString();
            existing.ExpiresAt = now.AddMinutes(15);
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var protectedTerms = result.Items
            .SelectMany(item => new[] { item.DisplayName, FormatDate(item.JoinedAt) })
            .Append(result.TotalItems.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ZaloMemberBotAnswer(text, intent, aiCalled, protectedTerms);
    }

    private static int? ExtractRecentJoinDays(string question)
    {
        var q = ZaloBotIntelligence.Normalize(question);
        var match = Regex.Match(
            q,
            @"(?<!\d)(\d{1,4})\s*ngay(?!\w)",
            RegexOptions.CultureInvariant);
        return match.Success &&
               int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? days
            : null;
    }
}
