namespace VolleyDraft.Api.Services;

/// <summary>
/// Beginner-safe deterministic recovery when a team-lineup/card request cannot
/// return a complete authoritative team result. The card command remains read-only;
/// this policy teaches only syntax that existing grounded handlers already own.
/// </summary>
public static class ZaloTeamResultRecoveryPolicy
{
    public static string BuildNoResultMessage(
        string sessionName,
        ZaloDraftReadinessSnapshot? readiness = null)
    {
        var normalizedName = NormalizeDisplayName(sessionName);
        var hasGroundedName = normalizedName.Length > 0;
        var canEmbedSelector = hasGroundedName && IsSafeInlineCommandSelector(sessionName, normalizedName);
        var name = hasGroundedName ? normalizedName : "Buổi này";
        var draftCommand = canEmbedSelector ? $"@Npc 9 {normalizedName}" : "@Npc 9";
        var imageCommand = canEmbedSelector ? $"@Npc 10 {normalizedName}" : "@Npc 10";
        var missingCommand = canEmbedSelector ? $"@Npc 4 {normalizedName}" : "@Npc 4";

        if (readiness?.HasTeams == true)
        {
            return $"{name} đã có kết quả chia team trong backend nhưng NPC chưa đọc được card/đội hình đầy đủ. " +
                   $"Thử lại `{imageCommand}`; nếu vẫn lặp lại, nhờ admin kiểm tra. " +
                   "Không chạy lại lệnh 9 chỉ để chữa card vì có thể làm thay đổi đội hình. " +
                   "Thông tin này lấy trực tiếp từ dữ liệu trận và không phụ thuộc AI.";
        }

        var header = $"{name} chưa có kết quả chia team chính thức nên hiện chưa có card 3 đội để gửi.\n" +
                     "Lệnh 10 chỉ đọc kết quả đã có, không tự tạo đội hình.";

        if (readiness is null)
        {
            return header + " Bạn cũng không cần biết các từ như roster, draft hay sync. Làm theo vòng này:\n" +
                   $"1) Trưởng nhóm, phó nhóm hoặc người được admin cấp quyền gõ `{draftCommand}`. NPC sẽ kiểm dữ liệu backend thật và nói đúng blocker hiện tại.\n" +
                   $"2) Nếu NPC báo thiếu/dư người: gõ `{missingCommand}` để xem số chỗ còn thiếu, chỉnh vote/danh sách thật rồi gõ lại lệnh 9.\n" +
                   "3) Nếu NPC báo hồ sơ chưa đủ: cập nhật đúng người, ví dụ `@Npc cập nhật Nick Tran: nam` hoặc `@Npc cập nhật Nick Tran: nam, công, trung bình`, rồi gõ lại lệnh 9.\n" +
                   "4) Nếu NPC báo còn suất đang nhường/chờ nhận: owner đổi ý dùng `huỷ pass`; người nhận đã vote đúng kèo dùng `xong`; người đang giữ claim muốn nhả dùng `huỷ nhận`. NPC chỉ chốt khi trạng thái thật khớp.\n" +
                   $"5) Chỉ khi NPC báo chia đội đã xong mới gõ `{imageCommand}` để lấy card 3 đội.\n" +
                   "Nếu bạn không có quyền chạy lệnh 9, gửi nguyên hướng dẫn này cho trưởng/phó nhóm. AI có tắt thì các cú pháp trên vẫn đi qua handler deterministic.";
        }

        var next = BuildGroundedNextStep(
            readiness,
            draftCommand,
            imageCommand,
            missingCommand);
        return header + "\n" + next +
               "\nThông tin trên lấy trực tiếp từ dữ liệu trận; AI có tắt thì lệnh 4, 9, 10 và các bước xử lý vẫn hoạt động.";
    }

