using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class OpenClawTextGatewayTests
{
    private readonly ZaloAiCallerScope caller = new("connection", "group", Guid.NewGuid().ToString());

    private IConfiguration Configuration(bool enabled = true, string endpoint = "https://claw.test/v1/chat/completions") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:OpenClaw:Enabled"] = enabled.ToString(),
            ["Ai:OpenClaw:Endpoint"] = endpoint,
            ["Ai:OpenClaw:GatewayToken"] = "test-gateway-token",
            ["Ai:OpenClaw:ConnectionId"] = caller.ConnectionId,
            ["Ai:OpenClaw:GroupId"] = caller.GroupId,
            ["Ai:OpenClaw:SenderId"] = caller.SenderId,
            ["Ai:Endpoint"] = "https://fallback.test/chat",
            ["Ai:ApiKey"] = "test-fallback-key",
            ["Ai:Model"] = "legacy-model"
        }).Build();

    private ZaloAiCompletionRequest Request() => new(ZaloAiWorkload.GeneralChat,
        [new("system", "rules"), new("user", "hello")], Caller: caller);

    private static HttpResponseMessage Reply(string content, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new
        {
            message = new { role = "assistant", content }, finish_reason = finish
        } } }))
    };

    [Fact]
    public async Task UsesExistingClientWithFixedAgentAndStatelessMessages()
    {
        using var client = new HttpClient(new Handler(async req =>
        {
            Assert.Equal("https://claw.test/v1/chat/completions", req.RequestUri!.AbsoluteUri);
            Assert.Equal("test-gateway-token", req.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
            Assert.Equal("openclaw/default", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("hello", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            foreach (var field in new[] { "user", "tools", "caller", "session_id" })
                Assert.False(json.RootElement.TryGetProperty(field, out _));
            Assert.False(req.Headers.Contains("x-openclaw-model"));
            Assert.False(req.Headers.Contains("x-openclaw-session-key"));
            return Reply("answer");
        }));
        var gateway = ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance);
        var result = await gateway.CompleteAsync(Request() with { ModelOverride = "other-model" });
        Assert.True(result.Success);
        Assert.Equal("OpenClaw", result.Provider);
        Assert.Equal("answer", result.Content);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task UnauthorizedScopeAndDisabledOrInvalidConfigurationKeepOriginalProvider()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            Assert.Equal("fallback.test", req.RequestUri!.Host);
            Assert.Equal("test-fallback-key", req.Headers.Authorization!.Parameter);
            return Task.FromResult(Reply("legacy"));
        }));
        var gateway = ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance);
        foreach (var request in new[] { Request() with { Caller = null },
            Request() with { Caller = caller with { SenderId = "other" } },
            Request() with { Caller = caller with { GroupId = "other" } },
            Request() with { Caller = caller with { ConnectionId = "other" } },
            Request() with { Workload = ZaloAiWorkload.IntentClassification } })
            Assert.Equal("legacy", (await gateway.CompleteAsync(request)).Content);
        foreach (var config in new[] { Configuration(false), Configuration(endpoint: "http://claw.test/v1/chat/completions"),
            Configuration(endpoint: "https://claw.test/other") })
            Assert.Equal("legacy", (await ZaloAiGatewayFactory.Create(client, config, NullLogger.Instance).CompleteAsync(Request())).Content);
        Assert.Equal(8, calls);
    }

    [Theory]
    [InlineData(402, "insufficient_quota")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(429, "insufficient_quota")]
    public async Task QuotaAndRateLimitFallBackAndCooldownAcrossFactoryInstances(int status, string type)
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.Host == "fallback.test")
            {
                fallbackCalls++;
                return Task.FromResult(Reply("legacy"));
            }
            primaryCalls++;
            return Task.FromResult(Error(status, type));
        }));
        var config = Configuration();
        var first = await ZaloAiGatewayFactory.Create(client, config, NullLogger.Instance).CompleteAsync(Request());
        Assert.True(first.Success);
        Assert.True(first.UsedFallback);
        Assert.Equal(2, first.Attempts);
        var second = await ZaloAiGatewayFactory.Create(client, config, NullLogger.Instance).CompleteAsync(Request());
        Assert.True(second.UsedFallback);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(2, fallbackCalls);
    }

    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(400, "invalid_request_error")]
    [InlineData(429, "content_policy_violation")]
    [InlineData(500, "api_error")]
    public async Task DoesNotFallbackForPermissionSafetyOrAmbiguousErrors(int status, string type)
    {
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal("claw.test", req.RequestUri!.Host);
            return Task.FromResult(Error(status, type));
        }));
        var result = await ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance).CompleteAsync(Request());
        Assert.False(result.Success);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientGatewayErrorsUseFallback(int status)
    {
        using var client = new HttpClient(new Handler(req => Task.FromResult(req.RequestUri!.Host == "claw.test"
            ? Error(status, "api_error") : Reply("legacy"))));
        var result = await ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance).CompleteAsync(Request());
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
    }

    [Theory]
    [InlineData("tool_calls")]
    [InlineData("length")]
    public async Task IncompleteOrToolResponseDoesNotPublishOrFallback(string finish)
    {
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal("claw.test", req.RequestUri!.Host);
            return Task.FromResult(Reply("partial", finish));
        }));
        var result = await ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance).CompleteAsync(Request());
        Assert.False(result.Success);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ConsumesExistingBudgetOnlyOncePerProviderCall()
    {
        using var scope = ZaloAiRequestBudget.Begin(preAnswerCalls: 0, answerCalls: 1);
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal("claw.test", req.RequestUri!.Host);
            return Task.FromResult(Error(429, "insufficient_quota"));
        }));
        var result = await ZaloAiGatewayFactory.Create(client, Configuration(), NullLogger.Instance).CompleteAsync(Request());
        Assert.False(result.Success);
        Assert.Equal("local_request_budget_exhausted", result.ProviderCode);
        Assert.Equal(0, ZaloAiRequestBudget.RemainingForTests);
    }

    private static HttpResponseMessage Error(int status, string type) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { type, message = type } }))
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
