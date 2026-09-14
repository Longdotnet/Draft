using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceNegationLanguageFuzzTests
{
    private static readonly ZaloMentionedUser[] ExplicitMentions =
    [
        new("uid-long", "Thanh Long"),
        new("uid-an", "To An"),
        new("uid-nguyen", "Đặng Thế Nguyễn")
    ];

    [Theory]
    [InlineData("tui k muốn chung team với @Thanh Long")]
    [InlineData("tui ko muốn chung team với @To An")]
    [InlineData("tui không muốn chung team với @To An")]
    [InlineData("tui không muốn chơi chung team với @Thanh Long")]
    [InlineData("đừng xếp tui chung team với @To An")]
    [InlineData("khỏi xếp tui chung đội với @Thanh Long")]
    public void Permanent_reproducer_negated_same_team_is_owned_but_not_executable(string question)
    {
        Assert.True(ZaloNaturalCommandParser.IsNegatedTeamPreference(question));
        Assert.True(ZaloNaturalCommandParser.TryParseTeamPreference(question, out var parsed));
        Assert.True(parsed.PlayerReferences.Count < 2);

        var bound = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(ExplicitMentions, parsed);

        Assert.NotNull(bound);
        Assert.True(bound!.PlayerReferences.Count < 2);
    }

    [Theory]
    [InlineData("tui muốn chung team với @To An")]
    [InlineData("tui muốn chơi chung team với @To An hôm nay")]
    [InlineData("To An muốn cùng team với Anh Duy thứ 6")]
    [InlineData("To An và Anh Duy chơi chung đội thứ 6")]
    [InlineData("xếp To An với Anh Duy chung team thứ 6")]
    public void Existing_positive_same_team_language_remains_executable(string question)
    {
        Assert.False(ZaloNaturalCommandParser.IsNegatedTeamPreference(question));
        Assert.True(ZaloNaturalCommandParser.TryParseTeamPreference(question, out var parsed));
        Assert.True(parsed.PlayerReferences.Count >= 2);
    }

    [Fact]
    public async Task Language_and_mention_mutations_never_promote_negation_into_positive_mutation()
    {
        var negativeTemplates = new[]
        {
            "tui {0} muốn chung team với @To An",
            "tui {0} muốn chơi chung team với @Thanh Long",
            "TUI {0} MUỐN CHUNG TEAM VỚI @To An",
            "  tui   {0}   muốn   chung team   với @To An  "
        };
        var negations = new[] { "không", "ko", "k", "hong" };
        var actions = new List<LanguageAction>();
        var seed = 20260914;
        var random = new StableFuzzRandom(seed);

        for (var index = 0; index < 128; index += 1)
        {
            var template = negativeTemplates[random.NextInt(negativeTemplates.Length)];
            var negation = negations[random.NextInt(negations.Length)];
            var mentionCount = 1 + random.NextInt(3);
            actions.Add(new LanguageAction(string.Format(template, negation), mentionCount, IsNegated: true));

            if (index % 8 == 0)
            {
                actions.Add(new LanguageAction(
                    random.NextBool()
                        ? "tui muốn chung team với @To An"
                        : "xếp To An với Anh Duy chung team thứ 6",
                    1 + random.NextInt(3),
                    IsNegated: false));
            }
        }

        var scenario = new StatefulFuzzCase<LanguageAction>(
            "team-preference-negation-language-corpus",
            seed,
            actions);
        var result = await StatefulFuzzRunner.RunAsync(scenario, new TeamPreferenceNegationTarget());

        Assert.False(result.Failed, Describe(result));
    }

    private static string Describe(StatefulFuzzRunResult<LanguageAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal sealed record LanguageAction(string Text, int MentionCount, bool IsNegated);

    internal sealed class LanguageState
    {
        public LanguageAction? LastAction { get; set; }
        public bool Parsed { get; set; }
        public int PlayerReferenceCount { get; set; }
    }

    internal sealed class TeamPreferenceNegationTarget : IStatefulFuzzTarget<LanguageState, LanguageAction>
    {
        public string Name => "team-preference-negation-language";

        public LanguageState CreateState(StatefulFuzzCase<LanguageAction> scenario) => new();

        public ValueTask ApplyAsync(
            LanguageState state,
            LanguageAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.LastAction = action;
            state.Parsed = ZaloNaturalCommandParser.TryParseTeamPreference(action.Text, out var parsed);
            if (!state.Parsed)
            {
                state.PlayerReferenceCount = 0;
                return ValueTask.CompletedTask;
            }

            var mentions = ExplicitMentions.Take(action.MentionCount).ToArray();
            var bound = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(mentions, parsed);
            state.PlayerReferenceCount = bound?.PlayerReferences.Count ?? 0;
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(LanguageState state)
        {
            if (state.LastAction is null)
                yield break;

            if (state.LastAction.IsNegated &&
                (!state.Parsed || state.PlayerReferenceCount >= 2))
            {
                yield return new StatefulInvariantViolation(
                    "conversation-ownership",
                    "negated-team-preference-promoted-to-positive",
                    $"Negated same-team language became an executable positive command: {state.LastAction.Text}",
                    "conversation-ownership:team-preference-negation-promoted-to-positive");
            }

            if (!state.LastAction.IsNegated &&
                (!state.Parsed || state.PlayerReferenceCount < 2))
            {
                yield return new StatefulInvariantViolation(
                    "conversation-ownership",
                    "positive-team-preference-regressed",
                    $"Existing positive same-team language stopped producing an executable command: {state.LastAction.Text}",
                    "conversation-ownership:team-preference-positive-regression");
            }
        }
    }
}