    private static string BuildGroundedNextStep(
        ZaloDraftReadinessSnapshot readiness,
        string draftCommand,
        string imageCommand,
        string missingCommand)
    {
        return readiness.State switch
        {
            ZaloDraftReadinessState.Ready =>
                $"Hiện đã đủ người và hồ sơ để chia đội. Trưởng nhóm, phó nhóm hoặc người được admin cấp quyền gõ `{draftCommand}`; khi NPC báo chia xong thì gõ `{imageCommand}`.",

            ZaloDraftReadinessState.UnresolvedPassSlots =>
                $"Hiện còn {Math.Max(1, readiness.ActivePassSlotRiskCount)} suất đang nhường/chờ nhận chưa hoàn tất. " +
                "Người nhường đổi ý dùng `huỷ pass`; người nhận đã vote đúng kèo dùng `xong`; người đang giữ claim muốn nhả dùng `huỷ nhận`. " +
                $"Xử lý xong rồi nhờ trưởng/phó chạy `{draftCommand}`, sau đó mới dùng `{imageCommand}`.",

            ZaloDraftReadinessState.RosterNotFull =>
                $"Hiện mới có {readiness.EffectiveSlotCount}/{readiness.Capacity} chỗ đủ điều kiện để chia, còn thiếu {Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount)} người/chỗ. " +
                $"Gõ `{missingCommand}` để xem số lượng hiện tại, chỉnh vote/danh sách thật rồi nhờ trưởng/phó chạy `{draftCommand}`.",

            ZaloDraftReadinessState.RosterOverCapacity =>
                $"Hiện có {readiness.EffectiveSlotCount}/{readiness.Capacity} chỗ đủ điều kiện để chia, đang dư {Math.Max(0, readiness.EffectiveSlotCount - readiness.Capacity)} người/chỗ. " +
                $"Gõ `{missingCommand}` để kiểm tra số lượng, chỉnh vote/danh sách thật rồi nhờ trưởng/phó chạy `{draftCommand}`.",

            ZaloDraftReadinessState.MissingProfiles =>
                BuildMissingProfilesMessage(readiness, draftCommand),

            ZaloDraftReadinessState.NoRoster =>
                $"Danh sách hiện chưa có người chơi nào. Gõ `{missingCommand}` để kiểm tra số chỗ, đồng bộ/chỉnh vote đúng trận rồi nhờ trưởng/phó chạy `{draftCommand}`.",

            ZaloDraftReadinessState.MissingStartTime =>
                $"Danh sách đã đủ nhưng trận chưa được chốt giờ bắt đầu. Nhờ admin chốt giờ trận trong cấu hình, rồi trưởng/phó chạy `{draftCommand}`.",

            ZaloDraftReadinessState.SessionStarted =>
                "Trận đã tới hoặc qua giờ bắt đầu nhưng chưa có kết quả đội chính thức. Lệnh 10 sẽ không tự đoán hay tự chia đội muộn; nhờ admin kiểm tra trạng thái trận trước khi làm tiếp.",

            ZaloDraftReadinessState.AlreadyDrafted =>
                $"Backend báo đã có kết quả đội nhưng card chưa đọc được đầy đủ. Thử lại `{imageCommand}`; nếu vẫn lỗi, nhờ admin kiểm tra và không tự draft lại.",

            ZaloDraftReadinessState.InvalidStatus =>
                BuildInvalidStatusMessage(readiness, imageCommand),

            _ =>
                $"NPC chưa xác định được trạng thái an toàn để xuất card. Nhờ admin kiểm tra dữ liệu trận; không tự đoán đội hình. Sau khi trạng thái ổn định, thử lại `{imageCommand}`."
        };
    }

    private static string BuildMissingProfilesMessage(
        ZaloDraftReadinessSnapshot readiness,
        string draftCommand)
    {
        var names = readiness.MissingProfileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(10)
            .ToList();
        var subject = names.Count > 0
            ? string.Join(", ", names)
            : $"{Math.Max(1, readiness.MissingProfileCount)} người";
        var example = names.FirstOrDefault() ?? "Nick Tran";
        return $"Còn thiếu hồ sơ của: {subject}. " +
               $"Cập nhật từng người, ví dụ `@Npc cập nhật {example}: nam` hoặc `@Npc cập nhật {example}: nam, công, trung bình`; sau đó nhờ trưởng/phó chạy `{draftCommand}`.";
    }

    private static string BuildInvalidStatusMessage(
        ZaloDraftReadinessSnapshot readiness,
        string imageCommand)
    {
        return readiness.ReasonCode switch
        {
            "draft_blocked_draft_in_progress" =>
                $"NPC đang trong quá trình chia đội nên lệnh 10 không gửi đội hình giữa chừng. Chờ thao tác hiện tại hoàn tất rồi gõ lại `{imageCommand}`.",
            "draft_blocked_finished_without_team_result" =>
                "Trận đang được đánh dấu đã kết thúc nhưng backend không có kết quả đội đầy đủ. Lệnh 10 sẽ không bịa đội hình; nhờ admin kiểm tra trạng thái/dữ liệu trước khi chạy lại.",
            "draft_blocked_existing_assignment" =>
                "Backend đang có dữ liệu chia đội dở dang nhưng chưa phải kết quả chính thức. Lệnh 10 sẽ không công bố đội hình một phần; nhờ admin kiểm tra trước khi làm tiếp.",
            "draft_blocked_fingerprint_unavailable" =>
                "NPC chưa kiểm tra được trạng thái dữ liệu đủ an toàn để chia đội. Hãy thử lại sau; nếu vẫn lặp lại thì nhờ admin kiểm tra, không tự đoán đội hình.",
            _ =>
                "Trạng thái trận hiện không cho phép coi đội hình là kết quả chính thức. Lệnh 10 sẽ không tự đoán hoặc tự sửa dữ liệu; nhờ admin kiểm tra trước khi làm tiếp."
        };
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
