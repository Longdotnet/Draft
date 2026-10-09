namespace VolleyDraft.Api.Services;

internal static class ZaloTeamPreferenceReplyText
{
    public static string MissingParticipants(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "I couldn't identify the team relationship and everyone involved with enough confidence. You can say something like “don't put me with @Nguyễn on T6”, “put me with @Nguyễn”, or clearly @mention the players.",
        ZaloReplyLanguage.Korean =>
            "팀 관계와 관련된 사람들을 확실하게 파악하지 못했어요. “T6에서 저를 @Nguyễn과 같은 팀으로 하지 마세요”, “저를 @Nguyễn과 같은 팀으로 해줘”처럼 말하거나 관련 선수를 @mention 해 주세요.",
        _ =>
            "Mình chưa xác định chắc quan hệ team và những người liên quan. Bạn có thể nói tự nhiên kiểu “đừng xếp tui với @Nguyễn T6”, “put me with @Nguyễn” hoặc @mention rõ những người cần xếp."
    };

    public static string Query(
        ZaloReplyLanguage language,
        IReadOnlyList<string> players,
        ZaloTeamRelationshipKind relation) => language switch
    {
        ZaloReplyLanguage.English =>
            $"I understand you're asking whether {string.Join(" and ", players)} should be {(relation == ZaloTeamRelationshipKind.Apart ? "on different teams" : "on the same team")}. I haven't changed anything. If you want to create that constraint, state it directly.",
        ZaloReplyLanguage.Korean =>
            $"{string.Join(" 및 ", players)} 선수를 {(relation == ZaloTeamRelationshipKind.Apart ? "서로 다른 팀으로 둘지" : "같은 팀으로 둘지")} 묻는 것으로 이해했어요. 아직 데이터는 변경하지 않았어요. 이 조건을 설정하려면 요청을 명확하게 말해 주세요.",
        _ =>
            $"Mình hiểu đây là câu hỏi về việc {string.Join(" và ", players)} có {(relation == ZaloTeamRelationshipKind.Apart ? "khác team" : "chung team")} hay không. Mình chưa đổi dữ liệu; nếu bạn muốn đặt ràng buộc, hãy nói rõ yêu cầu."
    };

    public static string Clarification(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "I understand this is about team placement, but I'm not confident enough to change data yet. Do you want: (1) same team, (2) different teams, or (3) remove the existing preference?",
        ZaloReplyLanguage.Korean =>
            "팀 배치 관계에 대한 요청으로 이해했지만 아직 데이터를 바꿀 만큼 확실하지 않아요. 원하는 것은 (1) 같은 팀, (2) 다른 팀, (3) 기존 요청 삭제 중 무엇인가요?",
        _ =>
            "Mình hiểu câu này đang nói về quan hệ xếp team nhưng chưa đủ chắc để đổi dữ liệu. Bạn muốn: (1) chung team, (2) khác team, hay (3) bỏ yêu cầu cũ?"
    };

    public static string ApartRequiresExactlyTwo(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "A different-team preference needs exactly two players so the bot doesn't infer extra pairs. Please name or @mention exactly two players.",
        ZaloReplyLanguage.Korean =>
            "다른 팀 요청은 봇이 의도하지 않은 추가 조합을 추측하지 않도록 정확히 두 명이 필요해요. 두 명만 이름으로 적거나 @mention 해 주세요.",
        _ =>
            "Yêu cầu khác team cần đúng hai người để tránh bot tự suy ra nhiều cặp ngoài ý bạn. Hãy nêu hoặc @mention đúng hai người."
    };

    public static string AlreadyApplied(
        ZaloReplyLanguage language,
        string sessionName,
        TeamPreferencePreview plan)
    {
        var players = string.Join(", ", plan.PlayerNames);
        return language switch
        {
            ZaloReplyLanguage.English => plan.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"{players} already has this constraint removed in {sessionName}; I didn't create duplicate data."
                : plan.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"{players} already has a different-team constraint in {sessionName}; I didn't create duplicate data."
                    : $"{players} already has a same-team constraint in {sessionName}; I didn't create duplicate data.",
            ZaloReplyLanguage.Korean => plan.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"{sessionName}에서 {players}의 해당 조건은 이미 삭제되어 있어요. 중복 데이터는 만들지 않았어요."
                : plan.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"{sessionName}에서 {players}는 이미 서로 다른 팀 조건이 있어요. 중복 데이터는 만들지 않았어요."
                    : $"{sessionName}에서 {players}는 이미 같은 팀 조건이 있어요. 중복 데이터는 만들지 않았어요.",
            _ => plan.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"{players} đã không còn ràng buộc này trong {sessionName}; mình không tạo dữ liệu trùng."
                : plan.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"{players} đã có ràng buộc khác team trong {sessionName}; mình không tạo dữ liệu trùng."
                    : $"{players} đã có ràng buộc chung team trong {sessionName}; mình không tạo dữ liệu trùng."
        };
    }

