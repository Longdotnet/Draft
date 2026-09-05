using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPollDateOptionTests
{
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
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 18, 0, 0, TimeSpan.FromHours(7)), candidate.StartTime);
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
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 18, 0, 0, TimeSpan.FromHours(7)), candidate.StartTime);
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
        "Vote sân ute tuần sau. 17h30-22h",
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
