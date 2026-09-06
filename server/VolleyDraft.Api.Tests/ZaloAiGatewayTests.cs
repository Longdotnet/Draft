using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

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
                ? ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down")
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

    [Theory]
    [InlineData(402)]
    [InlineData(429)]
    public async Task CompleteAsync_QuotaFailure_IsDistinctAndNeverRetried(int statusCode)
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(ErrorResponse(
                (HttpStatusCode)statusCode,
                "insufficient_quota",
                "billing quota exceeded"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "model-a",
            ["Ai:RetryCount"] = "2"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.False(result.Success);
        Assert.Equal(ZaloAiFailureKind.QuotaExceeded, result.FailureKind);
        Assert.False(result.Retryable);
        Assert.Equal("insufficient_quota", result.ProviderCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(401, ZaloAiFailureKind.AuthenticationFailed)]
    [InlineData(404, ZaloAiFailureKind.ModelOrEndpointUnavailable)]
    [InlineData(400, ZaloAiFailureKind.InvalidRequest)]
    [InlineData(503, ZaloAiFailureKind.ProviderUnavailable)]
    public async Task CompleteAsync_UsesSharedFailureTaxonomy(int statusCode, ZaloAiFailureKind expected)
    {
        var handler = new StubHandler(_ => Task.FromResult(
            ErrorResponse((HttpStatusCode)statusCode, "provider_code", "provider detail")));
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "model-a",
            ["Ai:RetryCount"] = "0"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "extract")]));

        Assert.False(result.Success);
        Assert.Equal(expected, result.FailureKind);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal("provider_code", result.ProviderCode);
    }

    [Fact]
    public async Task CompleteAsync_InvalidRequest_DoesNotWasteFallbackCall()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "primary.test"
                ? ErrorResponse(HttpStatusCode.BadRequest, "invalid_request", "same payload will fail elsewhere")
                : JsonResponse(HttpStatusCode.OK, "should-not-run"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "model-a",
            ["Ai:RetryCount"] = "0",
            ["Ai:Fallback:Endpoint"] = "https://fallback.test/chat",
            ["Ai:Fallback:ApiKey"] = "backup-key",
            ["Ai:Fallback:Model"] = "backup-model"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.False(result.Success);
        Assert.Equal(ZaloAiFailureKind.InvalidRequest, result.FailureKind);
        Assert.Equal(new[] { "primary.test" }, calls);
    }

    [Fact]
    public async Task CompleteAsync_QuotaFailure_CanUseIndependentFallbackWithoutRetryingPrimary()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "primary.test"
                ? ErrorResponse((HttpStatusCode)402, "insufficient_quota", "out of credits")
                : JsonResponse(HttpStatusCode.OK, "fallback-ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "primary-provider",
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "expired-key",
            ["Ai:Model"] = "primary-model",
            ["Ai:RetryCount"] = "2",
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
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "primary.test", "fallback.test" }, calls);
    }

    [Fact]
    public async Task CompleteAsync_UsesFallbackProviderWithoutChangingFeatureRequest()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "primary.test"
                ? ErrorResponse(HttpStatusCode.Unauthorized, "invalid_api_key", "failed")
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
        Assert.Equal(new[] { "primary.test", "fallback.test" }, calls);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotLogRawProviderBodyOrExceptionMessage()
    {
        const string secret = "super-secret-provider-detail";
        var logger = new CapturingLogger<OpenAiCompatibleZaloAiGateway>();
        var handler = new StubHandler(_ => Task.FromResult(
            ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", secret)));
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://primary.test/chat",
            ["Ai:ApiKey"] = "key",
            ["Ai:Model"] = "model-a",
            ["Ai:RetryCount"] = "0"
        });
        var gateway = CreateGateway(handler, configuration, logger);

        await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("RateLimited", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("rate_limit_exceeded", StringComparison.Ordinal));
    }

    private static OpenAiCompatibleZaloAiGateway CreateGateway(
        HttpMessageHandler handler,
        IConfiguration configuration,
        ILogger<OpenAiCompatibleZaloAiGateway>? logger = null) =>
        new(
            new HttpClient(handler),
            configuration,
            logger ?? NullLogger<OpenAiCompatibleZaloAiGateway>.Instance);

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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
