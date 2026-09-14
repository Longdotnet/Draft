using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class AiOptionalityFailureFuzzTests
{
    [Fact]
    public async Task Structured_rewrite_failure_mutations_fail_closed_to_deterministic_answer()
    {
        for (var seed = 1; seed <= 96; seed += 1)
        {
            var mode = seed % 8;
            var service = CreateService(_ => mode switch
            {
                0 => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = Json("{\"error\":{\"code\":\"rate_limit_exceeded\"}}")
                },
                1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = Json("{\"error\":{\"code\":\"insufficient_quota\"}}")
                },
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("provider unavailable")
                },
                3 => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = Json("{\"error\":{\"message\":\"secret-key-must-not-escape\"}}")
                },
                4 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = Json("{\"unexpected\":true}")
                },
                5 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = Json("{not-json")
                },
                6 => throw new HttpRequestException("dns secret.internal.example"),
                _ => throw new TaskCanceledException("transport timeout")
            });

            var grounded = $"Đã lên lịch nhắc trận #{seed} lúc 18:30.";
            var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
                seed % 2 == 0 ? "nhắc tui lúc 18:30" : "nho nhac tui 18h30",
                "Long",
                ZaloBotIntent.ScheduleReminder,
                grounded));

            Assert.True(
                result is null,
                $"seed={seed}; mode={mode}; fingerprint=ai-optionality:structured-failure-overrode-deterministic-truth; result={result}");
        }
    }

    [Fact]
    public async Task Failure_payload_mutations_never_become_authoritative_structured_output()
    {
        var malformedBodies = new[]
        {
            "",
            "null",
            "{}",
            "[]",
            "{\"choices\":[]}",
            "{\"choices\":[{}]}",
            "{\"choices\":[{\"message\":{}}]}",
            "{\"choices\":[{\"message\":{\"content\":null}}]}",
            "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}"
        };

        for (var seed = 0; seed < malformedBodies.Length * 8; seed += 1)
        {
            var body = malformedBodies[seed % malformedBodies.Length];
            var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(body)
            });

            var grounded = $"Slot #{seed + 1} thuộc về UID stable-{seed + 1}.";
            var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
                seed % 2 == 0 ? "ai nói lại dùm" : "viet lai cho dep",
                "Long",
                ZaloBotIntent.GeneralChat,
                grounded));

            Assert.True(
                result is null,
                $"seed={seed}; fingerprint=ai-optionality:malformed-success-became-authoritative; body={body}; result={result}");
        }
    }

    private static AiAssistantService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();

        return new AiAssistantService(
            new HttpClient(new StubHandler(responseFactory)),
            configuration,
            NullLogger<AiAssistantService>.Instance);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
