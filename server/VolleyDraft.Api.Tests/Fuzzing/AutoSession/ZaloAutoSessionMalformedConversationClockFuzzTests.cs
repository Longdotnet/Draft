using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionMalformedConversationClockFuzzTests
{
    private const string Fingerprint = "auto-session:malformed-conversation-clock-must-fail-closed";
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly ZaloAutoSessionConversationDraft Draft = new(
        [
            new("o1", "T6 18/9", "T6", new DateTimeOffset(2026, 9, 18, 17, 45, 0, VietnamOffset), 8, true),
            new("o2", "CN 20/9", "CN", new DateTimeOffset(2026, 9, 20, 17, 45, 0, VietnamOffset), 7, true)
        ],
        "UTE",
        6);

    [Theory]
    [InlineData("T6 29:30")]
    [InlineData("T6 17:600")]
    [InlineData("CN 24h00")]
    [InlineData("CN 18h990")]
    public async Task Minimized_malformed_day_clock_reproducer_fails_closed(string text)
    {
        var scenario = new StatefulFuzzCase<ConversationClockAction>(
            "auto-session-malformed-conversation-clock-minimized",
            20260913,
            [new(text)]);
        var result = await StatefulFuzzRunner.RunAsync(scenario, new MalformedConversationClockTarget());

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Stateful_malformed_clock_corpus_never_turns_invalid_time_into_plausible_draft_mutation()
    {
        for (var seed = 1; seed <= 192; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var day = random.NextBool() ? "T6" : "CN";
            var separator = random.NextBool() ? ":" : "h";
            string clock;
            if (random.NextBool())
            {
                var invalidHour = 24 + random.NextInt(76);
                clock = $"{invalidHour}{separator}{random.NextInt(60):00}";
            }
            else
            {
                var validHour = random.NextInt(24);
                var oversizedMinute = 60 + random.NextInt(940);
                clock = $"{validHour}{separator}{oversizedMinute}";
            }

            var text = random.NextInt(4) switch
            {
                0 => $"{day} {clock}",
                1 => $"đổi {day} {clock}",
                2 => $"{day} lúc {clock}",
                _ => $"chốt {day} {clock} nha"
            };
            var scenario = new StatefulFuzzCase<ConversationClockAction>(
                $"auto-session-malformed-conversation-clock-{seed}",
                seed,
                [new(text)]);
            var result = await StatefulFuzzRunner.RunAsync(scenario, new MalformedConversationClockTarget());

            Assert.False(result.Failed, Describe(result));
        }
    }

    [Theory]
    [InlineData("T6 23:59", "T6", 23 * 60 + 59)]
    [InlineData("CN 17h45", "CN", 17 * 60 + 45)]
    public void Valid_boundary_clocks_remain_deterministic(string text, string day, int expectedMinutes)
    {
        var interpretation = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            text,
            Draft,
            ZaloAutoSessionConversationState.Discussing,
            null);

        Assert.False(interpretation.NeedsClarification);
        Assert.Equal(ZaloAutoSessionConversationIntent.ModifyDraft, interpretation.Intent);
        Assert.True(interpretation.TimeOverrides.TryGetValue(day, out var minutes));
        Assert.Equal(expectedMinutes, minutes);
    }

    private static string Describe(StatefulFuzzRunResult<ConversationClockAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; text={result.Scenario.Actions.FirstOrDefault()?.Text ?? "none"}; violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal sealed record ConversationClockAction(string Text);

    internal sealed class ConversationClockState
    {
        public ZaloAutoSessionConversationInterpretation? LastInterpretation { get; set; }
        public string? LastText { get; set; }
    }

    internal sealed class MalformedConversationClockTarget : IStatefulFuzzTarget<ConversationClockState, ConversationClockAction>
    {
        public string Name => "auto-session-malformed-conversation-clock";

        public ConversationClockState CreateState(StatefulFuzzCase<ConversationClockAction> scenario) => new();

        public ValueTask ApplyAsync(
            ConversationClockState state,
            ConversationClockAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.LastText = action.Text;
            state.LastInterpretation = ZaloAutoSessionConversationInterpreter.InterpretByRules(
                action.Text,
                Draft,
                ZaloAutoSessionConversationState.Discussing,
                null);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ConversationClockState state)
        {
            var interpretation = state.LastInterpretation;
            if (interpretation is null)
                yield break;

            var mutatedTime = interpretation.TimeOverrides.Count > 0;
            var authoritativeMutation = interpretation.Intent == ZaloAutoSessionConversationIntent.ModifyDraft &&
                                        !interpretation.NeedsClarification;
            if (mutatedTime || authoritativeMutation)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "malformed-conversation-clock-must-fail-closed",
                    $"Malformed clock '{state.LastText}' produced intent={interpretation.Intent}, clarification={interpretation.NeedsClarification}, overrides={string.Join(',', interpretation.TimeOverrides.Select(item => $"{item.Key}:{item.Value}"))}.");
            }
        }
    }
}
