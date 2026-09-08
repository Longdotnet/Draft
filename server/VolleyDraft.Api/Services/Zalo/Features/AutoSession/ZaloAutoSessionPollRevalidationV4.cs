namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionPollRevalidationV4(
    ZaloAutoSessionConversationDraft CurrentSourceDraft,
    ZaloAutoSessionPollReconciliationV4 Reconciliation,
    IReadOnlyList<ZaloPollScheduleIssue> Issues,
    string CurrentStructureHash)
{
    public bool CanExecute => Issues.Count == 0 && !Reconciliation.RequiresConfirmation;
    public bool RequiresOrganizerConfirmation => Issues.Count == 0 && Reconciliation.RequiresConfirmation;
}

/// <summary>
/// Builds the authoritative current poll snapshot immediately before Auto Session mutation.
/// This workflow is intentionally AI-free: the poll parser owns calendar truth, approved
/// group configuration owns policy defaults, the V4 reconciler owns three-way merge semantics,
/// and callers may only mutate when CanExecute.
/// </summary>
internal static class ZaloAutoSessionPollRevalidationWorkflowV4
{
    public static ZaloAutoSessionPollRevalidationV4 Evaluate(
        BridgePoll currentPoll,
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationDraft sourceSnapshot,
        ZaloAutoSessionConversationDraft durableDraft,
        DateTimeOffset? currentTime = null)
    {
        var extraction = ZaloPollScheduleParser.ExtractSchedule(currentPoll, tracked, currentTime);
        var policyIssues = extraction.Issues.ToList();

        var organizerChangedLocation = !string.Equals(
            durableDraft.Location,
            sourceSnapshot.Location,
            StringComparison.Ordinal);
        var approvedLocation = tracked.DefaultLocation?.Trim() ?? string.Empty;
        var currentLocation = durableDraft.Location;
        if (!organizerChangedLocation)
        {
            if (approvedLocation.Length == 0)
            {
                policyIssues.Add(new ZaloPollScheduleIssue(
                    string.Empty,
                    string.Empty,
                    "approved_location_missing",
                    "Group chưa có sân mặc định được admin duyệt, nên tui không dùng lại sân cũ như một sự thật. Hãy nói rõ sân muốn dùng, ví dụ “sân UTE”, hoặc nhờ admin cấu hình sân mặc định."));
            }
            else
            {
                currentLocation = approvedLocation;
            }
        }

        var organizerChangedTeamSize = durableDraft.TeamSize != sourceSnapshot.TeamSize;
        var currentTeamSize = durableDraft.TeamSize;
        if (!organizerChangedTeamSize)
        {
            if (tracked.DefaultTeamSize < 2)
            {
                policyIssues.Add(new ZaloPollScheduleIssue(
                    string.Empty,
                    string.Empty,
                    "approved_team_size_missing",
                    "Group chưa có số người mỗi đội được admin duyệt, nên tui chưa tự đoán cấu hình trận. Hãy nói rõ tổng số người/cấu hình đội muốn dùng hoặc nhờ admin cấu hình mặc định."));
            }
            else
            {
                currentTeamSize = tracked.DefaultTeamSize;
            }
        }

        var currentSource = new ZaloAutoSessionConversationDraft(
            extraction.Candidates.Select(candidate => new ZaloAutoSessionConversationDraftItem(
                candidate.OptionId,
                candidate.OptionContent,
                candidate.DayKey,
                candidate.StartTime,
                candidate.VoteCount,
                sourceSnapshot.Items.Any(item =>
                    string.Equals(item.OptionId, candidate.OptionId, StringComparison.Ordinal) && item.Selected)))
                .ToList(),
            currentLocation,
            currentTeamSize);

        // Parser/policy issues are authoritative ambiguity. We still return the safely parsed
        // subset for diagnostics, but execution must fail closed regardless of material diff.
        var reconciliation = ZaloAutoSessionPollReconcilerV4.Reconcile(
            sourceSnapshot,
            durableDraft,
            currentSource);

        return new ZaloAutoSessionPollRevalidationV4(
            currentSource,
            reconciliation,
            policyIssues,
            ZaloPollScheduleParser.ComputeStructureHash(currentPoll));
    }

