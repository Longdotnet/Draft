using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloPollClassifierAiOptionalityFuzzTests
{
    [Fact]
    public async Task Ai_outcomes_cannot_change_deterministic_mutation_authority()
    {
        for (var seed = 1; seed <= 256; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var scoreBasisPoints = random.NextInt(10_001);
            var ruleScore = scoreBasisPoints / 10_000d;
            var scenario = new StatefulFuzzCase<AiClassifierAction>(
                $"poll-classifier-ai-optionality-{seed}",
                seed,
                StatefulSequenceMutator.Mutate(
                    SeedActions,
                    mutationSeed: seed,
                    createAction: CreateAction,
                    operationCount: 12));

            var target = new AiClassifierAuthorityTarget(ruleScore);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(
                result.Failed,
                $"Seed={seed}; RuleScore={ruleScore:F4}; Fingerprint={result.FailureFingerprint}; " +
                $"FailureAction={result.FailureActionIndex}; " +
                $"Exception={result.Exception}; Violation={result.Violation?.Message}");
        }
    }

    [Theory]
    [InlineData(0.7199, false)]
    [InlineData(0.72, true)]
    [InlineData(0.7201, true)]
    public async Task Authority_threshold_remains_stable_under_adversarial_ai_sequences(
        double ruleScore,
        bool expectedAuthority)
    {
        var scenario = new StatefulFuzzCase<AiClassifierAction>(
            $"poll-classifier-threshold-{ruleScore}",
            20260909,
            [
                new(true, 1, "strong_positive"),
                new(false, 1, "strong_negative"),
                new(false, 0.849999, "negative_below_rejection_threshold"),
                new(true, 0, "zero_confidence_positive"),
                new(false, 0, "zero_confidence_negative")
            ]);

        var result = await StatefulFuzzRunner.RunAsync(
            scenario,
            new AiClassifierAuthorityTarget(ruleScore));

        Assert.False(result.Failed, result.Violation?.Message ?? result.Exception?.ToString());
        Assert.Equal(expectedAuthority, ruleScore >= 0.72);
    }

    private static readonly AiClassifierAction[] SeedActions =
    [
        new(true, 0.99, "ai_positive"),
        new(false, 0.99, "ai_reject"),
        new(false, 0.85, "ai_reject_boundary"),
        new(false, 0.849999, "ai_negative_low_confidence"),
        new(true, 0.01, "ai_positive_low_confidence")
    ];

    private static AiClassifierAction CreateAction(StableFuzzRandom random) => new(
        IsSignup: random.NextBool(),
        Confidence: random.NextInt(10_001) / 10_000d,
        Reason: $"fuzz-{random.NextUInt32():x8}");

    private sealed record AiClassifierAction(
        bool IsSignup,
        double Confidence,
        string Reason);

    private sealed class AiClassifierAuthorityState(double ruleScore)
    {
        public double RuleScore { get; } = ruleScore;
        public bool ExpectedAuthority { get; } = ruleScore >= 0.72;
        public ZaloPollClassification? LastClassification { get; set; }
    }

    private sealed class AiClassifierAuthorityTarget(double ruleScore)
        : IStatefulFuzzTarget<AiClassifierAuthorityState, AiClassifierAction>
    {
        public string Name => "zalo-poll-classifier-ai-optionality";

        public AiClassifierAuthorityState CreateState(StatefulFuzzCase<AiClassifierAction> scenario) =>
            new(ruleScore);

        public ValueTask ApplyAsync(
            AiClassifierAuthorityState state,
            AiClassifierAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.LastClassification = ZaloPollClassifierService.ResolveWithAi(
                state.RuleScore,
                "fuzz_rule_evidence",
                action.IsSignup,
                action.Confidence,
                action.Reason);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(AiClassifierAuthorityState state)
        {
            var classification = state.LastClassification;
            if (classification is null) yield break;

            if (classification.IsVolleyballSignupPoll != state.ExpectedAuthority)
            {
                yield return Violation(
                    "authority-bit-parity",
                    $"AI changed mutation authority: expected={state.ExpectedAuthority}, actual={classification.IsVolleyballSignupPoll}.");
            }

            if (classification.CanAutoExecute(requireOrganizerApproval: false) != state.ExpectedAuthority)
            {
                yield return Violation(
                    "auto-execute-parity",
                    $"AI changed no-approval execution authority: expected={state.ExpectedAuthority}, actual={classification.CanAutoExecute(false)}.");
            }

            if (classification.CanAutoExecute(requireOrganizerApproval: true))
            {
                yield return Violation(
                    "approval-bypass",
                    "AI/classifier output allowed auto execution while organizer approval is required.");
            }

            if (!state.ExpectedAuthority && classification.SemanticCandidate && classification.CanAutoExecute(false))
            {
                yield return Violation(
                    "semantic-candidate-authority-escalation",
                    "A semantic-only AI candidate escalated into mutation authority.");
            }
        }

        private static StatefulInvariantViolation Violation(string id, string message) =>
            new(
                Domain: "auto-session-ai-optionality",
                Id: id,
                Message: message,
                Fingerprint: $"auto-session-ai-optionality:{id}");
    }
}
