namespace VolleyDraft.Api.Services;

internal static class ZaloDraftPreparationClientCopy
{
    private const string RelockCurrentListHint =
        "Nếu vẫn muốn chơi với danh sách hiện tại, nói `vẫn đánh` để tui đọc lại vote và chốt lại từ dữ liệu mới; nếu muốn tiếp tục kiếm cho đủ thì nói `kiếm thêm`.";

    private const string MissingProfileRecoveryHint =
        "Người được NPC tag có thể trả lời ngay tin hỏi hồ sơ bằng `nam`, `thủ`, `mới chơi` hoặc `tui nam, đánh công, tầm trung bình`; không cần @Npc. " +
        "Nếu cần cập nhật thay, admin/trưởng/phó hoặc người có quyền bot dùng `@Npc cập nhật @Tên: nam, công, trung bình` và phải tag đúng người. Xong thì thử `draft đi` lại.";

    internal static string StopMatch(string changePrefix, string sessionName) =>
        $"{changePrefix}Ok, tui ghi nhận trưởng/phó chốt dừng kèo {sessionName}. Tui ngưng nhắc chia đội cho trận này nha. Tui chưa tự xoá trận, vote hay thao tác huỷ sân bên ngoài.";

    internal static string VoteRefreshFailed(string sessionName) =>
        $"Tui nghe quyết định rồi nhưng chưa đọc lại được đúng vote của {sessionName}, nên chưa dám chốt theo dữ liệu có thể cũ nha 😭 Tui chưa đổi gì; thử lại khi vote đọc được giúp tui.";

    internal static string KeepRecruitingAlreadyFull(string sessionName, int count, int capacity) =>
        $"Tui vừa đọc lại vote {sessionName}: đang {count}/{capacity} chỗ rồi nha 😆 Tui tạm ngưng gọi thêm khi đang đủ; nếu sau đó hụt chỗ, tui tiếp tục kiếm theo quyết định này mà không bắt trưởng/phó chốt lại.";

    internal static string KeepRecruiting(string changePrefix, string sessionName, int count, int capacity) =>
        $"{changePrefix}Ok, kèo {sessionName} tiếp tục kiếm thêm nha 👌 Hiện vote đang {count}/{capacity} chỗ; từ giờ tui chỉ báo khi số người/chỗ thay đổi, không tự đổi hướng nếu trưởng/phó chưa nói.";

    internal static string PassRisk(string sessionName, int count, int capacity, int riskCount) =>
        $"Tui vừa đọc lại vote {sessionName}: {count}/{capacity} chỗ nhưng đang có {riskCount} chỗ đang nhường/chờ nhận. Xử lý người nhường/người nhận xong trước nha; khi đúng trường hợp có thể dùng `huỷ pass`, `xong` hoặc `huỷ nhận`, rồi nói lại `vẫn đánh` hay `chốt {count}`.";

    internal static string OverCapacity(string sessionName, int count, int capacity) =>
        $"{sessionName} đang {count}/{capacity} chỗ, tức đang dư {count - capacity} chỗ nên tui chưa chốt chơi với danh sách này nha. Xử lý người dư hoặc chỗ chơi chung/luân phiên trước giúp tui.";

    internal static string CountMismatch(int count, int capacity, int requested) =>
        $"Tui vừa đọc lại vote: hiện là {count}/{capacity} chỗ, không phải {requested} nha 😆 Nếu vẫn chơi với danh sách hiện tại thì nói `chốt {count}` giúp tui để khỏi chốt nhầm người.";

    internal static string Empty(string sessionName, int capacity) =>
        $"Vote {sessionName} đang 0/{capacity} chỗ nên chưa có danh sách người chơi để chốt nha.";

    internal static string SafeSnapshotUnavailable(string sessionName) =>
        $"Tui đọc được danh sách người chơi của {sessionName} nhưng chưa thể khóa an toàn đúng danh sách hiện tại, nên chưa ghi nhận quyết định này. Tui chưa đổi gì; thử lại sau nha.";

    internal static string PlayerCountLabel(int presentPlayerCount, int effectiveSlotCount) =>
        presentPlayerCount == effectiveSlotCount
            ? $"{effectiveSlotCount} chỗ"
            : $"{presentPlayerCount} người, tính thành {effectiveSlotCount} chỗ để chia đội";

    internal static string MissingProfiles(
        string changePrefix,
        string countLabel,
        int missingProfileCount,
        IEnumerable<string> missingProfileNames) =>
        $"{changePrefix}Ok, tui ghi nhận kèo vẫn chơi với {countLabel} 👌 Nhưng còn {missingProfileCount} người thiếu thông tin để chia đội: {string.Join(", ", missingProfileNames.Take(6))}. {MissingProfileRecoveryHint}";

