using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAiGatewayTests
{
    [Fact]
    public async Task CompleteAsync_UsesWorkloadSpecificModel()
    {
        string? requestBody = null;
        var handler = new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, "hello");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "cheap-default",
            ["Ai:Models:SocialReply"] = "social-model",
            ["Ai:RetryCount"] = "0"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.SocialReply,
            [new ZaloAiChatMessage("user", "ping")]));

        Assert.True(result.Success);
        Assert.Equal("social-model", result.Model);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal("social-model", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CompleteAsync_RetriesTransient429Once()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? JsonResponse(HttpStatusCode.TooManyRequests, null)
                : JsonResponse(HttpStatusCode.OK, "recovered"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "model-a",
            ["Ai:RetryCount"] = "1"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "classify")],
            Temperature: 0));

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task CompleteAsync_UsesFallbackProviderWithoutChangingFeatureRequest()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "primary.test"
                ? JsonResponse(HttpStatusCode.Unauthorized, null)
                : JsonResponse(HttpStatusCode.OK, "fallback-ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "primary-provider",
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "expired-key",
            ["Ai:Model"] = "primary-model",
            ["Ai:RetryCount"] = "0",
            ["Ai:Fallback:Provider"] = "backup-provider",
            ["Ai:Fallback:Endpoint"] = "https://fallback.test/chat",
            ["Ai:Fallback:ApiKey"] = "backup-key",
            ["Ai:Fallback:Model"] = "backup-model"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal("backup-provider", result.Provider);
        Assert.Equal("backup-model", result.Model);
        Assert.Equal(["primary.test", "fallback.test"], calls);
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

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string? content)
    {
        var body = content is null
            ? "{\"error\":{\"message\":\"failed\"}}"
            : JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content } }
                }
            });

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
