using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionParserTests
{
    private static readonly DateTimeOffset CurrentVietnamTime =
        new(2026, 8, 18, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void ExtractCandidates_ParsesVietnameseWeekdaysAndAssumesEvening()
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "Thứ 4 - 5h30", 8, []),
            new BridgePollOption("o2", "T6 17:30", 12, []),
            new BridgePollOption("o3", "CN 16h", 10, []));
        var tracked = new ZaloTrackedGroupData();

        var result = ZaloPollScheduleParser.ExtractCandidates(poll, tracked, CurrentVietnamTime);

        Assert.Equal(3, result.Count);
        Assert.Equal("T4", result[0].DayKey);
        Assert.Equal(17, result[0].StartTime.ToOffset(TimeSpan.FromHours(7)).Hour);
        Assert.Equal(30, result[0].StartTime.ToOffset(TimeSpan.FromHours(7)).Minute);
        Assert.Equal("T6", result[1].DayKey);
        Assert.Equal("CN", result[2].DayKey);
    }

    [Fact]
    public void ExtractCandidates_DropsOldScheduleOptionsDuringReconciliation()
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "T4 17h30", 8, []),
            new BridgePollOption("o2", "T6 17h30", 12, []));
        var muchLater = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(7));

        var result = ZaloPollScheduleParser.ExtractCandidates(poll, new ZaloTrackedGroupData(), muchLater);

        Assert.Empty(result);
    }

    [Fact]
    public void StructureHash_DoesNotChangeWhenOnlyVoteCountsChange()
    {
        var before = BuildPoll(
            "Kèo tuần này",
            new BridgePollOption("o1", "T4 17h30", 5, ["u1"]),
            new BridgePollOption("o2", "T6 17h30", 8, ["u2"]));
        var after = before with
        {
            UpdatedAtUnixMs = before.UpdatedAtUnixMs + 60_000,
            UniqueVoteCount = 10,
            Options =
            [
                new BridgePollOption("o1", "T4 17h30", 6, ["u1", "u3"]),
                new BridgePollOption("o2", "T6 17h30", 9, ["u2", "u4"])
            ]
        };

        Assert.Equal(
            ZaloPollScheduleParser.ComputeStructureHash(before),
            ZaloPollScheduleParser.ComputeStructureHash(after));
    }

    [Fact]
    public void StructureHash_ChangesWhenScheduleOptionChanges()
    {
        var before = BuildPoll(
            "Kèo tuần này",
            new BridgePollOption("o1", "T4 17h30", 5, []));
        var after = before with
        {
            Options = [new BridgePollOption("o1", "T4 18h", 5, [])]
        };

        Assert.NotEqual(
            ZaloPollScheduleParser.ComputeStructureHash(before),
            ZaloPollScheduleParser.ComputeStructureHash(after));
    }

    [Fact]
    public void SelectFromApproval_CanSelectSubsetAndOverrideTime()
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "T4 17h30", 5, []),
            new BridgePollOption("o2", "T6 17h30", 5, []),
            new BridgePollOption("o3", "CN 17h30", 5, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            CurrentVietnamTime);

        var selected = ZaloPollScheduleParser.SelectFromApproval("chỉ T4 đổi 18h và CN", candidates);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, item => item.DayKey == "T4" && item.StartTime.ToOffset(TimeSpan.FromHours(7)).Hour == 18);
        Assert.Contains(selected, item => item.DayKey == "CN");
        Assert.DoesNotContain(selected, item => item.DayKey == "T6");
    }

    [Theory]
    [InlineData("tạo cả 3")]
    [InlineData("xác nhận")]
    [InlineData("chỉ T6 CN")]
    [InlineData("T6 đổi 18h")]
    public void IsApproval_AcceptsExpectedOrganizerReplies(string text)
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "T6 17h30", 5, []),
            new BridgePollOption("o2", "CN 17h30", 5, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            CurrentVietnamTime);

        Assert.True(ZaloPollScheduleParser.IsApproval(text, candidates));
    }

    [Fact]
    public void IsApproval_DoesNotTreatDayMentionAsConfirmationWithoutActionSignal()
    {
        var poll = BuildPoll(
            "Bóng tuần này",
            new BridgePollOption("o1", "T6 17h30", 5, []),
            new BridgePollOption("o2", "CN 17h30", 5, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            CurrentVietnamTime);

        Assert.False(ZaloPollScheduleParser.IsApproval("T6 đông quá", candidates));
    }

    [Theory]
    [InlineData("bỏ qua")]
    [InlineData("không tạo")]
    [InlineData("hủy")]
    public void IsRejection_AcceptsExpectedOrganizerReplies(string text)
    {
        Assert.True(ZaloPollScheduleParser.IsRejection(text));
    }

    private static BridgePoll BuildPoll(string question, params BridgePollOption[] options) => new(
        "poll-1",
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
