using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class AiProviderFailoverStatefulFuzzTests
{
    private static readonly FailoverAction[] SeedCorpus =
    [
        new(FailoverActionKind.PrimaryRateLimited),
        new(FailoverActionKind.Complete),
        new(FailoverActionKind.PrimaryMalformedSuccess),
        new(FailoverActionKind.Complete),
        new(FailoverActionKind.PrimaryUnavailable),
        new(FailoverActionKind.Complete),
        new(FailoverActionKind.PrimaryHealthy),
        new(FailoverActionKind.Complete)
    ];

    [Fact]
    public async Task Seed_corpus_preserves_ordered_failover_and_primary_recovery()
    {
        var scenario = new StatefulFuzzCase<FailoverAction>(
            "ai-provider-failover-seed",
            20260915,
            SeedCorpus);
        var target = new FailoverTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.True(target.LastState!.FallbackSuccesses >= 3);
        Assert.True(target.LastState.PrimarySuccesses >= 1);
    }

    [Fact]
    public async Task Mutated_provider_failure_sequences_never_turn_failed_primary_output_into_authoritative_success()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedCorpus,
                seed,
                random => new FailoverAction(
                    (FailoverActionKind)random.NextInt(Enum.GetValues<FailoverActionKind>().Length)),
                operationCount: 16);
            var scenario = new StatefulFuzzCase<FailoverAction>(
                $"ai-provider-failover-{seed}",
                seed,
                actions);
            var target = new FailoverTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<FailoverAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum FailoverActionKind
    {
        PrimaryHealthy,
        PrimaryRateLimited,
        PrimaryUnavailable,
        PrimaryMalformedSuccess,
        PrimaryNetworkFailure,
        Complete,
        NoOp
    }

    internal sealed record FailoverAction(FailoverActionKind Kind);

    internal enum PrimaryMode
    {
        Healthy,
        RateLimited,
        Unavailable,
        MalformedSuccess,
        NetworkFailure
    }

    internal sealed class FailoverState
    {
        public required MutableProviderHandler Handler { get; init; }
        public required OpenAiCompatibleZaloAiGateway Gateway { get; init; }
        public PrimaryMode Mode { get; set; } = PrimaryMode.Healthy;
        public int PrimarySuccesses { get; set; }
        public int FallbackSuccesses { get; set; }
        public string? LastViolation { get; set; }
    }

    internal sealed class FailoverTarget : IStatefulFuzzTarget<FailoverState, FailoverAction>
    {
        public string Name => "ai-provider-failover-state-machine";
        public FailoverState? LastState { get; private set; }

        public FailoverState CreateState(StatefulFuzzCase<FailoverAction> scenario)
        {
            var handler = new MutableProviderHandler();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ai:RetryCount"] = "0",
                    ["Ai:TimeoutSeconds"] = "3",
                    ["Ai:Providers:0:Provider"] = "primary",
                    ["Ai:Providers:0:Endpoint"] = "https://primary.test/chat/completions",
                    ["Ai:Providers:0:ApiKey"] = "primary-test-key",
                    ["Ai:Providers:0:Model"] = "primary-model",
                    ["Ai:Providers:1:Provider"] = "fallback",
                    ["Ai:Providers:1:Endpoint"] = "https://fallback.test/chat/completions",
                    ["Ai:Providers:1:ApiKey"] = "fallback-test-key",
                    ["Ai:Providers:1:Model"] = "fallback-model"
                })
                .Build();
            var gateway = new OpenAiCompatibleZaloAiGateway(
                new HttpClient(handler),
                configuration,
                NullLogger<OpenAiCompatibleZaloAiGateway>.Instance);
            var state = new FailoverState
            {
                Handler = handler,
                Gateway = gateway
            };
            LastState = state;
            return state;
        }

        public async ValueTask ApplyAsync(
            FailoverState state,
            FailoverAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case FailoverActionKind.PrimaryHealthy:
                    state.Mode = PrimaryMode.Healthy;
                    break;
                case FailoverActionKind.PrimaryRateLimited:
                    state.Mode = PrimaryMode.RateLimited;
                    break;
                case FailoverActionKind.PrimaryUnavailable:
                    state.Mode = PrimaryMode.Unavailable;
                    break;
                case FailoverActionKind.PrimaryMalformedSuccess:
                    state.Mode = PrimaryMode.MalformedSuccess;
                    break;
                case FailoverActionKind.PrimaryNetworkFailure:
                    state.Mode = PrimaryMode.NetworkFailure;
                    break;
                case FailoverActionKind.Complete:
                    await CompleteAsync(state, actionIndex, cancellationToken);
                    break;
                case FailoverActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(FailoverState state)
        {
            if (state.LastViolation is not null)
            {
                yield return new StatefulInvariantViolation(
                    "ai-optionality",
                    "provider-failover-authority",
                    state.LastViolation);
            }
        }

        private static async Task CompleteAsync(
            FailoverState state,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Handler.Mode = state.Mode;
            var result = await state.Gateway.CompleteAsync(
                new ZaloAiCompletionRequest(
                    ZaloAiWorkload.SafeRewrite,
                    [new ZaloAiChatMessage("user", $"grounded-{actionIndex}")],
                    CorrelationId: $"fuzz-{actionIndex}"),
                cancellationToken);

            if (!result.Success)
            {
                state.LastViolation = $"Completion failed in mode {state.Mode}: {result.FailureKind}";
                return;
            }

            if (state.Mode == PrimaryMode.Healthy)
            {
                if (result.Provider != "primary" || result.UsedFallback || result.Content != "PRIMARY_OK")
                {
                    state.LastViolation =
                        $"Healthy primary did not remain authoritative: provider={result.Provider}, fallback={result.UsedFallback}, content={result.Content}";
                    return;
                }

                state.PrimarySuccesses += 1;
                return;
            }

            if (result.Provider != "fallback" || !result.UsedFallback || result.Content != "FALLBACK_OK")
            {
                state.LastViolation =
                    $"Failed primary became authoritative or fallback ordering broke: mode={state.Mode}, provider={result.Provider}, fallback={result.UsedFallback}, content={result.Content}";
                return;
            }

            state.FallbackSuccesses += 1;
        }
    }

    internal sealed class MutableProviderHandler : HttpMessageHandler
    {
        public PrimaryMode Mode { get; set; } = PrimaryMode.Healthy;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "fallback.test")
                return Task.FromResult(Success("FALLBACK_OK"));

            return Mode switch
            {
                PrimaryMode.Healthy => Task.FromResult(Success("PRIMARY_OK")),
                PrimaryMode.RateLimited => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = Json("{\"error\":{\"code\":\"rate_limit_exceeded\"}}")
                }),
                PrimaryMode.Unavailable => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = Json("{\"error\":{\"code\":\"provider_unavailable\"}}")
                }),
                PrimaryMode.MalformedSuccess => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = Json("{\"choices\":[{\"message\":{\"content\":\"   \"}}]}")
                }),
                PrimaryMode.NetworkFailure => throw new HttpRequestException("simulated primary network failure"),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private static HttpResponseMessage Success(string content) => new(HttpStatusCode.OK)
        {
            Content = Json($"{{\"choices\":[{{\"message\":{{\"content\":\"{content}\"}},\"finish_reason\":\"stop\"}}]}}")
        };

        private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    }
}
