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
               "Bạn không cần thử lệnh 10 liên tục. Trước khi chia đội, hãy chắc rằng danh sách người chơi đã chốt, " +
               "các suất đang nhường/chờ người nhận đã xử lý xong và hồ sơ người chơi đã đủ.\n" +
               $"Nếu bạn là trưởng nhóm, phó nhóm hoặc người được admin cấp quyền, gõ `{draftCommand}` để bắt đầu luồng chia đội; " +
               "NPC sẽ dùng dữ liệu backend và báo lại nếu còn điều kiện nào chưa đạt.\n" +
               $"Khi NPC báo draft xong, gõ `{imageCommand}` để lấy card 3 đội.";
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
