using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Formats lifecycle state for Zalo without inventing another source of truth.
/// Proactive leader reminders get a compact footer; explicit status questions get a
/// standalone brief with the authoritative headline/next step. Admin links are only
/// surfaced when the caller is allowed to operate the match.
/// </summary>
internal static class ZaloMatchBriefFormatter
{
    internal static string Append(
        string message,
        MatchLifecycleResponse lifecycle,
        string? adminDeepLink = null)
    {
        // Existing proactive reminders already target authorized organizer roles.
        // Keep that send lane unchanged and resolve the deployed frontend URL here.
        var resolvedLink = adminDeepLink ?? ZaloAdminDeepLinkBuilder.BuildFromEnvironment(lifecycle);
        return $"{message.Trim()}\n\n{BuildStateLine(lifecycle)}\n{BuildGuidance(lifecycle, canOperate: true, resolvedLink)}";
    }

    internal static string Standalone(
        MatchLifecycleResponse lifecycle,
        bool canOperate,
        string? adminDeepLink = null)
    {
        var lines = new List<string>
        {
            BuildStateLine(lifecycle),
            lifecycle.Headline.Trim()
        };

        if (!string.IsNullOrWhiteSpace(lifecycle.NextStep))
            lines.Add($"➡️ {lifecycle.NextStep.Trim()}");

        lines.Add(BuildGuidance(lifecycle, canOperate, adminDeepLink));
        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildStateLine(MatchLifecycleResponse lifecycle)
    {
        var state = $"📌 {lifecycle.SessionName}: {lifecycle.EffectiveSlotCount}/{lifecycle.Capacity} slot · {lifecycle.StageLabel}.";
        if (lifecycle.ActiveSlotRiskCount > 0)
            state += $" Pass đang mở: {lifecycle.ActiveSlotRiskCount}.";
        if (lifecycle.MissingProfileCount > 0)
            state += $" Hồ sơ thiếu: {lifecycle.MissingProfileCount}.";
        return state;
    }

    private static string BuildGuidance(
        MatchLifecycleResponse lifecycle,
        bool canOperate,
        string? adminDeepLink)
    {
        if (lifecycle.NeedsWebsite)
        {
            if (!canOperate)
            {
                return "⚠️ Kèo này cần trưởng/phó xử lý một exception trên web. Tui đã dừng trước phần không đủ chắc; ông chưa cần tự vào web.";
            }

            var link = string.IsNullOrWhiteSpace(adminDeepLink)
                ? string.Empty
                : $"\n🔗 Mở đúng exception: {adminDeepLink}";
            return $"⚠️ CẦN WEBSITE — {DescribeWebTarget(lifecycle.WebTarget)}. Bot dừng trước phần không đủ chắc để tự quyết.{link}";
        }

        if (lifecycle.Owner is MatchLifecycleOwner.ZaloBot or MatchLifecycleOwner.System)
            return "✅ CHƯA CẦN MỞ WEBSITE — bot đang xử lý tiếp; chưa có thao tác web nào cần người làm.";

        if (lifecycle.Owner == MatchLifecycleOwner.Leader)
        {
            if (!canOperate)
                return "✅ KHÔNG CẦN WEBSITE — bước tiếp theo thuộc trưởng/phó và làm ngay trong Zalo; ông chưa có quyền chốt nên tui không đưa lệnh admin cho ông.";

            var command = string.IsNullOrWhiteSpace(lifecycle.SuggestedZaloCommand)
                ? string.Empty
                : $" Nếu muốn tiếp tục, có thể nói `{lifecycle.SuggestedZaloCommand}`.";
            return $"✅ KHÔNG CẦN WEBSITE — phần còn lại xử lý ngay trong Zalo.{command}";
        }

        return "✅ KHÔNG CẦN WEBSITE.";
    }

    private static string DescribeWebTarget(string? webTarget) => webTarget switch
    {
        "bot-overbook-control" => "mở đúng khu vực Overbook để xác nhận target dư slot",
        "auto-session-control" => "mở Auto Session/Zalo để sửa liên kết group",
        "draft-workspace" => "mở đúng session/draft workspace để kiểm tra exception",
        _ => "mở đúng session được bot báo để xử lý exception"
    };
}
