using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionConversationAiAuthorityFuzzTests
{
    private static readonly DateTimeOffset T4 = new(2026, 9, 9, 17, 30, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset T6 = new(2026, 9, 11, 17, 30, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Cn = new(2026, 9, 13, 17, 30, 0, TimeSpan.FromHours(7));

    private static readonly ConversationAction[] SeedActions =
    [
        new("t6 thôi", ZaloAutoSessionConversationState.Discussing, AiMode.ContradictConfirm),
        new("không tạo t6", ZaloAutoSessionConversationState.Discussing, AiMode.ContradictConfirm),
        new("bỏ qua", ZaloAutoSessionConversationState.Discussing, AiMode.ContradictModify),
        new("reset", ZaloAutoSessionConversationState.ReadyToConfirm, AiMode.ContradictConfirm),
        new("tạo đi", ZaloAutoSessionConversationState.Discussing, AiMode.ContradictCancel),
        new("hmm lịch này sao ta", ZaloAutoSessionConversationState.Discussing, AiMode.RateLimited),
        new("xem giúp tui nha", ZaloAutoSessionConversationState.Discussing, AiMode.Timeout),
        new("lịch này ổn hông", ZaloAutoSessionConversationState.ReadyToConfirm, AiMode.MalformedEnvelope),
        new("cũng được á", ZaloAutoSessionConversationState.Discussing, AiMode.MalformedStructuredJson),
        new("theo ông thì sao", ZaloAutoSessionConversationState.Discussing, AiMode.ContradictConfirm),
        new("coi lại giúp tui", ZaloAutoSessionConversationState.Clarifying, AiMode.ContradictCancel),
        new("chưa biết nữa", ZaloAutoSessionConversationState.PreviewSent, AiMode.ContradictModify)
    ];

    [Fact]
    public async Task Ai_failure_and_contradiction_never_override_strong_rules_or_create_execute_authority()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var handler = new MutableAiHandler();
            var interpreter = CreateInterpreter(handler);
            var scenario = new StatefulFuzzCase<ConversationAction>(
                $"auto-session-conversation-ai-authority-{seed}",
                seed,
                StatefulSequenceMutator.Mutate(
                    SeedActions,
                    mutationSeed: seed,
                    createAction: CreateAction,
                    operationCount: 18));

            var result = await StatefulFuzzRunner.RunAsync(
                scenario,
                new ConversationAiAuthorityTarget(interpreter, handler, BuildDraft()));

            Assert.False(
                result.Failed,
                $"Seed={seed}; Fingerprint={result.FailureFingerprint}; action={result.FailureActionIndex}; " +
                $"Exception={result.Exception}; violation={result.Violation?.Message}");
        }
    }

    private static ConversationAction CreateAction(StableFuzzRandom random)
    {
        var texts = new[]
        {
            "t6 thôi", "bỏ cn", "tạo đi", "bỏ qua", "reset", "ok",
            "hmm lịch này sao ta", "xem giúp tui nha", "theo ông thì sao", "chưa biết nữa",
            "lịch này ổn hông", "coi lại giúp tui"
        };
        var states = Enum.GetValues<ZaloAutoSessionConversationState>();
        var modes = Enum.GetValues<AiMode>();
        return new ConversationAction(
            texts[random.NextInt(texts.Length)],
            states[random.NextInt(states.Length)],
            modes[random.NextInt(modes.Length)]);
    }

    private static ZaloAutoSessionConversationInterpreter CreateInterpreter(MutableAiHandler handler)
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

        return new ZaloAutoSessionConversationInterpreter(
            new StubHttpClientFactory(new HttpClient(handler)),
            config,
            NullLogger<ZaloAutoSessionConversationInterpreter>.Instance);
    }

    private static ZaloAutoSessionConversationDraft BuildDraft() => new(
    [
        new("o1", "T4 17h30", "T4", T4, 8, true),
        new("o2", "T6 17h30", "T6", T6, 10, true),
        new("o3", "CN 17h30", "CN", Cn, 9, true)
    ],
    "Sân UTE",
    6);

    private sealed record ConversationAction(
        string Text,
        ZaloAutoSessionConversationState State,
        AiMode Mode);

    private enum AiMode
    {
        RateLimited,
        ProviderUnavailable,
        Timeout,
        MalformedEnvelope,
        MalformedStructuredJson,
        ContradictConfirm,
        ContradictCancel,
        ContradictModify
    }

    private sealed class ConversationAiAuthorityState(
        ZaloAutoSessionConversationInterpreter interpreter,
        MutableAiHandler handler,
        ZaloAutoSessionConversationDraft draft)
    {
        public ZaloAutoSessionConversationInterpreter Interpreter { get; } = interpreter;
        public MutableAiHandler Handler { get; } = handler;
        public ZaloAutoSessionConversationDraft Draft { get; } = draft;
        public ZaloAutoSessionConversationInterpretation? Rules { get; set; }
        public ZaloAutoSessionConversationInterpretation? Actual { get; set; }
        public int RequestsBefore { get; set; }
        public int RequestsAfter { get; set; }
    }

    private sealed class ConversationAiAuthorityTarget(
        ZaloAutoSessionConversationInterpreter interpreter,
        MutableAiHandler handler,
        ZaloAutoSessionConversationDraft draft)
        : IStatefulFuzzTarget<ConversationAiAuthorityState, ConversationAction>
    {
        public string Name => "auto-session-conversation-ai-authority";

        public ConversationAiAuthorityState CreateState(StatefulFuzzCase<ConversationAction> scenario) =>
            new(interpreter, handler, draft);

        public async ValueTask ApplyAsync(
            ConversationAiAuthorityState state,
            ConversationAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Handler.Mode = action.Mode;
            state.Rules = ZaloAutoSessionConversationInterpreter.InterpretByRules(
                action.Text,
                state.Draft,
                action.State,
                null);
            state.RequestsBefore = state.Handler.RequestCount;
            state.Actual = await state.Interpreter.InterpretAsync(
                action.Text,
                state.Draft,
                action.State,
                null,
                cancellationToken);
            state.RequestsAfter = state.Handler.RequestCount;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ConversationAiAuthorityState state)
        {
            if (state.Rules is null || state.Actual is null)
                yield break;

            var rulesOwnTurn = state.Rules.Intent != ZaloAutoSessionConversationIntent.None ||
                               state.Rules.NeedsClarification ||
                               state.Rules.Confidence >= 0.8;

            if (rulesOwnTurn)
            {
                if (state.RequestsAfter != state.RequestsBefore)
                {
                    yield return Violation(
                        "strong-rule-called-ai",
                        "A high-confidence deterministic turn still crossed the AI boundary.");
                }

                if (!Equivalent(state.Rules, state.Actual))
                {
                    yield return Violation(
                        "strong-rule-overridden",
                        $"AI-capable interpreter changed deterministic result rules={state.Rules.Intent}/{state.Rules.Interpreter}, actual={state.Actual.Intent}/{state.Actual.Interpreter}.");
                }
            }

            if (state.Actual.ExplicitExecute &&
                !string.Equals(state.Actual.Interpreter, "rules", StringComparison.Ordinal))
            {
                yield return Violation(
                    "ai-explicit-execute",
                    "AI interpretation created explicit execution authority.");
            }

            if (!double.IsFinite(state.Actual.Confidence) || state.Actual.Confidence is < 0 or > 1)
            {
                yield return Violation(
                    "confidence-domain",
                    $"Interpreter emitted invalid confidence {state.Actual.Confidence}.");
            }
        }

        private static bool Equivalent(
            ZaloAutoSessionConversationInterpretation expected,
            ZaloAutoSessionConversationInterpretation actual) =>
            expected.Intent == actual.Intent &&
            expected.SelectionMode == actual.SelectionMode &&
            expected.ExplicitExecute == actual.ExplicitExecute &&
            expected.NeedsClarification == actual.NeedsClarification &&
            expected.Confidence.Equals(actual.Confidence) &&
            string.Equals(expected.Interpreter, actual.Interpreter, StringComparison.Ordinal) &&
            expected.Days.SequenceEqual(actual.Days, StringComparer.OrdinalIgnoreCase) &&
            expected.TimeOverrides.OrderBy(item => item.Key).SequenceEqual(actual.TimeOverrides.OrderBy(item => item.Key));

        private static StatefulInvariantViolation Violation(string id, string message) =>
            new(
                Domain: "auto-session-ai-optionality",
                Id: id,
                Message: message,
                Fingerprint: $"auto-session-ai-optionality:conversation-authority:{id}");
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class MutableAiHandler : HttpMessageHandler
    {
        private int requestCount;
        public AiMode Mode { get; set; } = AiMode.ContradictConfirm;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Mode switch
            {
                AiMode.RateLimited => Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"rate limited\"}}")),
                AiMode.ProviderUnavailable => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"unavailable\"}}")),
                AiMode.Timeout => Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout")),
                AiMode.MalformedEnvelope => Task.FromResult(Response(HttpStatusCode.OK, "{not-json")),
                AiMode.MalformedStructuredJson => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope("{not-structured-json"))),
                AiMode.ContradictCancel => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope(Structured("cancel")))),
                AiMode.ContradictModify => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope(Structured("modify")))),
                _ => Task.FromResult(Response(HttpStatusCode.OK, CompletionEnvelope(Structured("confirm"))))
            };
        }

        private static string Structured(string intent) => JsonSerializer.Serialize(new
        {
            intent,
            selectionMode = intent == "modify" ? "replace" : "none",
            days = intent == "modify" ? new[] { "T2", "T6" } : Array.Empty<string>(),
            timeOverrides = intent == "modify" ? new Dictionary<string, string> { ["T6"] = "18:00" } : new Dictionary<string, string>(),
            location = intent == "modify" ? "Sân AI tự đoán" : null,
            teamSize = intent == "modify" ? 30 : (int?)null,
            confidence = 0.999,
            needsClarification = false,
            clarification = (string?)null
        });

        private static string CompletionEnvelope(string content) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content }, finish_reason = "stop" } }
        });

        private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
