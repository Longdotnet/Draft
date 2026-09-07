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
/// This workflow is intentionally AI-free: the poll parser owns calendar truth, the V4
/// reconciler owns three-way merge semantics, and callers may only mutate when CanExecute.
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
            durableDraft.Location,
            durableDraft.TeamSize);

        // Parser issues are authoritative ambiguity. We still return the safely parsed subset
        // for diagnostics, but execution must fail closed regardless of the material-diff result.
        var reconciliation = ZaloAutoSessionPollReconcilerV4.Reconcile(
            sourceSnapshot,
            durableDraft,
            currentSource);

        return new ZaloAutoSessionPollRevalidationV4(
            currentSource,
            reconciliation,
            extraction.Issues,
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
            return "Poll chỉ thay đổi dữ liệu không ảnh hưởng quyết định đã chốt; tui đã đồng bộ lại bản mới nhất.";

        var details = material.Take(4).Select(DescribeChange).ToList();
        var suffix = material.Count > details.Count ? $"; và {material.Count - details.Count} thay đổi khác" : string.Empty;
        return "Poll đã đổi sau preview: " + string.Join("; ", details) + suffix +
               ". Tui giữ các chỉnh sửa còn hợp lệ nhưng chưa tạo website. Hãy kiểm tra bản nháp mới rồi xác nhận lại.";
    }

    private static string DescribeChange(ZaloAutoSessionPollChangeV4 change) => change.Kind switch
    {
        ZaloAutoSessionPollChangeKindV4.OptionAdded => $"thêm lựa chọn {change.After}",
        ZaloAutoSessionPollChangeKindV4.OptionRemoved => $"bỏ lựa chọn {change.Before}",
        ZaloAutoSessionPollChangeKindV4.OptionIdentityChanged => $"đổi lịch {change.Before} → {change.After}",
        ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged => $"đổi giờ của {change.OptionId}",
        _ => $"thay đổi {change.OptionId}"
    };
}