    public static string Preview(
        ZaloReplyLanguage language,
        string sessionName,
        TeamPreferencePreview plan)
    {
        if (plan.Relation == ZaloTeamRelationshipKind.Apart)
        {
            var action = language switch
            {
                ZaloReplyLanguage.English => plan.Operation switch
                {
                    ZaloTeamRelationshipOperation.Clear => "remove the different-team constraint",
                    ZaloTeamRelationshipOperation.Change => "change the current relationship to different teams",
                    _ => "keep these two players on different teams"
                },
                ZaloReplyLanguage.Korean => plan.Operation switch
                {
                    ZaloTeamRelationshipOperation.Clear => "다른 팀 조건을 삭제",
                    ZaloTeamRelationshipOperation.Change => "현재 관계를 다른 팀으로 변경",
                    _ => "두 선수를 서로 다른 팀으로 유지"
                },
                _ => plan.Operation switch
                {
                    ZaloTeamRelationshipOperation.Clear => "bỏ ràng buộc khác team",
                    ZaloTeamRelationshipOperation.Change => "đổi quan hệ hiện tại thành khác team",
                    _ => "giữ hai người ở khác team"
                }
            };
            var warningBlock = FormatWarnings(language, plan.Warnings);
            return language switch
            {
                ZaloReplyLanguage.English =>
                    $"I understand the request for {sessionName}: {action}.\n" +
                    $"- Players: {string.Join(", ", plan.PlayerNames)}{warningBlock}\n\n" +
                    "I haven't changed anything yet. Type @bot confirm to apply or @bot cancel to stop.",
                ZaloReplyLanguage.Korean =>
                    $"{sessionName} 요청을 이렇게 이해했어요: {action}.\n" +
                    $"- 선수: {string.Join(", ", plan.PlayerNames)}{warningBlock}\n\n" +
                    "아직 데이터는 변경하지 않았어요. 적용하려면 @bot 확인, 취소하려면 @bot 취소를 입력해 주세요.",
                _ =>
                    $"Mình hiểu yêu cầu cho {sessionName}: {action}.\n" +
                    $"- Thành viên: {string.Join(", ", plan.PlayerNames)}{warningBlock}\n\n" +
                    "Mình chưa đổi dữ liệu. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ."
            };
        }

        if (plan.Operation == ZaloTeamRelationshipOperation.Clear)
        {
            return language switch
            {
                ZaloReplyLanguage.English =>
                    $"I understand you want to remove the same-team preference for {string.Join(", ", plan.PlayerNames)} in {sessionName}.\n\n" +
                    "I haven't changed anything yet. Type @bot confirm to apply or @bot cancel to stop.",
                ZaloReplyLanguage.Korean =>
                    $"{sessionName}에서 {string.Join(", ", plan.PlayerNames)}의 같은 팀 요청을 삭제하려는 것으로 이해했어요.\n\n" +
                    "아직 데이터는 변경하지 않았어요. 적용하려면 @bot 확인, 취소하려면 @bot 취소를 입력해 주세요.",
                _ =>
                    $"Mình hiểu bạn muốn bỏ yêu cầu chung team của {string.Join(", ", plan.PlayerNames)} trong {sessionName}.\n\n" +
                    "Mình chưa đổi dữ liệu. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ."
            };
        }

        var projected = plan.BestProjectedTeamScore is null
            ? language switch
            {
                ZaloReplyLanguage.English => "not available yet",
                ZaloReplyLanguage.Korean => "아직 계산되지 않음",
                _ => "chưa tính được"
            }
            : language switch
            {
                ZaloReplyLanguage.English =>
                    $"{plan.BestProjectedTeamScore:0.##} points (target about {plan.TargetTeamScore:0.##}, deviation {plan.ProjectedDeviation:0.##})",
                ZaloReplyLanguage.Korean =>
                    $"{plan.BestProjectedTeamScore:0.##}점 (목표 약 {plan.TargetTeamScore:0.##}, 차이 {plan.ProjectedDeviation:0.##})",
                _ =>
                    $"{plan.BestProjectedTeamScore:0.##} điểm (mục tiêu khoảng {plan.TargetTeamScore:0.##}, lệch {plan.ProjectedDeviation:0.##})"
            };
        var warnings = FormatWarnings(language, plan.Warnings);
        var merge = plan.ExistingGroupIds.Count > 0
            ? language switch
            {
                ZaloReplyLanguage.English => " The new request will extend/merge an existing same-team group.",
                ZaloReplyLanguage.Korean => " 새 요청은 기존 같은 팀 그룹을 확장하거나 병합하게 돼요.",
                _ => " Yêu cầu mới sẽ mở rộng/gộp nhóm đã có."
            }
            : string.Empty;

        return language switch
        {
            ZaloReplyLanguage.English =>
                $"I've calculated the same-team group for {sessionName}:\n" +
                $"- Players: {string.Join(", ", plan.PlayerNames)}\n" +
                $"- Uses {plan.EffectiveSlotCount}/{plan.TeamSize} slots; fixed total score {plan.GroupScore:0.##}, average {plan.GroupAverageScore:0.##}\n" +
                $"- Best projected completion: {projected}.{merge}{warnings}\n\n" +
                "I haven't changed anything yet. Type @bot confirm to merge this group or @bot cancel to stop.",
            ZaloReplyLanguage.Korean =>
                $"{sessionName}의 같은 팀 그룹을 계산했어요:\n" +
                $"- 선수: {string.Join(", ", plan.PlayerNames)}\n" +
                $"- {plan.EffectiveSlotCount}/{plan.TeamSize} 슬롯 사용; 고정 총점 {plan.GroupScore:0.##}, 평균 {plan.GroupAverageScore:0.##}\n" +
                $"- 가장 좋은 예상 완성 점수: {projected}.{merge}{warnings}\n\n" +
                "아직 데이터는 변경하지 않았어요. 그룹을 적용하려면 @bot 확인, 취소하려면 @bot 취소를 입력해 주세요.",
            _ =>
                $"Mình đã tính thử nhóm chung team cho {sessionName}:\n" +
                $"- Thành viên: {string.Join(", ", plan.PlayerNames)}\n" +
                $"- Chiếm {plan.EffectiveSlotCount}/{plan.TeamSize} slot; tổng điểm cố định {plan.GroupScore:0.##}, trung bình {plan.GroupAverageScore:0.##}\n" +
                $"- Phương án hoàn thiện tốt nhất: {projected}.{merge}{warnings}\n\n" +
                "Mình chưa đổi dữ liệu. Gõ @bot xác nhận để gộp nhóm này hoặc @bot huỷ."
        };
    }

