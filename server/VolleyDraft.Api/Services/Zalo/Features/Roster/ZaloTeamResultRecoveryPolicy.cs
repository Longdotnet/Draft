namespace VolleyDraft.Api.Services;

/// <summary>
/// Beginner-safe deterministic recovery when a team-lineup/card request has no
/// authoritative team result yet. This policy intentionally does not guess why
/// the draft is missing: roster, profile, pass/share and draft validation remain
/// owned by their domain services.
/// </summary>
public static class ZaloTeamResultRecoveryPolicy
{
    public static string BuildNoResultMessage(string sessionName)
    {
        var normalizedName = NormalizeDisplayName(sessionName);
        var hasGroundedName = normalizedName.Length > 0;
        var canEmbedSelector = hasGroundedName && IsSafeInlineCommandSelector(sessionName, normalizedName);
        var name = hasGroundedName ? normalizedName : "Buổi này";
        var draftCommand = canEmbedSelector ? $"@Npc 9 {normalizedName}" : "@Npc 9";
        var imageCommand = canEmbedSelector ? $"@Npc 10 {normalizedName}" : "@Npc 10";

        return $"{name} chưa có kết quả chia team nên hiện chưa có card 3 đội để gửi.\n" +
               "Bạn không cần biết các từ như roster, draft hay sync, và cũng không cần thử lệnh 10 liên tục. Làm theo đúng vòng này:\n" +
               $"1) Trưởng nhóm, phó nhóm hoặc người được admin cấp quyền gõ `{draftCommand}`. NPC sẽ kiểm dữ liệu backend thật trước khi chia đội; nếu còn thiếu người, dư người, hồ sơ chưa đủ hoặc còn suất đang nhường/chờ người nhận, NPC sẽ chặn và hướng dẫn bước cần xử lý tiếp.\n" +
               "2) Làm xong đúng blocker NPC vừa báo rồi gõ lại lệnh 9. Không cần tự đoán trạng thái trong nhóm chat.\n" +
               $"3) Chỉ khi NPC báo draft đã xong, gõ `{imageCommand}` để lấy card 3 đội.\n" +
               "Nếu bạn không có quyền chạy lệnh 9, gửi đúng hướng dẫn này cho trưởng/phó nhóm; lệnh 10 không thể tự tạo đội hình.";
    }

    private static string NormalizeDisplayName(string? sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName)) return string.Empty;

        return string.Join(
            ' ',
            sessionName
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsSafeInlineCommandSelector(string rawName, string normalizedName)
    {
        if (normalizedName.Length > 160) return false;
        if (rawName.Any(char.IsControl)) return false;

        // The recovery command is rendered inline inside backticks and fed back to the
        // bot as an exact deterministic command. Session names containing bot-address
        // tokens or formatting delimiters must not be spliced into executable-looking
        // guidance. Bare @Npc 9/@Npc 10 intentionally re-enter the grounded selector.
        return !normalizedName.Contains('`') &&
               !normalizedName.Contains('@');
    }
}
