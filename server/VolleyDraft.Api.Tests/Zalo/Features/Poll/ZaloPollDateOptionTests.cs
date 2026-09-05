using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPollDateOptionTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public void Date_only_options_inherit_start_time_from_poll_question()
    {
        var created = new DateTimeOffset(2026, 8, 23, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            new BridgePollOption("o1", "25/8", 8, []),
            new BridgePollOption("o2", "27/8", 7, []));
        var tracked = new ZaloTrackedGroupData
        {
            DefaultStartMinutes = 18 * 60,
            AssumePmForHourUnder12 = true
        };

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            tracked,
            new DateTimeOffset(2026, 8, 24, 8, 0, 0, VietnamOffset));

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            var local = candidate.StartTime.ToOffset(VietnamOffset);
            Assert.Equal(17, local.Hour);
            Assert.Equal(30, local.Minute);
        });
        Assert.Equal(25, candidates[0].StartTime.ToOffset(VietnamOffset).Day);
        Assert.Equal(27, candidates[1].StartTime.ToOffset(VietnamOffset).Day);
    }

    [Fact]
    public void Production_poll_explicit_dates_override_weekday_inference()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. Max 18 slots/sân. 28k/slot. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật 13/9", 2, []),
            new BridgePollOption("o2", "Thứ 4 9/9", 0, []),
            new BridgePollOption("o3", "Thứ 3 8/9", 0, []),
            new BridgePollOption("o4", "Thứ 6 11/9", 0, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset));

        Assert.Collection(
            candidates,
            candidate => AssertCandidate(candidate, "T3", 2026, 9, 8, 17, 45),
            candidate => AssertCandidate(candidate, "T4", 2026, 9, 9, 17, 45),
            candidate => AssertCandidate(candidate, "T6", 2026, 9, 11, 17, 45),
            candidate => AssertCandidate(candidate, "CN", 2026, 9, 13, 17, 45));
    }

    [Theory]
    [InlineData("CN 13/9")]
    [InlineData("13/9 Chủ nhật")]
    [InlineData("Chủ nhật 13/09")]
    public void Explicit_date_is_authoritative_regardless_of_weekday_token_order(string optionText)
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", optionText, 2, []));

        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset)));

        AssertCandidate(candidate, "CN", 2026, 9, 13, 17, 45);
    }

    [Fact]
    public void Conflicting_weekday_and_explicit_date_fails_closed()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật 12/9", 2, []));

        var extraction = ZaloPollScheduleParser.ExtractSchedule(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset));

        Assert.Empty(extraction.Candidates);
        var issue = Assert.Single(extraction.Issues);
        Assert.Equal("weekday_date_conflict", issue.Code);
        Assert.Equal("o1", issue.OptionId);
        Assert.Contains("12/09", issue.Message, StringComparison.Ordinal);
        Assert.Contains("T7", issue.Message, StringComparison.Ordinal);
        Assert.Contains("CN 13/09", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Weekday_only_option_uses_next_week_scope_from_poll_title()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật", 2, []));

        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset)));

        AssertCandidate(candidate, "CN", 2026, 9, 13, 17, 45);
    }

    [Fact]
    public void Create_boundary_rejects_stale_plan_when_explicit_source_date_does_not_match_resolved_date()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật 13/9", 2, []));
        var staleCandidate = new ZaloAutoSessionCandidate(
            "o1",
            "Chủ nhật 13/9",
            "CN",
            new DateTimeOffset(2026, 9, 6, 17, 45, 0, VietnamOffset),
            2);

        Assert.False(ZaloPollScheduleParser.ValidateCandidateConsistency(poll, staleCandidate, out var reason));
        Assert.Equal("explicit_date_mismatch", reason);
    }

    [Fact]
    public void Create_boundary_accepts_the_exact_resolved_preview_plan()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật 13/9", 2, []));
        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset)));

        Assert.True(ZaloPollScheduleParser.ValidateCandidateConsistency(poll, candidate, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Poll_fingerprint_changes_when_schedule_option_changes_after_preview()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var previewed = BuildPoll(
            created,
            "Vote sân UTE tuần sau. 17:45-22:00",
            new BridgePollOption("o1", "Chủ nhật 13/9", 2, []));
        var changed = previewed with
        {
            Options = [new BridgePollOption("o1", "Chủ nhật 20/9", 2, [])],
            UpdatedAtUnixMs = previewed.UpdatedAtUnixMs + 60_000
        };

        Assert.NotEqual(
            ZaloPollScheduleParser.ComputeStructureHash(previewed),
            ZaloPollScheduleParser.ComputeStructureHash(changed));
    }

    [Fact]
    public void Yearless_date_option_after_new_year_rolls_forward_instead_of_becoming_stale()
    {
        var created = new DateTimeOffset(2026, 12, 31, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(created, new BridgePollOption("o1", "2/1", 8, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 12, 31, 21, 0, 0, VietnamOffset));

        var candidate = Assert.Single(candidates);
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 17, 30, 0, VietnamOffset), candidate.StartTime);
    }

    [Fact]
    public void Yearless_leap_day_option_resolves_to_the_next_valid_occurrence()
    {
        var created = new DateTimeOffset(2027, 3, 1, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(created, new BridgePollOption("o1", "29/2", 8, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2027, 3, 1, 21, 0, 0, VietnamOffset));

        var candidate = Assert.Single(candidates);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 17, 30, 0, VietnamOffset), candidate.StartTime);
    }

    [Fact]
    public async Task Date_only_multi_choice_vote_san_poll_passes_deterministic_classifier()
    {
        var created = new DateTimeOffset(2026, 8, 23, 20, 0, 0, VietnamOffset);
        var poll = BuildPoll(
            created,
            new BridgePollOption("o1", "25/8", 8, []),
            new BridgePollOption("o2", "27/8", 7, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 8, 24, 8, 0, 0, VietnamOffset));
        var classifier = new ZaloPollClassifierService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloPollClassifierService>.Instance);

        var result = await classifier.ClassifyAsync(poll, candidates);

        Assert.True(result.IsVolleyballSignupPoll);
        Assert.True(result.Confidence >= .72);
        Assert.Contains("date_pattern", result.Reason, StringComparison.Ordinal);
    }

    private static void AssertCandidate(
        ZaloAutoSessionCandidate candidate,
        string dayKey,
        int year,
        int month,
        int day,
        int hour,
        int minute)
    {
        Assert.Equal(dayKey, candidate.DayKey);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, 0, VietnamOffset), candidate.StartTime);
    }

    private static BridgePoll BuildPoll(DateTimeOffset created, params BridgePollOption[] options) =>
        BuildPoll(created, "Vote sân ute tuần sau. 17h30-22h", options);

    private static BridgePoll BuildPoll(DateTimeOffset created, string question, params BridgePollOption[] options) => new(
        "poll-ute-next-week",
        question,
        "leader-1",
        options,
        true,
        false,
        false,
        false,
        options.Sum(option => option.VoteCount),
        created.ToUnixTimeMilliseconds(),
        created.ToUnixTimeMilliseconds(),
        0);
}