    public static string Applied(
        ZaloReplyLanguage language,
        string sessionName,
        TeamRelationshipApplyResult result)
    {
        var players = string.Join(", ", result.PlayerNames);
        return language switch
        {
            ZaloReplyLanguage.English => result.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"Removed the {(result.Relation == ZaloTeamRelationshipKind.Apart ? "different-team" : "same-team")} preference for {players} in {sessionName}."
                : result.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"Recorded that {players} must be on different teams in {sessionName}. During the draft, the bot will keep the two sides apart even if one side belongs to another same-team group."
                    : $"Recorded that {players} want to be on the same team in {sessionName}. During the draft, the bot will keep the whole group together; each player still uses a separate slot unless they already share a slot.",
            ZaloReplyLanguage.Korean => result.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"{sessionName}에서 {players}의 {(result.Relation == ZaloTeamRelationshipKind.Apart ? "다른 팀" : "같은 팀")} 요청을 삭제했어요."
                : result.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"{sessionName}에서 {players}는 서로 다른 팀이어야 한다고 기록했어요. 드래프트할 때 한쪽이 다른 같은 팀 그룹에 속해 있어도 두 쪽을 같은 팀에 배치하지 않아요."
                    : $"{sessionName}에서 {players}가 같은 팀을 원한다고 기록했어요. 드래프트할 때 전체 그룹을 같은 팀에 유지하고, 기존에 슬롯을 공유한 경우를 제외하면 각 사람은 별도 슬롯을 사용해요.",
            _ => result.Operation == ZaloTeamRelationshipOperation.Clear
                ? $"Đã bỏ yêu cầu {(result.Relation == ZaloTeamRelationshipKind.Apart ? "khác team" : "chung team")} của {players} trong {sessionName}."
                : result.Relation == ZaloTeamRelationshipKind.Apart
                    ? $"Đã ghi nhận {players} phải ở khác team trong {sessionName}. Khi draft, bot sẽ không xếp hai bên vào cùng đội, kể cả khi một bên thuộc nhóm chung team khác."
                    : $"Đã ghi nhận {players} muốn ở cùng team trong {sessionName}. Khi draft, bot sẽ giữ cả nhóm cùng đội; mỗi người vẫn dùng một slot riêng, trừ người đã share slot từ trước."
        };
    }

