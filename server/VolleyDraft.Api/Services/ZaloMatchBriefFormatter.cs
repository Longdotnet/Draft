using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Adds one compact lifecycle footer to an existing leader reminder. The domain
/// reminder keeps its detailed decision wording; this formatter answers the product
/// question the organizer actually cares about: do I need the website right now?
/// </summary>
internal static class ZaloMatchBriefFormatter
{
    internal static string Append(string message, MatchLifecycleResponse lifecycle)
    {
        var state = $"📌 {lifecycle.SessionName}: {lifecycle.EffectiveSlotCount}/{lifecycle.Capacity} slot · {lifecycle.StageLabel}.";
        if (lifecycle.ActiveSlotRiskCount > 0)
            state += $" Pass đang mở: {lifecycle.ActiveSlotRiskCount}.";
        if (lifecycle.MissingProfileCount > 0)
            state += $" Hồ sơ thiếu: {lifecycle.MissingProfileCount}.";

        var guidance = lifecycle.NeedsWebsite
            ? $"⚠️ CẦN WEBSITE — {DescribeWebTarget(lifecycle.WebTarget)}. Bot dừng trước phần không đủ chắc để tự quyết."
            : lifecycle.Owner is MatchLifecycleOwner.ZaloBot or MatchLifecycleOwner.System
                ? "✅ CHƯA CẦN MỞ WEBSITE — bot đang xử lý tiếp; ông chưa cần làm gì trên web."
                : lifecycle.Owner == MatchLifecycleOwner.Leader
                    ? "✅ KHÔNG CẦN WEBSITE — nếu cần chốt gì thì trả lời ngay trong Zalo."
                    : "✅ KHÔNG CẦN WEBSITE.";

        return $"{message.Trim()}\n\n{state}\n{guidance}";
    }

    private static string DescribeWebTarget(string? webTarget) => webTarget switch
    {
        "bot-overbook-control" => "vào đúng khu vực Overbook để xác nhận exception",
        "auto-session-control" => "vào Auto Session/Zalo để sửa liên kết group",
        "draft-workspace" => "vào đúng session/draft workspace để kiểm tra exception",
        _ => "mở đúng session được bot báo để xử lý exception"
    };
}