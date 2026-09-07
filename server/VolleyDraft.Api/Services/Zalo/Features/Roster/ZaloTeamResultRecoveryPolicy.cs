using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Beginner-safe deterministic recovery when a team-lineup/card request has no
/// authoritative team result yet. Grounded readiness may explain the blocker,
/// while authoritative roster/profile/draft services remain the source of truth.
/// </summary>
public static class ZaloTeamResultRecoveryPolicy
{
    public static bool HasAuthoritativeTeamResult(IReadOnlyList<TeamPreviewResponse> teams) =>
        teams.Any(team => team.Slots.Count > 0);

    public static string BuildNoResultMessage(string sessionName) =>
        BuildNoResultMessage(sessionName, null);

    public static string BuildNoResultMessage(
        string sessionName,
        ZaloDraftReadinessSnapshot? readiness)
    {
        var normalizedName = NormalizeDisplayName(sessionName);
        var hasGroundedName = normalizedName.Length > 0;
        var canEmbedSelector = hasGroundedName && IsSafeInlineCommandSelector(sessionName, normalizedName);
        var name = hasGroundedName ? normalizedName : "Buổi này";
        var draftCommand = canEmbedSelector ? $"@Npc 9 {normalizedName}" : "@Npc 9";
        var imageCommand = canEmbedSelector ? $"@Npc 10 {normalizedName}" : "@Npc 10";
        var header = $"{name} chưa có kết quả chia team nên hiện chưa có card 3 đội để gửi.\n" +
                     "Bạn không cần thử lệnh 10 liên tục. ";

        if (readiness is null)
        {
            return header +
                   "Trước khi chia đội, hãy chắc rằng danh sách người chơi đã chốt, " +
                   "các suất đang nhường/chờ người nhận đã xử lý xong và hồ sơ người chơi đã đủ.\n" +
                   $"Nếu bạn là trưởng nhóm, phó nhóm hoặc người được admin cấp quyền, gõ `{draftCommand}` để bắt đầu luồng chia đội; " +
                   "NPC sẽ dùng dữ liệu backend và báo lại nếu còn điều kiện nào chưa đạt.\n" +
                   $"Khi NPC báo draft xong, gõ `{imageCommand}` để lấy card 3 đội.";
        }

        var blocker = BuildGroundedBlocker(readiness, draftCommand, imageCommand);
        return header + blocker;
    }

    private static string BuildGroundedBlocker(
        ZaloDraftReadinessSnapshot readiness,
        string draftCommand,
        string imageCommand)
    {
        switch (readiness.State)
        {
            case ZaloDraftReadinessState.NoRoster:
                return "Danh sách hiện chưa có người chơi nào. Admin cần đồng bộ/chốt danh sách của trận trước. " +
                       $"Khi đã có người, người có quyền dùng `{draftCommand}` để NPC kiểm tra và chia đội.";

            case ZaloDraftReadinessState.RosterNotFull:
            {
                var missing = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
                var sharedNote = readiness.PresentPlayerCount > readiness.EffectiveSlotCount
                    ? $" Hiện có {readiness.PresentPlayerCount} người nhưng backend tính {readiness.EffectiveSlotCount} suất vì có người dùng chung suất."
                    : string.Empty;
                return $"Backend đang tính {readiness.EffectiveSlotCount}/{readiness.Capacity} suất, còn thiếu {missing} suất.{sharedNote} " +
                       "Hãy chốt thêm người hoặc xử lý lại danh sách nếu số suất chưa đúng; chưa nên draft lúc này.";
            }

            case ZaloDraftReadinessState.RosterOverCapacity:
            {
                var extra = Math.Max(0, readiness.EffectiveSlotCount - readiness.Capacity);
                return $"Backend đang tính {readiness.EffectiveSlotCount}/{readiness.Capacity} suất, dư {extra} suất. " +
                       "Cần chốt lại người chơi/share suất trước khi chia đội; NPC sẽ không tự bỏ người để đủ số.";
            }

            case ZaloDraftReadinessState.MissingProfiles:
            {
                var visibleNames = readiness.MissingProfileNames.Take(6).ToList();
                var names = visibleNames.Count == 0 ? "người chơi chưa đủ hồ sơ" : string.Join(", ", visibleNames);
                var remainder = Math.Max(0, readiness.MissingProfileCount - visibleNames.Count);
                var more = remainder > 0 ? $" và {remainder} người khác" : string.Empty;
                var example = visibleNames.FirstOrDefault();
                var exampleText = string.IsNullOrWhiteSpace(example)
                    ? string.Empty
                    : $" Ví dụ: `@Npc cập nhật {example}: nam`.";
                return $"Danh sách đã đủ suất nhưng còn {readiness.MissingProfileCount} hồ sơ chưa đủ: {names}{more}." +
                       exampleText +
                       $" Sau khi hồ sơ đủ, người có quyền dùng `{draftCommand}`.";
            }

            case ZaloDraftReadinessState.MissingStartTime:
                return "Danh sách và hồ sơ đã sẵn sàng nhưng trận chưa có giờ bắt đầu authoritative. " +
                       $"Admin cần chốt giờ trước; sau đó người có quyền dùng `{draftCommand}`.";

            case ZaloDraftReadinessState.SessionStarted:
                return "Giờ bắt đầu của trận đã tới hoặc đã qua nhưng chưa có kết quả chia đội. " +
                       "NPC sẽ không tự chia muộn chỉ vì lệnh 10; hãy nhờ admin kiểm tra trạng thái trận.";

            case ZaloDraftReadinessState.AlreadyDrafted:
                return "Backend ghi nhận trận đã draft nhưng hiện không đọc được slot đội hình authoritative. " +
                       "NPC không gửi card rỗng; hãy nhờ admin kiểm tra dữ liệu draft trước khi thử lại.";

            case ZaloDraftReadinessState.InvalidStatus:
                return readiness.ReasonCode switch
                {
                    "draft_blocked_draft_in_progress" =>
                        $"Trận đang trong lúc draft. Chờ NPC báo hoàn tất rồi dùng `{imageCommand}`; không cần chạy lệnh 9 lần nữa.",
                    "draft_blocked_existing_assignment" =>
                        "Backend đang có assignment đội hình nhưng trạng thái draft chưa nhất quán. NPC không đoán đội và không gửi card rỗng; hãy nhờ admin kiểm tra trước.",
                    "draft_blocked_fingerprint_unavailable" =>
                        "NPC chưa xác minh được trạng thái danh sách/share suất an toàn để draft. Hãy thử lại sau hoặc nhờ admin kiểm tra; NPC sẽ không đoán.",
                    _ =>
                        "Trạng thái trận hiện chưa cho phép chia đội an toàn. NPC không đoán hoặc tự đổi trạng thái; hãy nhờ admin kiểm tra trước."
                };

            case ZaloDraftReadinessState.Ready:
                return $"Danh sách đã đủ {readiness.EffectiveSlotCount}/{readiness.Capacity} suất, hồ sơ đã đủ và giờ trận đã chốt. " +
                       $"Nếu bạn là trưởng nhóm, phó nhóm hoặc người được admin cấp quyền, gõ `{draftCommand}` để chia đội. " +
                       $"Khi NPC báo draft xong, gõ `{imageCommand}` để lấy card 3 đội.";

            default:
                return $"NPC chưa xác định được blocker an toàn. Người có quyền có thể dùng `{draftCommand}` để chạy lại kiểm tra deterministic trước khi draft.";
        }
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
