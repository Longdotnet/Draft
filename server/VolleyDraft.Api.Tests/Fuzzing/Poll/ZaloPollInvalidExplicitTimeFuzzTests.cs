using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloPollInvalidExplicitTimeFuzzTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public async Task Invalid_explicit_hours_fail_closed_across_option_and_poll_default_time_mutations()
    {
        for (var seed = 1; seed <= 192; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var hour = 24 + random.NextInt(6);
            var minute = random.NextInt(60);
            var useQuestion = random.NextBool();
            var separator = random.NextBool() ? ":" : "h";
            var spacing = random.NextBool() ? "" : " ";
            var time = separator == ":"
                ? $"{hour}{spacing}:{spacing}{minute:00}"
                : $"{hour}{spacing}h{spacing}{minute:00}";
            var action = new InvalidTimeAction(useQuestion, time);
            var scenario = new StatefulFuzzCase<InvalidTimeAction>(
                $"invalid-explicit-time-{seed}",
                seed,
                [action]);

            var result = await StatefulFuzzRunner.RunAsync(scenario, new InvalidTimeTarget());

            Assert.False(
                result.Failed,
                $"Invalid explicit time was accepted. seed={seed}; source={(useQuestion ? "question" : "option")}; time={time}; fingerprint={result.FailureFingerprint}");
        }
    }

    [Fact]
    public void Minimized_invalid_boundary_24_00_is_a_permanent_fail_closed_regression()
    {
        var extraction = Extract("Vote sân UTE tuần sau. Max 18 slots/sân. 17:45", "CN 13/09/2026 24:00");

        Assert.Empty(extraction.Candidates);
        Assert.Contains(extraction.Issues, issue => issue.Code == "invalid_explicit_time");
    }

    [Fact]
    public void Valid_boundary_23_59_remains_authoritative()
    {
        var extraction = Extract("Vote sân UTE tuần sau. Max 18 slots/sân. 17:45", "CN 13/09/2026 23:59");

        var candidate = Assert.Single(extraction.Candidates);
        Assert.Empty(extraction.Issues);
        Assert.Equal(23, candidate.StartTime.ToOffset(VietnamOffset).Hour);
        Assert.Equal(59, candidate.StartTime.ToOffset(VietnamOffset).Minute);
    }

    [Fact]
    public void Invalid_approval_time_cannot_silently_clamp_an_existing_candidate()
    {
        var original = new ZaloAutoSessionCandidate(
            "o1",
            "CN 13/09/2026 17:45",
            "CN",
            new DateTimeOffset(2026, 9, 13, 17, 45, 0, VietnamOffset),
            2);

        var selected = ZaloPollScheduleParser.SelectFromApproval("CN 29:59", [original]);

        var candidate = Assert.Single(selected);
        Assert.Equal(original.StartTime, candidate.StartTime);
    }

    private static ZaloPollScheduleExtraction Extract(string question, string option)
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var poll = new BridgePoll(
            "poll-fuzz-invalid-time",
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

    private sealed record InvalidTimeAction(bool UseQuestion, string TimeText);

    private sealed class InvalidTimeState
    {
        public ZaloPollScheduleExtraction? Extraction { get; set; }
    }

    private sealed class InvalidTimeTarget : IStatefulFuzzTarget<InvalidTimeState, InvalidTimeAction>
    {
        public string Name => "poll-invalid-explicit-time";

        public InvalidTimeState CreateState(StatefulFuzzCase<InvalidTimeAction> scenario) => new();

        public ValueTask ApplyAsync(
            InvalidTimeState state,
            InvalidTimeAction action,
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

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(InvalidTimeState state)
        {
            if (state.Extraction is null)
                yield break;

            var hasInvalidTimeIssue = state.Extraction.Issues.Any(issue =>
                string.Equals(issue.Code, "invalid_explicit_time", StringComparison.Ordinal));
            if (state.Extraction.Candidates.Count > 0 || !hasInvalidTimeIssue)
            {
                yield return new StatefulInvariantViolation(
                    "poll-schedule",
                    "invalid-explicit-time-accepted",
                    "An explicit hour outside 00..23 must fail closed instead of being normalized into another authoritative schedule.");
            }
        }
    }
}