    public static string PendingConfirmation(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "I'm waiting for confirmation of the team preference. Type @bot confirm to apply or @bot cancel; no data has changed yet.",
        ZaloReplyLanguage.Korean =>
            "팀 관계 요청 확인을 기다리고 있어요. 적용하려면 @bot 확인, 취소하려면 @bot 취소를 입력해 주세요. 아직 데이터는 변경되지 않았어요.",
        _ =>
            "Mình đang chờ xác nhận nhóm muốn chung team. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ; dữ liệu vẫn chưa đổi."
    };

    public static string SessionClarification(
        ZaloReplyLanguage language,
        IReadOnlyList<string> sessionChoices) => language switch
    {
        ZaloReplyLanguage.English =>
            $"I need to know which match this team preference is for. Send the request again with a date or match name{FormatChoices(sessionChoices)}.",
        ZaloReplyLanguage.Korean =>
            $"이 팀 관계 요청이 어느 경기인지 확인이 필요해요. 날짜나 경기 이름을 포함해서 다시 보내 주세요{FormatChoices(sessionChoices)}.",
        _ =>
            $"Mình cần biết yêu cầu xếp team này dành cho trận nào. Hãy gửi lại kèm ngày hoặc tên trận{FormatChoices(sessionChoices)}."
    };

    public static string PreviewFailed(ZaloReplyLanguage language, string? vietnameseDetail = null) => language switch
    {
        ZaloReplyLanguage.English =>
            "I couldn't calculate this team preference yet. Please check the players and match, then try again.",
        ZaloReplyLanguage.Korean =>
            "아직 이 팀 관계 요청을 계산하지 못했어요. 선수와 경기를 확인한 뒤 다시 시도해 주세요.",
        _ => string.IsNullOrWhiteSpace(vietnameseDetail)
            ? "Chưa tính được phương án quan hệ team."
            : vietnameseDetail
    };

    public static string Infeasible(ZaloReplyLanguage language, string? vietnameseDetail = null) => language switch
    {
        ZaloReplyLanguage.English =>
            "This team preference can't be applied with the current match constraints. Please adjust the conflicting team preference or group and try again.",
        ZaloReplyLanguage.Korean =>
            "현재 경기 조건에서는 이 팀 관계 요청을 적용할 수 없어요. 충돌하는 팀 관계나 그룹을 조정한 뒤 다시 시도해 주세요.",
        _ => string.IsNullOrWhiteSpace(vietnameseDetail)
            ? "Yêu cầu quan hệ team này chưa thể áp dụng."
            : vietnameseDetail
    };

    public static string ApplyFailed(ZaloReplyLanguage language, string? vietnameseDetail = null) => language switch
    {
        ZaloReplyLanguage.English =>
            "I couldn't save this team preference. The match state may have changed; please send the request again so I can re-check it.",
        ZaloReplyLanguage.Korean =>
            "이 팀 관계 요청을 저장하지 못했어요. 경기 상태가 바뀌었을 수 있으니 요청을 다시 보내서 재확인해 주세요.",
        _ => string.IsNullOrWhiteSpace(vietnameseDetail)
            ? "Chưa ghi nhận được yêu cầu quan hệ team."
            : vietnameseDetail
    };

    public static string OperatorVerificationFailed(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "I can't verify your Zalo group leader/deputy role right now. Please try again later or ask an admin to grant your UID access in Bot chat & reminder.",
        ZaloReplyLanguage.Korean =>
            "현재 Zalo 그룹장/부그룹장 권한을 확인할 수 없어요. 잠시 후 다시 시도하거나 관리자에게 Bot chat & reminder에서 UID 권한을 부여해 달라고 해 주세요.",
        _ =>
            "Mình chưa xác minh được quyền trưởng/phó nhóm từ Zalo lúc này. Bạn thử lại sau hoặc nhờ admin cấp UID trong phần Bot chat & reminder."
    };

    public static string OperatorRequired(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "This action changes data, so only the group leader, deputy, or a Zalo operator authorized by an admin can use it.",
        ZaloReplyLanguage.Korean =>
            "이 작업은 데이터를 변경하므로 그룹장, 부그룹장 또는 관리자가 권한을 준 Zalo operator만 사용할 수 있어요.",
        _ =>
            "Lệnh này thay đổi dữ liệu nên chỉ trưởng nhóm, phó nhóm hoặc Zalo operator được admin cấp quyền mới dùng được."
    };

    private static string FormatWarnings(ZaloReplyLanguage language, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return string.Empty;
        var heading = language switch
        {
            ZaloReplyLanguage.English => "\nThings to note:\n- ",
            ZaloReplyLanguage.Korean => "\n확인할 점:\n- ",
            _ => "\nĐiểm cần lưu ý:\n- "
        };
        return heading + string.Join("\n- ", warnings);
    }

    private static string FormatChoices(IReadOnlyList<string> choices) =>
        choices.Count == 0 ? string.Empty : $": {string.Join(", ", choices)}";
}
