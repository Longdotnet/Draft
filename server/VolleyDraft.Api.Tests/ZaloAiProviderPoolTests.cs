using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAiProviderPoolTests
{
    [Fact]
    public async Task ProviderPool_UsesConfiguredOrderAndIgnoresDeadLegacyModel()
    {
        var calls = new List<(string Host, string Model)>();
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var model = json.RootElement.GetProperty("model").GetString()!;
            calls.Add((request.RequestUri!.Host, model));

            return request.RequestUri.Host switch
            {
                "p1.test" => ErrorResponse(HttpStatusCode.ServiceUnavailable, "provider_unavailable", "down"),
                "p2.test" => ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "busy"),
                _ => JsonResponse(HttpStatusCode.OK, "recovered")
            };
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://legacy.test/chat",
            ["Ai:ApiKey"] = "legacy-key",
            ["Ai:Model"] = "dead-legacy-model",
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "p1",
            ["Ai:Providers:0:Endpoint"] = "https://p1.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-1",
            ["Ai:Providers:0:Model"] = "inclusionai/ling-3.0-flash-vl:free",
            ["Ai:Providers:1:Provider"] = "p2",
            ["Ai:Providers:1:Endpoint"] = "https://p2.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-2",
            ["Ai:Providers:1:Model"] = "nex-agi/nex-n2.5-pro:free",
            ["Ai:Providers:2:Provider"] = "p3",
            ["Ai:Providers:2:Endpoint"] = "https://p3.test/chat",
            ["Ai:Providers:2:ApiKey"] = "key-3",
            ["Ai:Providers:2:Model"] = "nex-agi/nex-n2.5-mini:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "extract")],
            Temperature: 0));

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal("p3", result.Provider);
        Assert.Equal("nex-agi/nex-n2.5-mini:free", result.Model);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(
            new[]
            {
                ("p1.test", "inclusionai/ling-3.0-flash-vl:free"),
                ("p2.test", "nex-agi/nex-n2.5-pro:free"),
                ("p3.test", "nex-agi/nex-n2.5-mini:free")
            },
            calls);
        Assert.DoesNotContain(calls, call => call.Model == "dead-legacy-model");
    }

    [Fact]
    public async Task ProviderPool_BlankKeysInheritLegacyKeyAndExactDuplicatesAreSkipped()
    {
        var models = new List<string>();
        var auth = new List<string?>();
        var handler = new StubHandler(async request =>
        {
            auth.Add(request.Headers.Authorization?.ToString());
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var model = json.RootElement.GetProperty("model").GetString()!;
            models.Add(model);
            return model.Contains("ling-3.0", StringComparison.Ordinal)
                ? ErrorResponse(HttpStatusCode.ServiceUnavailable, "provider_unavailable", "down")
                : JsonResponse(HttpStatusCode.OK, "fallback-ok");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:ApiKey"] = "shared-key",
            ["Ai:Model"] = "dead-legacy-model",
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-1",
            ["Ai:Providers:0:Model"] = "inclusionai/ling-3.0-flash-vl:free",
            ["Ai:Providers:1:Provider"] = "slot-4-duplicate",
            ["Ai:Providers:1:Model"] = "inclusionai/ling-3.0-flash-vl:free",
            ["Ai:Providers:2:Provider"] = "slot-3",
            ["Ai:Providers:2:Model"] = "nex-agi/nex-n2.5-mini:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(
            new[]
            {
                "inclusionai/ling-3.0-flash-vl:free",
                "nex-agi/nex-n2.5-mini:free"
            },
            models);
        Assert.All(auth, value => Assert.Equal("Bearer shared-key", value));
    }

    [Fact]
    public async Task ProviderPool_SameModelWithDifferentKeysRemainsARealFailoverSlot()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            var value = request.Headers.Authorization?.Parameter;
            auth.Add(value);
            return Task.FromResult(value == "key-a"
                ? ErrorResponse(HttpStatusCode.Unauthorized, "invalid_api_key", "bad key")
                : JsonResponse(HttpStatusCode.OK, "second-key-ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-1",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "nex-agi/nex-n2.5-pro:free",
            ["Ai:Providers:1:Provider"] = "slot-5",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "nex-agi/nex-n2.5-pro:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.SocialReply,
            [new ZaloAiChatMessage("user", "reply")])) ;

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
    }

    private static OpenAiCompatibleZaloAiGateway CreateGateway(
        HttpMessageHandler handler,
        IConfiguration configuration) =>
        new(
            new HttpClient(handler),
            configuration,
            NullLogger<OpenAiCompatibleZaloAiGateway>.Instance);

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content } }
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string code, string message) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { code, message } }),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
