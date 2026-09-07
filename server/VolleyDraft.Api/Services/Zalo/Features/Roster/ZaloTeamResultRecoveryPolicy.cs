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
        var name = string.IsNullOrWhiteSpace(sessionName)
            ? "trận này"
            : sessionName.Trim();
        var draftCommand = $"@Npc 9 {name}";
        var imageCommand = $"@Npc 10 {name}";

        return $"{name} chưa có kết quả chia team nên hiện chưa có card 3 đội để gửi.\n" +
               "Bạn không cần thử lệnh 10 liên tục. Trước khi chia đội, hãy chắc rằng danh sách người chơi đã chốt, " +
               "các suất đang nhường/chờ người nhận đã xử lý xong và hồ sơ người chơi đã đủ.\n" +
               $"Nếu bạn là trưởng nhóm, phó nhóm hoặc người được admin cấp quyền, gõ `{draftCommand}` để bắt đầu luồng chia đội; " +
               "NPC sẽ dùng dữ liệu backend và báo lại nếu còn điều kiện nào chưa đạt.\n" +
               $"Khi NPC báo draft xong, gõ `{imageCommand}` để lấy card 3 đội.";
    }
}
