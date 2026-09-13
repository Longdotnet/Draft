using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloPollMalformedClockFuzzTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public async Task Oversized_numeric_clock_tokens_fail_closed_in_question_and_options()
    {
        for (var seed = 1; seed <= 192; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var useQuestion = random.NextBool();
            var separator = random.NextBool() ? ":" : "h";
            var oversizedHour = random.NextBool();
            var hourDigits = oversizedHour
                ? (100 + random.NextInt(900)).ToString()
                : (17 + random.NextInt(7)).ToString();
            var minuteDigits = oversizedHour
                ? random.NextInt(60).ToString("00")
                : (100 + random.NextInt(900)).ToString();
            var time = $"{hourDigits}{separator}{minuteDigits}";
            var scenario = new StatefulFuzzCase<MalformedClockAction>(
                $"malformed-clock-{seed}",
                seed,
                [new MalformedClockAction(useQuestion, time)]);

            var result = await StatefulFuzzRunner.RunAsync(scenario, new MalformedClockTarget());

            Assert.False(
                result.Failed,
                $"Malformed explicit clock silently fell back. seed={seed}; source={(useQuestion ? "question" : "option")}; time={time}; fingerprint={result.FailureFingerprint}");
        }
    }

    [Theory]
    [InlineData("17:600")]
    [InlineData("17h600")]
    [InlineData("100:00")]
    [InlineData("100h00")]
    public void Minimized_malformed_numeric_clock_is_a_permanent_fail_closed_regression(string time)
    {
        var extraction = Extract(
            "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45",
            $"CN 13/09/2026 {time}");

        Assert.Empty(extraction.Candidates);
        Assert.Contains(extraction.Issues, issue => issue.Code == "invalid_explicit_time");
    }

    [Fact]
    public void Valid_clock_next_to_boundary_remains_authoritative()
    {
        var extraction = Extract(
            "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45",
            "CN 13/09/2026 23:59");

        var candidate = Assert.Single(extraction.Candidates);
        Assert.Empty(extraction.Issues);
        Assert.Equal(23, candidate.StartTime.ToOffset(VietnamOffset).Hour);
        Assert.Equal(59, candidate.StartTime.ToOffset(VietnamOffset).Minute);
    }

    private static ZaloPollScheduleExtraction Extract(string question, string option)
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = new BridgePoll(
            "poll-fuzz-malformed-clock",
            question,
            "leader-1",
            [new BridgePollOption("o1", option, 2, [])],
            true,
            false,
            false,
            false,
            2,
            created.ToUnixTimeMilliseconds(),
            created.ToUnixTimeMilliseconds(),
            0);
        return ZaloPollScheduleParser.ExtractSchedule(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset));
    }

    private sealed record MalformedClockAction(bool UseQuestion, string TimeText);

    private sealed class MalformedClockState
    {
        public ZaloPollScheduleExtraction? Extraction { get; set; }
    }

    private sealed class MalformedClockTarget : IStatefulFuzzTarget<MalformedClockState, MalformedClockAction>
    {
        public string Name => "poll-malformed-clock";

        public MalformedClockState CreateState(StatefulFuzzCase<MalformedClockAction> scenario) => new();

        public ValueTask ApplyAsync(
            MalformedClockState state,
            MalformedClockAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            var question = action.UseQuestion
                ? $"Vote sân UTE tuần sau. Max 18 slots/sân. {action.TimeText}"
                : "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45";
            var option = action.UseQuestion
                ? "CN 13/09/2026"
                : $"CN 13/09/2026 {action.TimeText}";
            state.Extraction = Extract(question, option);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(MalformedClockState state)
        {
            if (state.Extraction is null)
                yield break;

            var hasInvalidTimeIssue = state.Extraction.Issues.Any(issue =>
                string.Equals(issue.Code, "invalid_explicit_time", StringComparison.Ordinal));
            if (state.Extraction.Candidates.Count > 0 || !hasInvalidTimeIssue)
            {
                yield return new StatefulInvariantViolation(
                    "poll-schedule",
                    "malformed-explicit-clock-fallback",
                    "A numeric clock token with oversized hour/minute width must fail closed instead of silently falling back to another authoritative time.");
            }
        }
    }
}
