using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloPollClassifierAiFailureSurfaceFuzzTests
{
    [Fact]
    public async Task Provider_failures_malformed_output_and_contradictions_preserve_deterministic_authority()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var strong = random.NextBool();
            var fixture = BuildFixture(strong);
            var baseline = await ClassifyWithoutAiAsync(fixture.Poll, fixture.Candidates);
            var handler = new MutableAiHandler();
            var classifier = CreateConfiguredClassifier(handler);
            var scenario = new StatefulFuzzCase<AiSurfaceAction>(
                $"poll-classifier-ai-failure-surface-{seed}",
                seed,
                StatefulSequenceMutator.Mutate(
                    SeedActions,
                    mutationSeed: seed,
                    createAction: CreateAction,
                    operationCount: 14));

            var result = await StatefulFuzzRunner.RunAsync(
                scenario,
                new AiFailureSurfaceTarget(
                    classifier,
                    handler,
                    fixture.Poll,
                    fixture.Candidates,
                    baseline));

            Assert.False(
                result.Failed,
                $"Seed={seed}; strong={strong}; baselineAuthority={baseline.IsVolleyballSignupPoll}; " +
                $"Fingerprint={result.FailureFingerprint}; action={result.FailureActionIndex}; " +
                $"Exception={result.Exception}; violation={result.Violation?.Message}");
        }
    }

    private static readonly AiSurfaceAction[] SeedActions =
    [
        new(AiSurfaceMode.RateLimited, false, 0.99),
        new(AiSurfaceMode.ProviderUnavailable, false, 0.99),
        new(AiSurfaceMode.Timeout, false, 0.99),
        new(AiSurfaceMode.MalformedEnvelope, false, 0.99),
        new(AiSurfaceMode.EmptyCompletion, false, 0.99),
        new(AiSurfaceMode.MalformedClassifierJson, false, 0.99),
        new(AiSurfaceMode.ValidClassifier, true, 0.99),
        new(AiSurfaceMode.ValidClassifier, false, 0.99),
        new(AiSurfaceMode.ValidClassifier, true, 0.01),
        new(AiSurfaceMode.ValidClassifier, false, 0.01)
    ];

    private static AiSurfaceAction CreateAction(StableFuzzRandom random)
    {
        var modes = Enum.GetValues<AiSurfaceMode>();
        return new AiSurfaceAction(
            modes[random.NextInt(modes.Length)],
            random.NextBool(),
            random.NextInt(10_001) / 10_000d);
    }

    private static async Task<ZaloPollClassification> ClassifyWithoutAiAsync(
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates)
    {
        var classifier = new ZaloPollClassifierService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloPollClassifierService>.Instance);
        return await classifier.ClassifyAsync(poll, candidates);
    }

    private static ZaloPollClassifierService CreateConfiguredClassifier(MutableAiHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "fuzz-key",
                ["Ai:Model"] = "fuzz-model",
                ["Ai:RetryCount"] = "0",
                ["Ai:TimeoutSeconds"] = "3"
            })
            .Build();
        return new ZaloPollClassifierService(
            new HttpClient(handler),
            config,
            NullLogger<ZaloPollClassifierService>.Instance);
    }

    private static ClassifierFixture BuildFixture(bool strong)
    {
        var created = new DateTimeOffset(2026, 9, 9, 18, 0, 0, TimeSpan.FromHours(7));
        var options = strong
            ? new[]
            {
                new BridgePollOption("o1", "T6 17h30", 8, []),
                new BridgePollOption("o2", "CN 17h30", 7, [])
            }
            : new[] { new BridgePollOption("o1", "T6 17h30", 1, []) };
        var poll = new BridgePoll(
            "fuzz-ai-surface",
            strong ? "Vote kèo bóng chuyền tuần này" : "Khảo sát lịch tuần này",
            "leader",
            options,
            strong,
            false,
            false,
            false,
            options.Sum(item => item.VoteCount),
            created.ToUnixTimeMilliseconds(),
            created.ToUnixTimeMilliseconds(),
            0);
        var candidates = options.Select((option, index) => new ZaloAutoSessionCandidate(
            option.Id,
            option.Content,
            index == 0 ? "T6" : "CN",
            created.AddDays(index + 2),
            option.VoteCount)).ToList();
        return new ClassifierFixture(poll, candidates);
    }

    private sealed record ClassifierFixture(
        BridgePoll Poll,
        IReadOnlyList<ZaloAutoSessionCandidate> Candidates);

    private enum AiSurfaceMode
    {
        RateLimited,
        ProviderUnavailable,
        Timeout,
        MalformedEnvelope,
        EmptyCompletion,
        MalformedClassifierJson,
        ValidClassifier
    }

    private sealed record AiSurfaceAction(AiSurfaceMode Mode, bool IsSignup, double Confidence);

    private sealed class AiFailureSurfaceState(
        ZaloPollClassifierService classifier,
        MutableAiHandler handler,
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        ZaloPollClassification baseline)
    {
        public ZaloPollClassifierService Classifier { get; } = classifier;
        public MutableAiHandler Handler { get; } = handler;
        public BridgePoll Poll { get; } = poll;
        public IReadOnlyList<ZaloAutoSessionCandidate> Candidates { get; } = candidates;
        public ZaloPollClassification Baseline { get; } = baseline;
        public ZaloPollClassification? Last { get; set; }
    }

    private sealed class AiFailureSurfaceTarget(
        ZaloPollClassifierService classifier,
        MutableAiHandler handler,
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        ZaloPollClassification baseline)
        : IStatefulFuzzTarget<AiFailureSurfaceState, AiSurfaceAction>
    {
        public string Name => "zalo-poll-classifier-ai-failure-surface";

        public AiFailureSurfaceState CreateState(StatefulFuzzCase<AiSurfaceAction> scenario) =>
            new(classifier, handler, poll, candidates, baseline);

        public async ValueTask ApplyAsync(
            AiFailureSurfaceState state,
            AiSurfaceAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Handler.Action = action;
            state.Last = await state.Classifier.ClassifyAsync(state.Poll, state.Candidates, cancellationToken);
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(AiFailureSurfaceState state)
        {
            if (state.Last is null) yield break;

            if (state.Last.IsVolleyballSignupPoll != state.Baseline.IsVolleyballSignupPoll)
            {
                yield return Violation(
                    "authority-parity",
                    $"AI surface changed deterministic authority: baseline={state.Baseline.IsVolleyballSignupPoll}, actual={state.Last.IsVolleyballSignupPoll}.");
            }

            if (state.Last.CanAutoExecute(requireOrganizerApproval: false) != state.Baseline.IsVolleyballSignupPoll)
            {
                yield return Violation(
                    "auto-execute-parity",
                    "AI failure/contradiction changed no-approval execution authority.");
            }

            if (state.Last.CanAutoExecute(requireOrganizerApproval: true))
            {
                yield return Violation(
                    "approval-bypass",
                    "Classifier output bypassed organizer approval.");
            }

            if (!double.IsFinite(state.Last.Confidence) || state.Last.Confidence is < 0 or > 1)
            {
                yield return Violation(
                    "confidence-domain",
                    $"Classifier emitted non-serializable/out-of-domain confidence {state.Last.Confidence}.");
            }
        }

        private static StatefulInvariantViolation Violation(string id, string message) =>
            new(
                Domain: "auto-session-ai-optionality",
                Id: id,
                Message: message,
                Fingerprint: $"auto-session-ai-optionality:failure-surface:{id}");
    }

    private sealed class MutableAiHandler : HttpMessageHandler
    {
        public AiSurfaceAction Action { get; set; } = new(AiSurfaceMode.ValidClassifier, true, 0.99);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Action.Mode switch
            {
                AiSurfaceMode.RateLimited => Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"rate limited\"}}")),
                AiSurfaceMode.ProviderUnavailable => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"unavailable\"}}")),
                AiSurfaceMode.Timeout => Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated provider timeout")),
                AiSurfaceMode.MalformedEnvelope => Task.FromResult(Response(HttpStatusCode.OK, "{not-json")),
                AiSurfaceMode.EmptyCompletion => Task.FromResult(Response(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"\"}}]}")),
                AiSurfaceMode.MalformedClassifierJson => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope("{not-classifier-json"))),
                _ => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope(JsonSerializer.Serialize(new
                {
                    isVolleyballSignupPoll = Action.IsSignup,
                    confidence = Action.Confidence,
                    reason = "fuzz_contradiction"
                }))))
            };
        }

        private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static string CompletionEnvelope(string content) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content }, finish_reason = "stop" } }
        });
    }
}
