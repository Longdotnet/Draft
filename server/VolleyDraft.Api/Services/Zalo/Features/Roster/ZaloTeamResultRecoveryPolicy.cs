namespace VolleyDraft.Api.Services;

/// <summary>
/// Beginner-safe deterministic recovery when a team-lineup/card request has no
/// authoritative team result yet. The card command remains read-only; this policy
/// teaches only syntax that existing grounded handlers already own.
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
        var missingCommand = canEmbedSelector ? $"@Npc 4 {normalizedName}" : "@Npc 4";

        return $"{name} chưa có kết quả chia team nên hiện chưa có card 3 đội để gửi.\n" +
               "Lệnh 10 chỉ đọc kết quả đã có, không tự tạo đội hình. Bạn cũng không cần biết các từ như roster, draft hay sync. Làm theo vòng này:\n" +
               $"1) Trưởng nhóm, phó nhóm hoặc người được admin cấp quyền gõ `{draftCommand}`. NPC sẽ kiểm dữ liệu backend thật và nói đúng blocker hiện tại.\n" +
               $"2) Nếu NPC báo thiếu/dư người: gõ `{missingCommand}` để xem số chỗ còn thiếu, chỉnh vote/danh sách thật rồi gõ lại lệnh 9.\n" +
               "3) Nếu NPC báo hồ sơ chưa đủ: cập nhật đúng người, ví dụ `@Npc cập nhật Nick Tran: nam` hoặc `@Npc cập nhật Nick Tran: nam, công, trung bình`, rồi gõ lại lệnh 9.\n" +
               "4) Nếu NPC báo còn suất đang nhường/chờ nhận: owner đổi ý dùng `huỷ pass`; người nhận đã vote đúng kèo dùng `xong`; người đang giữ claim muốn nhả dùng `huỷ nhận`. NPC chỉ chốt khi trạng thái thật khớp.\n" +
               $"5) Chỉ khi NPC báo chia đội đã xong mới gõ `{imageCommand}` để lấy card 3 đội.\n" +
               "Nếu bạn không có quyền chạy lệnh 9, gửi nguyên hướng dẫn này cho trưởng/phó nhóm. AI có tắt thì các cú pháp trên vẫn đi qua handler deterministic.";
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

        // Recovery commands are rendered inline and can be pasted back into the bot.
        // Never splice transport-address or formatting delimiters into executable-looking
        // guidance; bare commands intentionally re-enter the authoritative selector flow.
        return !normalizedName.Contains('`') &&
               !normalizedName.Contains('@');
    }
}
