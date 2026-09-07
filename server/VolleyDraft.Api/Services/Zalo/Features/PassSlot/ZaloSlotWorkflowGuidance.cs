using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloSlotWorkflowGuidanceResult(
    ZaloBotIntent Intent,
    string Text);

/// <summary>
/// Deterministic contextual help for the two slot concepts that users most often
/// conflate in chat:
/// - pass/nhường = the current owner gives up one participation for somebody else;
/// - share slot = multiple people intentionally share one team slot.
///
/// This helper explains supported syntax only. It never opens an offer, changes the
/// roster, resolves names, or authorizes a delegated mutation. Actual commands still
/// flow through their existing grounded domain handlers.
/// </summary>
internal static class ZaloSlotWorkflowGuidance
{
    private static readonly Regex HelpSignalPattern = new(
        @"(?<![a-z0-9])(?:(?:go|ghi|viet|nhap|noi|lam|xu\s+ly)\s+(?:sao|the\s+nao|nhu\s+nao)|(?:the\s+nao|nhu\s+nao|cu\s+phap|huong\s+dan|lenh\s+gi|dung\s+lenh\s+gi|cach))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PassDomainPattern = new(
        @"(?<![a-z0-9])(?:pass|nhuong|tra|bo)\s+(?:slot|suat)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShareDomainPattern = new(
        @"(?<![a-z0-9])(?:share|chung)\s+(?:mot\s+)?slot(?![a-z0-9])|(?<![a-z0-9])slot\s+(?:share|chung)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ZaloSlotWorkflowGuidanceResult? TryBuild(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        if (normalized.Length == 0 || !HelpSignalPattern.IsMatch(normalized)) return null;

        var asksPass = PassDomainPattern.IsMatch(normalized);
        var asksShare = ShareDomainPattern.IsMatch(normalized);
        if (!asksPass && !asksShare) return null;

        // "share slot" is a distinct VolleyDraft product concept. Prefer explaining
        // that flow when the user explicitly names it so we do not teach pass-slot
        // syntax for a request that really means alternating/shared participation.
        return asksShare && !asksPass
            ? new ZaloSlotWorkflowGuidanceResult(ZaloBotIntent.ShareSlot, BuildShareSlotHelp())
            : new ZaloSlotWorkflowGuidanceResult(ZaloBotIntent.SlotTransfer, BuildPassSlotHelp());
    }

    private static string BuildPassSlotHelp() =>
        "Pass/nhường slot làm như này nha:\n" +
        "1) Người đang có slot tự nói `pass slot T6` (hoặc `nhường suất CN`). NPC sẽ kiểm roster/session rồi mở slot cho nhóm; bước này chưa tự sửa roster.\n" +
        "2) Người muốn lấy nói `tui nhận T6`; nếu chỉ có đúng một slot đang mở thì `tui nhận` cũng được.\n" +
        "3) Trước draft: owner bỏ vote, người nhận vote vào đúng kèo rồi nói `xong`; NPC chỉ chốt khi roster thật đã đổi. Sau draft: người đang giữ claim nói `chốt`; NPC revalidate rồi mới chuyển.\n" +
        "4) Admin/operator chỉ nên làm hộ khi đã biết rõ cả người nhường lẫn người nhận. Với luồng chuyển trực tiếp sau draft có thể dùng `@A pass slot cho @B`; NPC vẫn kiểm quyền + session + trạng thái, tên trùng thì nên mention đúng người. Trước draft không dùng lệnh admin để lách poll.";

    private static string BuildShareSlotHelp() =>
        "Share slot khác pass slot nha: share là 2-3 người dùng chung một slot/luân phiên, không phải nhường hẳn suất cho người khác.\n" +
        "- Tự share: `tui muốn share slot với @To An T6`.\n" +
        "- Admin/operator làm hộ: `@A muốn share slot với @B T6` (có thể thêm người thứ 3 nếu feature cho phép).\n" +
        "NPC sẽ bind mention vào Zalo UID và kiểm roster/session trước khi thay đổi. Nếu mục tiêu là bỏ hẳn suất để người khác nhận thì dùng flow pass: `pass slot T6` → người khác `tui nhận T6`.";
}
