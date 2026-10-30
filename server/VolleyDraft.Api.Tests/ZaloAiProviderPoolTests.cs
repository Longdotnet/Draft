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

    [Fact]
    public async Task StructuredWorkload_SendsJsonObjectFormat_AndDisablesXingReasoningByDefault()
    {
        JsonElement? captured = null;
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            captured = json.RootElement.Clone();
            return JsonResponse(HttpStatusCode.OK, "{\"ok\":true}");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Providers:0:Provider"] = "xing",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "xingchen-agi/xing4.0-29b-a4b:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "extract json")]));

        Assert.True(result.Success);
        Assert.True(captured.HasValue);
        Assert.Equal("json_object", captured.Value.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(captured.Value.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task StructuredWorkload_CanOptIntoXingReasoning()
    {
        JsonElement? captured = null;
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            captured = json.RootElement.Clone();
            return JsonResponse(HttpStatusCode.OK, "{\"ok\":true}");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Providers:0:Provider"] = "xing",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "xingchen-agi/xing4.0-29b-a4b:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "understand a natural-language action")],
            ReasoningEnabled: true));

        Assert.True(result.Success);
        Assert.True(captured.HasValue);
        Assert.Equal("json_object", captured.Value.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(captured.Value.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task InvalidResponse_DoesNotHammerSameModelAcrossDifferentKeys()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls += 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "" } } } }),
                    Encoding.UTF8,
                    "application/json")
            });
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "xingchen-agi/xing4.0-29b-a4b:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "extract")])) ;

        Assert.False(result.Success);
        Assert.Equal(ZaloAiFailureKind.InvalidResponse, result.FailureKind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DynamicFreeRouter_InvalidResponse_FailsOverAcrossAccounts()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            var key = request.Headers.Authorization?.Parameter;
            auth.Add(key);
            return Task.FromResult(key == "key-a"
                ? JsonResponse(HttpStatusCode.OK, "")
                : JsonResponse(HttpStatusCode.OK, "router-recovered"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "openrouter/free",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "openrouter/free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.True(result.Success);
        Assert.Equal("router-recovered", result.Content);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
    }

    [Fact]
    public async Task DynamicFreeRouter_Timeout_FailsOverAcrossAccounts()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            var key = request.Headers.Authorization?.Parameter;
            auth.Add(key);
            if (key == "key-a")
                throw new TaskCanceledException("simulated provider timeout");

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "router-timeout-recovered"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "openrouter/free",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "openrouter/free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")]));

        Assert.True(result.Success);
        Assert.Equal("router-timeout-recovered", result.Content);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
    }

    [Fact]
    public async Task DomainNarration_FixedTimeout_SkipsEquivalentAccount_AndUsesRouterOverride()
    {
        var attempts = new List<(string Key, string Model)>();
        var handler = new StubHandler(async request =>
        {
            var raw = await request.Content!.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(raw);
            var model = payload.RootElement.GetProperty("model").GetString() ?? string.Empty;
            var key = request.Headers.Authorization?.Parameter ?? string.Empty;
            attempts.Add((key, model));
            if (model == "xingchen-agi/xing4.0-29b-a4b:free")
                throw new TaskCanceledException("simulated fixed-model timeout");

            return JsonResponse(
                HttpStatusCode.OK,
                """{"answer":"router recovered","messageIds":[]}""");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Models:DomainNarration"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:2:Provider"] = "slot-c",
            ["Ai:Providers:2:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:2:ApiKey"] = "key-c",
            ["Ai:Providers:2:Model"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:2:Models:DomainNarration"] = "openrouter/free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.DomainNarration,
            [new ZaloAiChatMessage("user", "summarize")]));

        Assert.True(result.Success);
        Assert.Equal("openrouter/free", result.Model);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(
            new[]
            {
                ("key-a", "xingchen-agi/xing4.0-29b-a4b:free"),
                ("key-c", "openrouter/free")
            },
            attempts);
        Assert.DoesNotContain(attempts, attempt => attempt.Key == "key-b");
    }

    [Fact]
    public async Task ProviderPool_RoundRobinsHealthyAccountsAcrossRequests()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            auth.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://round-robin.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "free-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://round-robin.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "free-model",
            ["Ai:Providers:2:Provider"] = "slot-c",
            ["Ai:Providers:2:Endpoint"] = "https://round-robin.test/chat",
            ["Ai:Providers:2:ApiKey"] = "key-c",
            ["Ai:Providers:2:Model"] = "free-model"
        });
        var gateway = CreateGateway(handler, configuration);

        for (var index = 0; index < 3; index++)
        {
            var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
                ZaloAiWorkload.GeneralChat,
                [new ZaloAiChatMessage("user", $"hello-{index}")]));
            Assert.True(result.Success);
            Assert.Equal(1, result.Attempts);
        }

        Assert.Equal(new[] { "key-a", "key-b", "key-c" }, auth);
    }

    [Fact]
    public async Task RateLimited_UsesAtMostOneAlternate_AndCoolsFailedAccounts()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            auth.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.TooManyRequests,
                "rate_limit_exceeded",
                "rate limited"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:RateLimitCooldownSeconds"] = "120",
            ["Ai:MaxRateLimitProviderAttempts"] = "2",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://cooldown.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "free-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://cooldown.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "free-model",
            ["Ai:Providers:2:Provider"] = "slot-c",
            ["Ai:Providers:2:Endpoint"] = "https://cooldown.test/chat",
            ["Ai:Providers:2:ApiKey"] = "key-c",
            ["Ai:Providers:2:Model"] = "free-model"
        });
        var gateway = CreateGateway(handler, configuration);

        var first = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "first")]));
        var second = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "second")]));

        Assert.False(first.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, first.FailureKind);
        Assert.Equal(2, first.Attempts);
        Assert.False(second.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, second.FailureKind);
        Assert.Equal(1, second.Attempts);
        Assert.Equal(new[] { "key-a", "key-b", "key-c" }, auth);
    }

    [Fact]
    public async Task RequestBudget_ReservesFiveAnswerCallsAfterOnePreAnswerCall()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls += 1;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://budget.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "free-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var first = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "one")]));
        var second = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "two")]));
        var third = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.SocialReply,
            [new ZaloAiChatMessage("user", "three")]));
        var fourth = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "four")]));
        var fifth = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.SocialReply,
            [new ZaloAiChatMessage("user", "five")]));
        var sixth = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.DomainNarration,
            [new ZaloAiChatMessage("user", "six")]));
        var seventh = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "seven")]));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(third.Success);
        Assert.True(fourth.Success);
        Assert.True(fifth.Success);
        Assert.True(sixth.Success);
        Assert.False(seventh.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, seventh.FailureKind);
        Assert.Equal("local_request_budget_exhausted", seventh.ProviderCode);
        Assert.Equal(0, seventh.Attempts);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task RequestBudget_ClassifierFailoverCannotStarveGeneralChat()
    {
        var models = new List<string>();
        var auth = new List<string?>();
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var model = json.RootElement.GetProperty("model").GetString()!;
            models.Add(model);
            auth.Add(request.Headers.Authorization?.Parameter);
            return model == "classifier-model"
                ? ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "classifier busy")
                : JsonResponse(HttpStatusCode.OK, "chat-ok");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:MaxRateLimitProviderAttempts"] = "2",
            ["Ai:Models:IntentClassification"] = "classifier-model",
            ["Ai:Models:GeneralChat"] = "chat-model",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://budget-priority.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "default-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://budget-priority.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "default-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var classifier = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "classify this natural message")]));
        var chat = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "answer naturally")]));

        Assert.False(classifier.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, classifier.FailureKind);
        Assert.Equal(1, classifier.Attempts);
        Assert.True(chat.Success);
        Assert.Equal("chat-ok", chat.Content);
        Assert.Equal(new[] { "classifier-model", "chat-model" }, models);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
    }

    [Fact]
    public async Task RequestBudget_DirectedAudienceStructuredExtraction_CanUseAnswerPoolAndFailOverAfter429()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            var key = request.Headers.Authorization?.Parameter;
            auth.Add(key);
            return Task.FromResult(key == "key-a"
                ? ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "busy")
                : JsonResponse(HttpStatusCode.OK, "{\"route\":\"Send\"}"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:MaxRateLimitProviderAttempts"] = "2",
            ["Ai:Models:StructuredExtraction"] = "structured-model",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://directed-audience.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "default-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://directed-audience.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "default-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "tag mấy người đó nhớ đem banh")],
            UseAnswerBudget: true));

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
        Assert.Equal(4, ZaloAiRequestBudget.RemainingForTests);
    }

    [Fact]
    public async Task RequestBudget_NormalStructuredExtraction_RemainsSingleAttemptPreAnswer()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            auth.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.TooManyRequests,
                "rate_limit_exceeded",
                "busy"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:MaxRateLimitProviderAttempts"] = "2",
            ["Ai:Models:StructuredExtraction"] = "structured-model",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://normal-structured.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "default-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://normal-structured.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "default-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "ordinary extract") ]));

        Assert.False(result.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, result.FailureKind);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(new[] { "key-a" }, auth);
        Assert.Equal(5, ZaloAiRequestBudget.RemainingForTests);
    }

    [Fact]
    public async Task RequestBudget_BlocksSecondPreAnswerStageAndPreservesAnswerPool()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls += 1;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "classified"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://two-classifiers.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "classifier-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var first = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "stage one")]));
        var second = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "stage two")]));
        var answer = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "answer")]));

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("local_request_budget_exhausted", second.ProviderCode);
        Assert.True(answer.Success);
        Assert.Equal(2, calls);
        Assert.Equal(4, ZaloAiRequestBudget.RemainingForTests);
    }

    [Fact]
    public async Task RequestBudget_ClassifierInvalidResponse_ThenGeneralChat429_CanFailOverAcrossFiveProviders()
    {
        var calls = new List<(string Key, string Model)>();
        var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var model = json.RootElement.GetProperty("model").GetString()!;
            var key = request.Headers.Authorization?.Parameter ?? string.Empty;
            calls.Add((key, model));

            if (model == "classifier-model")
                return JsonResponse(HttpStatusCode.OK, "");
            if (key is "key-a" or "key-b" or "key-c" or "key-d")
                return ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "busy");
            return JsonResponse(HttpStatusCode.OK, "chat-fallback-ok");
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:MaxRateLimitProviderAttempts"] = "5",
            ["Ai:Models:IntentClassification"] = "classifier-model",
            ["Ai:Models:GeneralChat"] = "chat-model",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://stage-budget.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "default-model",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://stage-budget.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "default-model",
            ["Ai:Providers:2:Provider"] = "slot-c",
            ["Ai:Providers:2:Endpoint"] = "https://stage-budget.test/chat",
            ["Ai:Providers:2:ApiKey"] = "key-c",
            ["Ai:Providers:2:Model"] = "default-model",
            ["Ai:Providers:3:Provider"] = "slot-d",
            ["Ai:Providers:3:Endpoint"] = "https://stage-budget.test/chat",
            ["Ai:Providers:3:ApiKey"] = "key-d",
            ["Ai:Providers:3:Model"] = "default-model",
            ["Ai:Providers:4:Provider"] = "slot-e",
            ["Ai:Providers:4:Endpoint"] = "https://stage-budget.test/chat",
            ["Ai:Providers:4:ApiKey"] = "key-e",
            ["Ai:Providers:4:Model"] = "default-model"
        });
        var gateway = CreateGateway(handler, configuration);

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var classifier = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "classify") ]));
        var answer = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "answer") ]));

        Assert.False(classifier.Success);
        Assert.Equal(ZaloAiFailureKind.InvalidResponse, classifier.FailureKind);
        Assert.True(answer.Success);
        Assert.Equal("chat-fallback-ok", answer.Content);
        Assert.Equal(
            new[]
            {
                ("key-a", "classifier-model"),
                ("key-a", "chat-model"),
                ("key-b", "chat-model"),
                ("key-c", "chat-model"),
                ("key-d", "chat-model"),
                ("key-e", "chat-model")
            },
            calls);
        Assert.Equal(0, ZaloAiRequestBudget.RemainingForTests);
    }

    [Fact]
    public async Task RequestBudget_AllAnswerProvidersFail_DoesNotSendSixthExternalCall()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            auth.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.TooManyRequests,
                "rate_limit_exceeded",
                "busy"));
        });
        var values = new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:MaxRateLimitProviderAttempts"] = "6"
        };
        for (var index = 0; index < 6; index++)
        {
            values[$"Ai:Providers:{index}:Provider"] = $"slot-{index + 1}";
            values[$"Ai:Providers:{index}:Endpoint"] = "https://budget-cap.test/chat";
            values[$"Ai:Providers:{index}:ApiKey"] = $"key-{index + 1}";
            values[$"Ai:Providers:{index}:Model"] = "openrouter/free";
        }
        var gateway = CreateGateway(handler, BuildConfiguration(values));

        using var budget = ZaloAiRequestBudget.Begin(preAnswerCalls: 1, answerCalls: 5);
        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "answer") ]));

        Assert.False(result.Success);
        Assert.Equal("local_request_budget_exhausted", result.ProviderCode);
        Assert.Equal(5, auth.Count);
        Assert.Equal(new[] { "key-1", "key-2", "key-3", "key-4", "key-5" }, auth);
        Assert.Equal(1, ZaloAiRequestBudget.RemainingForTests);
    }

    [Fact]
    public async Task RateLimited_FailsOverToSameModelWithDifferentKey()
    {
        var auth = new List<string?>();
        var handler = new StubHandler(request =>
        {
            var apiKey = request.Headers.Authorization?.Parameter;
            auth.Add(apiKey);
            return Task.FromResult(apiKey == "key-a"
                ? ErrorResponse(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "rate limited")
                : JsonResponse(HttpStatusCode.OK, "second-key-ok"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "key-a",
            ["Ai:Providers:0:Model"] = "xingchen-agi/xing4.0-29b-a4b:free",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "key-b",
            ["Ai:Providers:1:Model"] = "xingchen-agi/xing4.0-29b-a4b:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.StructuredExtraction,
            [new ZaloAiChatMessage("user", "extract")])) ;

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new[] { "key-a", "key-b" }, auth);
    }

    [Fact]
    public async Task RateLimited_SkipsSameResolvedModelWithSameKey()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls += 1;
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.TooManyRequests,
                "rate_limit_exceeded",
                "rate limited"));
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:RetryCount"] = "0",
            ["Ai:Models:GeneralChat"] = "shared-chat-model",
            ["Ai:Providers:0:Provider"] = "slot-a",
            ["Ai:Providers:0:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:0:ApiKey"] = "shared-key",
            ["Ai:Providers:0:Model"] = "default-a",
            ["Ai:Providers:1:Provider"] = "slot-b",
            ["Ai:Providers:1:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:Providers:1:ApiKey"] = "shared-key",
            ["Ai:Providers:1:Model"] = "default-b"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.GeneralChat,
            [new ZaloAiChatMessage("user", "hello")])) ;

        Assert.False(result.Success);
        Assert.Equal(ZaloAiFailureKind.RateLimited, result.FailureKind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletionParser_AcceptsArrayTextContent()
    {
        var handler = new StubHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"intent\\\":\\\"GeneralChat\\\"}\"}]}}]}",
                Encoding.UTF8,
                "application/json")
        }));
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://openrouter.test/chat",
            ["Ai:ApiKey"] = "key-a",
            ["Ai:Model"] = "liquid/lfm-2.5-2.6b:free"
        });
        var gateway = CreateGateway(handler, configuration);

        var result = await gateway.CompleteAsync(new ZaloAiCompletionRequest(
            ZaloAiWorkload.IntentClassification,
            [new ZaloAiChatMessage("user", "classify")])) ;

        Assert.True(result.Success);
        Assert.Equal("{\"intent\":\"GeneralChat\"}", result.Content);
    }

    private static OpenAiCompatibleZaloAiGateway CreateGateway(
        HttpMessageHandler handler,
        IConfiguration configuration)
    {
        OpenAiCompatibleZaloAiGateway.ResetProviderHealthForTests();
        return new OpenAiCompatibleZaloAiGateway(
            new HttpClient(handler),
            configuration,
            NullLogger<OpenAiCompatibleZaloAiGateway>.Instance);
    }

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
