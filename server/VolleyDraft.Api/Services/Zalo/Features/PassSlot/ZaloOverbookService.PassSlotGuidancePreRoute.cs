using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private static readonly Regex PassSlotGuidanceTopicPattern = new(
        @"(?<![a-z0-9])(?:pass|nhuong|bo)\s+(?:slot|suat|cho|keo)(?![a-z0-9])|(?<![a-z0-9])(?:slot|suat)\s+(?:pass|nhuong)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PassSlotGuidanceHelpPattern = new(
        @"(?<![a-z0-9])(?:go|nhap|noi)\s+(?:gi|sao|the\s+nao)(?![a-z0-9])|(?<![a-z0-9])(?:lam\s+sao|cach\s+(?:nao|lam)|cu\s+phap|huong\s+dan|dung\s+sao|su\s+dung\s+sao)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Beginner-safe deterministic help for pass-slot questions. This lane is
    /// informational only: it never opens an offer, claims a slot, changes poll/roster,
    /// or calls AI. Explicit bot addressing/reply ownership is required so ordinary
    /// group discussion about somebody passing a slot does not wake NPC.
    /// </summary>
    internal async Task<bool> TryHandlePassSlotGuidancePreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!IsPassSlotGuidanceQuestion(incoming)) return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return false;

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

        await SendDeterministicPreRouteResponseAsync(
            connection.Id,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            BuildPassSlotGuidance(),
            "PassSlotGuidance",
            cancellationToken);
        return true;
    }

    internal static bool IsPassSlotGuidanceQuestion(ZaloIncomingMessageEvent incoming)
    {
        if (!IsExplicitlyAddressedToBot(incoming)) return false;

        var question = incoming.MentionedBot
            ? ZaloBotService.ExtractQuestion(incoming)
            : incoming.Content ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(question).Trim();
        if (normalized.Length == 0) return false;

        return PassSlotGuidanceTopicPattern.IsMatch(normalized) &&
               PassSlotGuidanceHelpPattern.IsMatch(normalized);
    }

    internal static string BuildPassSlotGuidance() =>
        "Pass slot = nhường hẳn suất của mình cho người khác; share slot = nhiều người thay phiên chung 1 slot, hai cái khác nhau nha.\n" +
        "\nCách pass dễ nhất, không cần AI:\n" +
        "1. Người đang có tên trong roster/poll tự gõ: `pass slot T6` (hoặc `nhường suất CN`). NPC sẽ kiểm tra đúng người + đúng kèo từ backend rồi mở slot.\n" +
        "2. Người muốn lấy gõ: `tui nhận T6` (nếu chỉ có một slot rõ ràng thì `tui nhận` cũng được).\n" +
        "3. Trước draft: owner bỏ vote, người nhận vote vào đúng kèo rồi gõ `xong`; NPC chỉ xác nhận khi roster đã đổi thật.\n" +
        "4. Sau draft: người đã giữ slot gõ `chốt`; NPC re-check trạng thái rồi mới chuyển.\n" +
        "5. Owner đổi ý khi chưa hoàn tất: gõ `huỷ pass`. Người đang giữ claim muốn nhả: gõ `huỷ nhận`.\n" +
        "\nAdmin/operator nếu cần chuyển thay cho hai người có thể dùng cú pháp deterministic `@Npc @A pass slot cho @B`; nếu có nhiều kèo NPC sẽ hỏi lại trận thay vì đoán. Quyền và UID vẫn được kiểm tra server-side.\n" +
        "\nNếu ý bạn là chơi thay phiên chung một slot thì dùng ShareSlot, ví dụ: `@Npc tui muốn share slot với @To An hôm nay`.";

    private static bool IsExplicitlyAddressedToBot(ZaloIncomingMessageEvent incoming)
    {
        if (incoming.MentionedBot)
        {
            var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
            return botId.Length > 0 && incoming.Mentions.Any(mention =>
                string.Equals(
                    ZaloOverbookLogic.NormalizeId(mention.Uid),
                    botId,
                    StringComparison.Ordinal));
        }

        var quotedSenderId = ZaloOverbookLogic.NormalizeId(incoming.Quote?.SenderId);
        var currentBotId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        return currentBotId.Length > 0 &&
               string.Equals(currentBotId, quotedSenderId, StringComparison.Ordinal);
    }
}
