using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiProviderFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", AiProviderFailureKind.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, "{}", AiProviderFailureKind.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}", AiProviderFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"insufficient_quota\"}}", AiProviderFailureKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.RequestTimeout, "{}", AiProviderFailureKind.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, "{}", AiProviderFailureKind.Timeout)]
    [InlineData(HttpStatusCode.NotFound, "{}", AiProviderFailureKind.ModelOrEndpointUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, "{}", AiProviderFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", AiProviderFailureKind.ProviderUnavailable)]
    public void Http_failures_are_classified_without_exposing_raw_body(
        HttpStatusCode statusCode,
        string body,
        AiProviderFailureKind expected)
    {
        var result = AiProviderFailure.FromHttp(statusCode, body);

        Assert.Equal(expected, result.Kind);
        Assert.DoesNotContain(body, result.ToUserMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Payment_required_is_treated_as_quota_exhaustion()
    {
        var result = AiProviderFailure.FromHttp((HttpStatusCode)402, "{\"error\":{\"message\":\"credits exhausted\"}}");

        Assert.Equal(AiProviderFailureKind.QuotaExceeded, result.Kind);
        Assert.False(result.Retryable);
        Assert.Contains("hết hạn mức", result.ToUserMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_error_code_is_sanitized_before_logging()
    {
        var result = AiProviderFailure.FromHttp(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"rate_limit<script>secret</script>\"}}");

        Assert.Equal("rate_limitscriptsecretscript", result.ProviderCode);
        Assert.DoesNotContain("<", result.ProviderCode);
        Assert.DoesNotContain(">", result.ProviderCode);
    }

    [Fact]
    public void Network_exception_is_retryable_but_not_called_timeout()
    {
        var result = AiProviderFailure.FromException(
            new HttpRequestException("DNS lookup failed for host with secret details"),
            CancellationToken.None);

        Assert.Equal(AiProviderFailureKind.NetworkFailure, result.Kind);
        Assert.True(result.Retryable);
        Assert.DoesNotContain("DNS", result.ToUserMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Task_cancellation_without_caller_cancel_is_timeout()
    {
        var result = AiProviderFailure.FromException(new TaskCanceledException(), CancellationToken.None);

        Assert.Equal(AiProviderFailureKind.Timeout, result.Kind);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void Caller_cancellation_is_not_misreported_as_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = AiProviderFailure.FromException(new TaskCanceledException(), cts.Token);

        Assert.Equal(AiProviderFailureKind.Cancelled, result.Kind);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task General_chat_explains_quota_instead_of_generic_connection_error()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"insufficient_quota\",\"message\":\"secret provider detail\"}}",
                Encoding.UTF8,
                "application/json")
        });

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("hết hạn mức", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dữ liệu hệ thống", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vẫn dùng được", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret provider detail", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("không kết nối được dịch vụ AI", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_chat_explains_rate_limit_separately_from_quota()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"rate_limit_exceeded\"}}",
                Encoding.UTF8,
                "application/json")
        });

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("giới hạn số lượt gọi", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hết hạn mức", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_chat_explains_authentication_failure_without_leaking_provider_body()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"api-key=SUPER-SECRET\"}}",
                Encoding.UTF8,
                "application/json")
        });

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("xác thực", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER-SECRET", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task General_chat_explains_provider_outage()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("upstream unavailable")
        });

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("Nhà cung cấp AI", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tạm lỗi", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_chat_explains_invalid_success_payload_as_response_contract_failure()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
        });

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("không đúng định dạng", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_chat_explains_network_failure()
    {
        var service = CreateService(_ => throw new HttpRequestException("dns secret.internal.example"));

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("không kết nối được tới nhà cung cấp AI", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.internal.example", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_chat_explains_timeout()
    {
        var service = CreateService(_ => throw new TaskCanceledException("transport timed out"));

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("phản hồi quá lâu", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Structured_ai_failure_still_returns_null_for_deterministic_fallback()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"code\":\"insufficient_quota\"}}")
        });

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "nhắc T6",
            "Long",
            ZaloBotIntent.ScheduleReminder,
            "Đã lên lịch cho T6."));

        Assert.Null(result);
    }

    [Fact]
    public async Task Missing_ai_configuration_reports_system_configuration_not_missing_session_data()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new AiAssistantService(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            configuration,
            NullLogger<AiAssistantService>.Instance);

        var answer = await service.AnswerAsync(CreateContext());

        Assert.Contains("chưa được cấu hình đầy đủ", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tên hoặc ngày của trận", answer, StringComparison.OrdinalIgnoreCase);
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

    private static ZaloAiContext CreateContext() => new(
        "group-1",
        new ZaloAiSender("sender-1", "Long"),
        "bot còn đó không?",
        [],
        [],
        null,
        [],
        new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.FromHours(7)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
