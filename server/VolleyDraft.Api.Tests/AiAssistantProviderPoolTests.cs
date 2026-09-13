using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiAssistantProviderPoolTests
{
    [Fact]
    public async Task AnswerAsync_ProviderPoolOnly_FailsOverToSecondCredential()
    {
        var calls = new List<(string Host, string? Authorization, string Model)>();
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            calls.Add((
                request.RequestUri!.Host,
                request.Headers.Authorization?.Parameter,
                json.RootElement.GetProperty("model").GetString()!));

            return request.RequestUri.Host == "legacy-primary.test"
                ? ErrorResponse(HttpStatusCode.ServiceUnavailable, "provider_unavailable", "down")
                : JsonResponse(HttpStatusCode.OK, "fallback general answer");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:TimeoutSeconds"] = "6",
            ["Ai:Providers:0:Provider"] = "legacy-primary",
            ["Ai:Providers:0:Endpoint"] = "https://legacy-primary.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-1",
            ["Ai:Providers:0:Model"] = "model-1",
            ["Ai:Providers:1:Provider"] = "legacy-backup",
            ["Ai:Providers:1:Endpoint"] = "https://legacy-backup.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-2",
            ["Ai:Providers:1:Model"] = "model-2"
        });
        var service = CreateService(handler, configuration);

        Assert.True(service.IsConfigured);
        var answer = await service.AnswerAsync(new ZaloAiContext(
            "group-1",
            new ZaloAiSender("u1", "Long"),
            "hello",
            [],
            [],
            null,
            [],
            DateTimeOffset.UtcNow));

        Assert.Equal("fallback general answer", answer);
        Assert.Equal(
            new[]
            {
                ("legacy-primary.test", "key-1", "model-1"),
                ("legacy-backup.test", "key-2", "model-2")
            },
            calls);
    }

    [Fact]
    public async Task ClassifyAsync_ProviderPoolOnly_UsesGatewayInsteadOfLegacyAiKeys()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "classifier-primary.test"
                ? ErrorResponse(HttpStatusCode.NotFound, "model_not_found", "gone")
                : JsonResponse(
                    HttpStatusCode.OK,
                    "{\"intent\":\"GeneralChat\",\"confidence\":0.99,\"sessionReference\":null,\"needsClarification\":false,\"clarificationQuestion\":null,\"reason\":\"chat\"}"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "classifier-primary",
            ["Ai:Providers:0:Endpoint"] = "https://classifier-primary.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "classifier-a",
            ["Ai:Providers:1:Provider"] = "classifier-backup",
            ["Ai:Providers:1:Endpoint"] = "https://classifier-backup.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "classifier-b"
        });
        var service = CreateService(handler, configuration);

        var decision = await service.ClassifyAsync(new ZaloIntentClassifierContext(
            "nói chuyện chút",
            new ZaloAiSender("u1", "Long"),
            [],
            [],
            DateTimeOffset.UtcNow));

        Assert.Equal(ZaloBotIntent.GeneralChat, decision.Intent);
        Assert.Equal(0.99, decision.Confidence, 2);
        Assert.Equal(new[] { "classifier-primary.test", "classifier-backup.test" }, calls);
    }

    private static AiAssistantService CreateService(
        HttpMessageHandler handler,
        IConfiguration configuration) =>
        new(
            new HttpClient(handler),
            configuration,
            NullLogger<AiAssistantService>.Instance);

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
