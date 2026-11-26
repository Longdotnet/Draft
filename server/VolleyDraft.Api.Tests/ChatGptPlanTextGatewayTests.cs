using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ChatGptPlanTextGatewayTests
{
    private readonly ZaloAiCallerScope caller = new("connection", "group", Guid.NewGuid().ToString());

    private IConfigurationRoot Configuration(bool approved = true, bool enabled = true, bool expired = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:ChatGptPlan:Enabled"] = enabled.ToString(),
            ["Ai:ChatGptPlan:HostedAccessApproved"] = approved.ToString(),
            ["Ai:ChatGptPlan:ConnectionId"] = caller.ConnectionId,
            ["Ai:ChatGptPlan:GroupId"] = caller.GroupId,
            ["Ai:ChatGptPlan:SenderId"] = caller.SenderId,
            ["Ai:ChatGptPlan:AccessToken"] = "test-only-oauth-token",
            ["Ai:ChatGptPlan:AccessTokenExpiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(expired ? -5 : 60).ToString("O"),
            ["Ai:ChatGptPlan:GrantedScopes"] = "resource.invoke chatgpt.tokens.use.direct",
            ["Ai:ChatGptPlan:Model"] = "test-model"
        }).Build();

    private ZaloAiCompletionRequest Request() => new(ZaloAiWorkload.GeneralChat,
        [new("system", "instructions"), new("user", "ping")], Caller: caller);

    private static HttpResponseMessage Stream(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("", events.Select(e => $"data: {e}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage Completed() => Stream(
        """{"type":"response.output_text.delta","delta":"hello "}""",
        """{"type":"response.output_text.delta","delta":"world"}""",
        """{"type":"response.completed"}""");

    [Fact]
    public async Task UsesOfficialResponsesContractAndCompletedText()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(async req =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", req.RequestUri!.AbsoluteUri);
            Assert.Equal("test-only-oauth-token", req.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("developer", json.RootElement.GetProperty("input")[0].GetProperty("role").GetString());
            Assert.False(json.RootElement.TryGetProperty("temperature", out _));
            Assert.False(json.RootElement.TryGetProperty("max_output_tokens", out _));
            Assert.False(json.RootElement.TryGetProperty("caller", out _));
            return Completed();
        });
        using var client = new HttpClient(handler);
        var gateway = new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback);
        var result = await gateway.CompleteAsync(Request());
        Assert.True(result.Success);
        Assert.Equal("hello world", result.Content);
        Assert.Equal("ChatGPTPlan", result.Provider);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task DisabledUnapprovedExpiredOrDifferentCallerUsesExistingGatewayOnly()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ => throw new InvalidOperationException("Unexpected request"));
        using var client = new HttpClient(handler);
        foreach (var config in new[] { Configuration(enabled: false), Configuration(approved: false), Configuration(expired: true) })
        {
            var gateway = new ChatGptPlanTextGateway(client, config, NullLogger.Instance, fallback);
            Assert.Equal("fallback", (await gateway.CompleteAsync(Request())).Provider);
        }
        var enabled = new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback);
        Assert.Equal("fallback", (await enabled.CompleteAsync(Request() with { Caller = caller with { SenderId = "other" } })).Provider);
        Assert.Equal("fallback", (await enabled.CompleteAsync(Request() with { Caller = null })).Provider);
        Assert.Equal("fallback", (await enabled.CompleteAsync(Request() with { Caller = caller with { GroupId = "other" } })).Provider);
        Assert.Equal("fallback", (await enabled.CompleteAsync(Request() with { Workload = ZaloAiWorkload.IntentClassification })).Provider);
        Assert.Equal(7, fallback.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaFailureFallsBackAndPausesFurtherPlanRequests(bool streaming)
    {
        var calls = 0;
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(streaming
                ? Stream("""{"type":"response.failed","response":{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}}""")
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"error":{"code":"subscription_sharing_usage_limit_exceeded"}}""")
                });
        });
        using var client = new HttpClient(handler);
        var gateway = new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback);
        var result = await gateway.CompleteAsync(Request());
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("fallback", (await gateway.CompleteAsync(Request())).Provider);
        Assert.Equal(1, calls);
        Assert.Equal(2, fallback.Calls);
    }

    [Fact]
    public async Task PermissionFailureDoesNotFallBack()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"subscription_sharing_user_not_eligible"}}""")
        }));
        using var client = new HttpClient(handler);
        var result = await new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback).CompleteAsync(Request());
        Assert.Equal(ZaloAiFailureKind.AuthenticationFailed, result.FailureKind);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task RefusalIsReturnedWithoutCallingAnotherProvider()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ => Task.FromResult(Stream(
            """{"type":"response.refusal.delta","delta":"I cannot help."}""",
            """{"type":"response.completed"}""")));
        using var client = new HttpClient(handler);
        var result = await new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback).CompleteAsync(Request());
        Assert.True(result.Success);
        Assert.Equal("I cannot help.", result.Content);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task IncompleteStreamNeverPublishesPartialText()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ => Task.FromResult(Stream(
            """{"type":"response.output_text.delta","delta":"partial"}""")));
        using var client = new HttpClient(handler);
        var result = await new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback).CompleteAsync(Request());
        Assert.False(result.Success);
        Assert.Null(result.Content);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task TemporaryNetworkFailureUsesExistingGateway()
    {
        var fallback = new FakeFallback();
        using var handler = new StubHandler(_ => throw new HttpRequestException("test"));
        using var client = new HttpClient(handler);
        var result = await new ChatGptPlanTextGateway(client, Configuration(), NullLogger.Instance, fallback).CompleteAsync(Request());
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(1, fallback.Calls);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeFallback : IZaloAiGateway
    {
        public bool IsConfigured => true;
        public int Calls { get; private set; }

        public Task<ZaloAiCompletionResult> CompleteAsync(ZaloAiCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ZaloAiCompletionResult(true, "legacy response", ZaloAiFailureKind.None,
                "fallback", "legacy-model", 1, 200, TimeSpan.Zero, false));
        }
    }
}