    public static string BuildOrganizerMessage(ZaloAutoSessionPollRevalidationV4 result)
    {
        if (result.Issues.Count > 0)
        {
            var issue = result.Issues[0];
            return issue.Message + " Website vẫn chưa được tạo.";
        }

        var material = result.Reconciliation.Changes
            .Where(change => change.RequiresConfirmation)
            .ToList();
        if (material.Count == 0)
            return "Poll chỉ thay đổi dữ liệu hoặc default đã được admin duyệt; tui đã đồng bộ lại bản mới nhất.";

        var details = material.Take(4).Select(DescribeChange).ToList();
        var suffix = material.Count > details.Count ? $"; và {material.Count - details.Count} thay đổi khác" : string.Empty;
        var nextAction = BuildGroundedNextAction(result, material);
        return "Poll đã đổi sau preview: " + string.Join("; ", details) + suffix +
               ". Tui giữ các chỉnh sửa còn hợp lệ và website vẫn chưa được tạo. " + nextAction;
    }

    private static string BuildGroundedNextAction(
        ZaloAutoSessionPollRevalidationV4 result,
        IReadOnlyList<ZaloAutoSessionPollChangeV4> material)
    {
        var added = material
            .Where(change => change.Kind == ZaloAutoSessionPollChangeKindV4.OptionAdded)
            .Select(change => result.Reconciliation.Draft.Items.FirstOrDefault(item =>
                string.Equals(item.OptionId, change.OptionId, StringComparison.Ordinal)))
            .Where(item => item is not null)
            .Cast<ZaloAutoSessionConversationDraftItem>()
            .ToList();

        if (added.Count > 0)
        {
            var selectors = string.Join(", ", added.Select(item => item.DayKey).Distinct(StringComparer.OrdinalIgnoreCase));
            var example = added[0].DayKey;
            return $"Lựa chọn mới đang để CHƯA chọn để tránh tự ý thêm lịch. Muốn lấy thêm thì nói “thêm {example}”" +
                   (added.Count > 1 ? $" (các lựa chọn mới: {selectors})" : string.Empty) +
                   "; nếu giữ bản nháp hiện tại thì nói “tạo đi”.";
        }

        var timeChange = material.FirstOrDefault(change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged);
        if (timeChange is not null)
        {
            var item = result.Reconciliation.Draft.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.OptionId, timeChange.OptionId, StringComparison.Ordinal));
            if (item is not null)
            {
                var local = item.StartTime.ToOffset(TimeSpan.FromHours(7));
                return $"Giờ mới trong poll của {item.DayKey} là {local:HH:mm}. Nếu giờ này đúng thì nói “tạo đi”; muốn sửa thì nói kiểu “{item.DayKey} 18h”.";
            }
        }

        if (material.Any(change => change.Kind == ZaloAutoSessionPollChangeKindV4.OptionRemoved))
            return "Lựa chọn đã bị xóa khỏi poll cũng đã bị loại khỏi bản nháp. Nếu đó là ý bạn thì nói “tạo đi”; nếu không, hãy sửa lại poll trước.";

        if (material.Any(change => change.Kind == ZaloAutoSessionPollChangeKindV4.OptionIdentityChanged))
            return "Ngày/lịch trong poll đã đổi nên tui dùng lịch mới, không tự giữ ngày cũ. Nếu bản nháp mới đúng thì nói “tạo đi”; nếu không, hãy sửa lại poll trước.";

        return "Hãy kiểm tra đúng phần vừa đổi; nếu bản nháp mới đúng thì nói “tạo đi”, còn muốn chỉnh thì nói trực tiếp ngày/giờ/sân cần đổi.";
    }

    private static string DescribeChange(ZaloAutoSessionPollChangeV4 change) => change.Kind switch
    {
        ZaloAutoSessionPollChangeKindV4.OptionAdded => $"thêm lựa chọn {change.After}",
        ZaloAutoSessionPollChangeKindV4.OptionRemoved => $"bỏ lựa chọn {change.Before}",
        ZaloAutoSessionPollChangeKindV4.OptionIdentityChanged => $"đổi lịch {change.Before} → {change.After}",
        ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged => $"đổi giờ của {change.OptionId}",
        ZaloAutoSessionPollChangeKindV4.ApprovedLocationChanged => $"đổi sân mặc định {change.Before} → {change.After}",
        ZaloAutoSessionPollChangeKindV4.ApprovedTeamSizeChanged => $"đổi số người/đội mặc định {change.Before} → {change.After}",
        _ => $"thay đổi {change.OptionId}"
    };
}
