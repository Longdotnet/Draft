using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Formats lifecycle state for Zalo without inventing another source of truth.
/// User-facing copy is action-first: show the current match state and the next useful
/// step. The website remains an implementation detail; an exception link only appears
/// when a human action is genuinely required.
/// </summary>
internal static class ZaloMatchBriefFormatter
{
    internal static string Append(
        string message,
        MatchLifecycleResponse lifecycle,
        string? adminDeepLink = null)
    {
        // Existing proactive reminders already contain their domain-specific action.
        // Add only the authoritative compact state and, for a genuine exception, the
        // one-tap resolution link. Do not narrate whether the website is needed.
        var lines = new List<string>
        {
            message.Trim(),
            string.Empty,
            BuildStateLine(lifecycle)
        };

        if (lifecycle.NeedsWebsite)
        {
            var resolvedLink = adminDeepLink ?? ZaloAdminDeepLinkBuilder.BuildFromEnvironment(lifecycle);
            if (!string.IsNullOrWhiteSpace(resolvedLink))
                lines.Add($"🔗 Xử lý bước đang vướng: {resolvedLink}");
        }

        return string.Join("\n", lines);
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

        var action = BuildAction(lifecycle, canOperate);
        if (!string.IsNullOrWhiteSpace(action))
            lines.Add($"➡️ {action}");

        if (lifecycle.NeedsWebsite && canOperate && !string.IsNullOrWhiteSpace(adminDeepLink))
            lines.Add($"🔗 Xử lý ngay: {adminDeepLink}");

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

    private static string? BuildAction(
        MatchLifecycleResponse lifecycle,
        bool canOperate)
    {
        if (lifecycle.NeedsWebsite)
        {
            if (!canOperate)
                return "Bước này cần trưởng/phó xử lý; tui giữ nguyên dữ liệu để tránh tự quyết sai.";

            return DescribeHumanAction(lifecycle.WebTarget);
        }

        if (lifecycle.Owner is MatchLifecycleOwner.ZaloBot or MatchLifecycleOwner.System)
            return "Bot tiếp tục xử lý theo trạng thái hiện tại; chưa có gì ông cần làm.";

        if (lifecycle.Owner == MatchLifecycleOwner.Leader)
        {
            if (!canOperate)
                return "Chờ trưởng/phó chốt bước tiếp theo; tui không đưa lệnh admin cho người chưa có quyền.";

            return string.IsNullOrWhiteSpace(lifecycle.SuggestedZaloCommand)
                ? "Trưởng/phó có thể chốt bước tiếp theo ngay trong Zalo."
                : $"Có thể nói `{lifecycle.SuggestedZaloCommand}` ngay trong Zalo.";
        }

        return null;
    }

    private static string DescribeHumanAction(string? webTarget) => webTarget switch
    {
        "bot-overbook-control" => "Xác nhận đúng người đang dư slot để automation tiếp tục.",
        "auto-session-control" => "Sửa liên kết Zalo/group cho đúng kèo để bot tiếp tục theo dõi.",
        "draft-workspace" => "Kiểm tra và bổ sung dữ liệu còn thiếu của đúng session này.",
        _ => "Xử lý bước đang vướng của đúng session này rồi bot sẽ tiếp tục."
    };
}
