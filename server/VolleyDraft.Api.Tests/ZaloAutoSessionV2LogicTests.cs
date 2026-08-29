using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionV2LogicTests
{
    [Fact]
    public void BuildOrganizerPreview_ExplainsWebsiteCreationAndCanarySafety()
    {
        var poll = BuildPoll("Bóng tuần này", new BridgePollOption("o1", "T4", 8, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.FromHours(7)));

        var live = ZaloAutoSessionV2Service.BuildOrganizerPreview(
            poll,
            candidates,
            3,
            6,
            4,
            "Sân UTE",
            ZaloAutoSessionRolloutMode.Live);
        var previewOnly = ZaloAutoSessionV2Service.BuildOrganizerPreview(
            poll,
            candidates,
            3,
            6,
            4,
            "Sân UTE",
            ZaloAutoSessionRolloutMode.PreviewOnly);

        Assert.Contains("PREVIEW WEBSITE", live);
        Assert.Contains("Bạn không cần nhớ câu lệnh", live);
        Assert.Contains("8/18 người", live);
        Assert.DoesNotContain("4 set", live);
        Assert.DoesNotContain("đội ×", live);
        Assert.Contains("Tui đã kiểm tra website", live);
        Assert.Contains("Website CHƯA được tạo", live);
        Assert.Contains("CANARY PREVIEW", previewOnly);
        Assert.Contains("KHÔNG tạo website", previewOnly);
    }

    [Fact]
    public void IgnoredClassifierDecision_RetriesAfterNewPollActivity()
    {
        var now = new DateTimeOffset(2026, 8, 29, 20, 15, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll("Bóng tuần này", new BridgePollOption("o1", "T4 17h30", 8, []));
        var existing = new ZaloPollSessionProposalData
        {
            Status = ZaloPollSessionProposalStatus.Ignored,
            ClassifierReason = "rule_score_below_threshold",
            PollUpdatedAtUnixMs = poll.UpdatedAtUnixMs - 1,
            UpdatedAt = now.AddMinutes(-3)
        };

        Assert.True(ZaloAutoSessionV2Service.ShouldRetryIgnoredProposal(
            existing,
            poll,
            now,
            TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void PermanentIgnoredDecision_DoesNotRetry()
    {
        var now = new DateTimeOffset(2026, 8, 29, 20, 15, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll("Bóng tuần này", new BridgePollOption("o1", "T4 17h30", 8, []));
        var existing = new ZaloPollSessionProposalData
        {
            Status = ZaloPollSessionProposalStatus.Ignored,
            ClassifierReason = "poll_creator_is_not_group_organizer",
            PollUpdatedAtUnixMs = poll.UpdatedAtUnixMs - 1,
            UpdatedAt = now.AddHours(-1)
        };

        Assert.False(ZaloAutoSessionV2Service.ShouldRetryIgnoredProposal(
            existing,
            poll,
            now,
            TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void PastSchedulePoll_DoesNotRetryAfterItHasNoCurrentOption()
    {
        var now = new DateTimeOffset(2026, 8, 29, 20, 15, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll("Bóng tuần này", new BridgePollOption("o1", "27/8 17h30", 8, []));
        var existing = new ZaloPollSessionProposalData
        {
            Status = ZaloPollSessionProposalStatus.Ignored,
            ClassifierReason = "no_current_schedule_option",
            PollUpdatedAtUnixMs = poll.UpdatedAtUnixMs - 1,
            UpdatedAt = now.AddHours(-2)
        };

        Assert.False(ZaloAutoSessionV2Service.ShouldRetryIgnoredProposal(
            existing,
            poll,
            now,
            TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void ApprovedDayDefault_ChangesOnlyOptionsWithoutExplicitTime()
    {
        var day = new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.FromHours(7));
        var candidates = new[]
        {
            new ZaloAutoSessionCandidate("o1", "CN", "CN", day, 4),
            new ZaloAutoSessionCandidate("o2", "T6 18h", "T6", day.AddDays(-2).AddMinutes(30), 5)
        };
        var learned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CN"] = 16 * 60,
            ["T6"] = 19 * 60
        };

        var result = ZaloAutoSessionV2Service.ApplyLearnedDayDefaults(candidates, learned);

        Assert.Equal(16, result[0].StartTime.ToOffset(TimeSpan.FromHours(7)).Hour);
        Assert.Equal(18, result[1].StartTime.ToOffset(TimeSpan.FromHours(7)).Hour);
    }

    [Fact]
    public void ExistingApprovalParser_AcceptsWebsiteGuidanceReplies()
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "T4 17h30", 5, []),
            new BridgePollOption("o2", "T6 17h30", 5, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.FromHours(7)));

        Assert.True(ZaloPollScheduleParser.IsApproval("xác nhận tạo website", candidates));
        Assert.True(ZaloPollScheduleParser.IsApproval("T4 18h rồi tạo", candidates));
        var selected = ZaloPollScheduleParser.SelectFromApproval("T4 18h rồi tạo", candidates);
        Assert.Single(selected);
        Assert.Equal(18, selected[0].StartTime.ToOffset(TimeSpan.FromHours(7)).Hour);
    }

    private static BridgePoll BuildPoll(string question, params BridgePollOption[] options) => new(
        "poll-v2",
        question,
        "captain-1",
        options,
        true,
        false,
        false,
        false,
        options.Sum(option => option.VoteCount),
        new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(7)).ToUnixTimeMilliseconds(),
        new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(7)).ToUnixTimeMilliseconds(),
        0);
}
