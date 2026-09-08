namespace VolleyDraft.Api.Services;

/// <summary>
/// Beginner-safe next-step copy after a player-profile mutation. The caller must build the
/// canonical draft-readiness snapshot after the mutation; profile completeness alone is never
/// treated as authority that a match can be drafted.
/// </summary>
public static class ZaloProfileUpdateReadinessCopy
{
    public static string Build(ZaloDraftReadinessSnapshot? readiness)
    {
        if (readiness is null)
            return " Tui đã lưu hồ sơ, nhưng chưa đọc được trạng thái trận để chỉ bước tiếp theo an toàn. Hãy thử `@Npc 9` để NPC kiểm lại dữ liệu thật; NPC sẽ không tự đoán.";

        return readiness.State switch
        {
            ZaloDraftReadinessState.Ready =>
                " Hồ sơ đã đủ và trạng thái hiện tại sẵn sàng chia đội. Trưởng/phó hoặc người được cấp quyền nói `draft đi`; NPC sẽ kiểm lại dữ liệu thật ngay trước khi chia.",

            ZaloDraftReadinessState.UnresolvedPassSlots =>
                $" Hồ sơ đã đủ nhưng còn {Math.Max(1, readiness.ActivePassSlotRiskCount)} chỗ đang nhường/chờ nhận chưa xong. Người nhường đổi ý dùng `huỷ pass`; người nhận đã vote đúng kèo dùng `xong`; người đang giữ chỗ nhận muốn nhả dùng `huỷ nhận`. Xử lý xong rồi nói `draft đi`.",

            ZaloDraftReadinessState.RosterNotFull =>
                BuildPartialRoster(readiness),

            ZaloDraftReadinessState.RosterOverCapacity =>
                $" Hồ sơ đã đủ nhưng hiện có {readiness.EffectiveSlotCount}/{readiness.Capacity} chỗ để chia đội, đang dư {Math.Max(0, readiness.EffectiveSlotCount - readiness.Capacity)}. Gõ `@Npc 4` để kiểm tra danh sách thật và xử lý người/chỗ dư trước. Khi danh sách hợp lệ, NPC sẽ đọc lại trạng thái; đừng chạy `draft đi` khi vẫn còn dư chỗ.",

            ZaloDraftReadinessState.MissingProfiles =>
                BuildMissingProfiles(readiness),

            ZaloDraftReadinessState.MissingStartTime =>
                " Hồ sơ đã đủ nhưng trận chưa có giờ bắt đầu. Nhờ admin chốt giờ trận trước; sau đó trưởng/phó nói `draft đi` để NPC kiểm lại và chia khi đủ điều kiện.",

            ZaloDraftReadinessState.SessionStarted =>
                " Hồ sơ đã lưu, nhưng trận đã tới hoặc qua giờ bắt đầu. NPC sẽ không tự chia đội muộn; nhờ admin kiểm tra trạng thái trận trước khi làm tiếp.",

            ZaloDraftReadinessState.NoRoster =>
                " Hồ sơ đã lưu nhưng danh sách hiện không có người chơi. Gõ `@Npc 4` để kiểm tra vote đúng trận. Nếu vẫn muốn gom người, trưởng/phó nói `kiếm thêm`; NPC không tự đoán rằng trận bị huỷ hay tự chia từ danh sách rỗng.",

            ZaloDraftReadinessState.AlreadyDrafted =>
                " Hồ sơ đã lưu. Trận này đã có kết quả chia đội; không chạy lại `draft đi` chỉ vì vừa sửa hồ sơ. Dùng `@Npc 10` để xem lại card/đội hình.",

            ZaloDraftReadinessState.InvalidStatus =>
                BuildInvalidStatus(readiness),

            _ =>
                " Hồ sơ đã lưu, nhưng trạng thái trận hiện chưa an toàn để chia đội. Dùng `@Npc 9` để NPC kiểm lại dữ liệu thật; NPC sẽ không tự đoán."
        };
    }

    private static string BuildPartialRoster(ZaloDraftReadinessSnapshot readiness)
    {
        var missing = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
        return $" Hồ sơ đã đủ nhưng hiện mới có {readiness.EffectiveSlotCount}/{readiness.Capacity} chỗ để chia đội, còn thiếu {missing}. " +
               "Gõ `@Npc 4` để xem danh sách thật. Nếu trưởng/phó muốn vẫn chơi với số người hiện tại thì nói `vẫn đánh` (hoặc `chốt " +
               readiness.EffectiveSlotCount + "`); nếu muốn tiếp tục tuyển thì nói `kiếm thêm`. Chỉ sau khi NPC chốt đúng hướng và kiểm lại danh sách mới dùng `draft đi`.";
    }

    private static string BuildMissingProfiles(ZaloDraftReadinessSnapshot readiness)
    {
        var names = readiness.MissingProfileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(10)
            .ToList();
        var subject = names.Count > 0
            ? string.Join(", ", names)
            : $"{Math.Max(1, readiness.MissingProfileCount)} người";
        return $" Còn hồ sơ thiếu: {subject}. Người được NPC hỏi có thể trả lời ngay `nam`, `thủ`, `mới chơi`... không cần @Npc; admin/trưởng/phó có thể dùng `@Npc cập nhật @Tên: nam, công, trung bình` và phải tag đúng người.";
    }

    private static string BuildInvalidStatus(ZaloDraftReadinessSnapshot readiness) =>
        readiness.ReasonCode switch
        {
            "draft_blocked_draft_in_progress" =>
                " Hồ sơ đã lưu. NPC đang chia đội nên không chạy thêm thao tác draft; chờ lần chia hiện tại kết thúc.",
            "draft_blocked_finished_without_team_result" =>
                " Hồ sơ đã lưu, nhưng trận đang được đánh dấu đã kết thúc mà hệ thống không có kết quả đội đầy đủ. Nhờ admin kiểm tra trạng thái trước khi làm tiếp.",
            "draft_blocked_existing_assignment" =>
                " Hồ sơ đã lưu, nhưng hệ thống đang có dữ liệu chia đội dở dang. NPC sẽ không tự chia tiếp hoặc chia lại; nhờ admin kiểm tra trước.",
            "draft_blocked_fingerprint_unavailable" =>
                " Hồ sơ đã lưu, nhưng NPC chưa kiểm tra được dữ liệu có đủ an toàn để chia đội hay chưa. Hãy thử lại `@Npc 9`; nếu vẫn lặp lại thì nhờ admin kiểm tra.",
            _ =>
                " Hồ sơ đã lưu, nhưng trạng thái trận hiện không cho phép chia đội an toàn. Nhờ admin kiểm tra trước khi chạy lại `@Npc 9`."
        };
}