    internal static string MissingProfileBlocker(
        string sessionName,
        int missingProfileCount,
        IEnumerable<string> missingProfileNames) =>
        $"{sessionName} đã đủ người/chỗ nhưng còn {missingProfileCount} người thiếu thông tin để chia đội: {string.Join(", ", missingProfileNames.Take(8))}. {MissingProfileRecoveryHint}";

    internal static string Locked(
        string changePrefix,
        string countLabel,
        int teamCount,
        int playersPerTeam) =>
        $"{changePrefix}Ok chốt kèo hiện tại: {countLabel} → {teamCount} đội x{playersPerTeam} 👌 Khi muốn chia nói `draft đi`; tui sẽ đọc lại vote, danh sách người chơi và quyền lần cuối trước khi chạy.";

    internal static string NotEven(
        string changePrefix,
        string countLabel,
        int effectiveSlotCount,
        int teamCount) =>
        $"{changePrefix}Ok, tui ghi nhận kèo vẫn chơi với {countLabel} 👌 Nhưng {effectiveSlotCount} chỗ chưa chia đều được {teamCount} đội. Nếu muốn NPC tự chia, xử lý các chỗ chơi chung/luân phiên hoặc đổi số người về số chia hết cho {teamCount}; tui không tự bỏ hay thêm người.";

    internal static string DecisionActorRoleStale(string sessionName) =>
        $"Quyền trưởng/phó của người đã chốt danh sách {sessionName} không còn xác minh được, nên quyết định cũ hết hiệu lực. Tui chưa chia đội nha. Nhờ trưởng/phó hiện tại chọn lại hướng. {RelockCurrentListHint}";

    internal static string DraftVoteRefreshFailed(string sessionName) =>
        $"Tui chưa đọc lại được đúng vote {sessionName}, nên chưa chia đội trên dữ liệu có thể cũ nha. Tui chưa đổi gì; thử `draft đi` lại khi vote đọc được giúp tui.";

    internal static string PlayerListChanged(string sessionName) =>
        $"Danh sách người chơi {sessionName} vừa đổi so với lúc trưởng/phó chốt, nên quyết định cũ hết hiệu lực nha 😭 Tui chưa chia đội. {RelockCurrentListHint}";

    internal static string PartialPassRisk(string sessionName, int riskCount) =>
        $"{sessionName} đang có {riskCount} chỗ đang nhường/chờ nhận nên tui chưa chia đội nha. Xử lý người nhường/người nhận xong trước; khi đúng trường hợp có thể dùng `huỷ pass`, `xong` hoặc `huỷ nhận`. Xử lý xong nói `vẫn đánh` để tui đọc lại vote và chốt lại đúng danh sách trước khi chia đội.";

    internal static string PartialNotEven(int effectiveSlotCount, int teamCount) =>
        $"Kèo vẫn chơi thì ok, nhưng {effectiveSlotCount} chỗ chưa chia đều được {teamCount} đội nên NPC chưa thể tự chia. Xử lý các chỗ chơi chung/luân phiên hoặc đổi số người trước nha. Sau khi danh sách đổi, nói `vẫn đánh` để tui đọc lại vote và chốt lại đúng danh sách mới.";

    internal static string BuildDecisionChangePrefix(
        ZaloDraftPreparationDecisionSnapshot? previous,
        ZaloDraftPreparationDecisionKind nextKind,
        int? nextSlots,
        string actorName)
    {
        if (previous is null) return string.Empty;
        if (previous.Kind == nextKind &&
            (nextKind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster ||
             previous.EffectiveSlotCount == nextSlots))
            return string.Empty;

        var before = DescribeDecision(previous.Kind, previous.EffectiveSlotCount);
        var after = DescribeDecision(nextKind, nextSlots);
        return $"Cập nhật theo {actorName}: {before} → {after}. ";
    }

    private static string DescribeDecision(ZaloDraftPreparationDecisionKind kind, int? count) => kind switch
    {
        ZaloDraftPreparationDecisionKind.KeepRecruiting => "tiếp tục kiếm thêm",
        ZaloDraftPreparationDecisionKind.StopMatch => "dừng kèo",
        ZaloDraftPreparationDecisionKind.PlayCurrentRoster => count is { } slots
            ? $"chơi với {slots} chỗ hiện tại"
            : "chơi với danh sách hiện tại",
        _ => "đổi quyết định kèo"
    };
}