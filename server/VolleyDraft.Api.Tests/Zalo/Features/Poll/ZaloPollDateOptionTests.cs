using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPollDateOptionTests
{
    [Fact]
    public void Explicit_date_is_authoritative_when_option_also_contains_weekday()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(
            created,
            new BridgePollOption("sun", "Chủ nhật 13/9", 2, []),
            new BridgePollOption("tue", "Thứ 3 8/9", 5, []),
            new BridgePollOption("wed", "Thứ 4 9/9", 6, []),
            new BridgePollOption("fri", "Thứ 6 11/9", 8, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7)));

        Assert.Collection(
            candidates,
            candidate => Assert.Equal(new DateTimeOffset(2026, 9, 8, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime),
            candidate => Assert.Equal(new DateTimeOffset(2026, 9, 9, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime),
            candidate => Assert.Equal(new DateTimeOffset(2026, 9, 11, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime),
            candidate => Assert.Equal(new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime));
    }

    [Theory]
    [InlineData("CN 13/9")]
    [InlineData("13/9 Chủ nhật")]
    [InlineData("Chủ nhật 13/09")]
    public void Explicit_date_precedence_handles_common_vietnamese_variants(string option)
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", option, 2, []));

        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7))));

        Assert.Equal(new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime);
    }

    [Fact]
    public void Conflicting_weekday_does_not_override_explicit_date_and_mutation_fails_closed()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "Chủ nhật 12/9", 2, []));

        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7))));

        Assert.Equal("T7", candidate.DayKey);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime);
        Assert.False(ZaloPollScheduleParser.TryValidateCandidateForMutation(poll, candidate, out var error));
        Assert.Contains("T7", error, StringComparison.Ordinal);
        Assert.Contains("12/09", error, StringComparison.Ordinal);
        Assert.Contains("CN", error, StringComparison.Ordinal);
        Assert.Contains("13/09", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Mutation_guard_rejects_stale_bad_preview_when_raw_option_has_explicit_date()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "Chủ nhật 13/9", 2, []));
        var staleBadCandidate = new ZaloAutoSessionCandidate(
            "o1",
            "Chủ nhật 13/9",
            "CN",
            new DateTimeOffset(2026, 9, 6, 17, 45, 0, TimeSpan.FromHours(7)),
            2);

        Assert.False(ZaloPollScheduleParser.TryValidateCandidateForMutation(poll, staleBadCandidate, out var error));
        Assert.Contains("13/09/2026", error, StringComparison.Ordinal);
        Assert.Contains("06/09/2026", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Mutation_guard_accepts_resolved_candidate_that_matches_explicit_source_date()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "Chủ nhật 13/9", 2, []));
        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7))));

        Assert.True(ZaloPollScheduleParser.TryValidateCandidateForMutation(poll, candidate, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Weekday_only_option_uses_next_week_scope_from_poll_title()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "Chủ nhật", 2, []));

        var candidate = Assert.Single(ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7))));

        Assert.Equal(new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7)), candidate.StartTime);
    }

    [Fact]
    public void Date_only_options_inherit_start_time_from_poll_question()
    {
        var created = new DateTimeOffset(2026, 8, 23, 20, 0, 0, TimeSpan.FromHours(7));
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
            new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            var local = candidate.StartTime.ToOffset(TimeSpan.FromHours(7));
            Assert.Equal(17, local.Hour);
            Assert.Equal(30, local.Minute);
        });
        Assert.Equal(25, candidates[0].StartTime.ToOffset(TimeSpan.FromHours(7)).Day);
        Assert.Equal(27, candidates[1].StartTime.ToOffset(TimeSpan.FromHours(7)).Day);
    }

    [Fact]
    public void Yearless_date_option_after_new_year_rolls_forward_instead_of_becoming_stale()
    {
        var created = new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "2/1", 8, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.FromHours(7)));

        var candidate = Assert.Single(candidates);
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 17, 30, 0, TimeSpan.FromHours(7)), candidate.StartTime);
    }

    [Fact]
    public void Yearless_leap_day_option_resolves_to_the_next_valid_occurrence()
    {
        var created = new DateTimeOffset(2027, 3, 1, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(created, new BridgePollOption("o1", "29/2", 8, []));

        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2027, 3, 1, 21, 0, 0, TimeSpan.FromHours(7)));

        var candidate = Assert.Single(candidates);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 17, 30, 0, TimeSpan.FromHours(7)), candidate.StartTime);
    }

    [Fact]
    public async Task Date_only_multi_choice_vote_san_poll_passes_deterministic_classifier()
    {
        var created = new DateTimeOffset(2026, 8, 23, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = BuildPoll(
            created,
            new BridgePollOption("o1", "25/8", 8, []),
            new BridgePollOption("o2", "27/8", 7, []));
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.FromHours(7)));
        var classifier = new ZaloPollClassifierService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloPollClassifierService>.Instance);

        var result = await classifier.ClassifyAsync(poll, candidates);

        Assert.True(result.IsVolleyballSignupPoll);
        Assert.True(result.Confidence >= .72);
        Assert.Contains("date_pattern", result.Reason, StringComparison.Ordinal);
    }

    private static BridgePoll BuildPoll(DateTimeOffset created, params BridgePollOption[] options) => new(
        "poll-ute-next-week",
        "Vote sân UTE tuần sau. Max 18 slots/sân. 28k/slot. 17:45-22:00",
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