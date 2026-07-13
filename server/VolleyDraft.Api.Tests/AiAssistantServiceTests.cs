using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiAssistantServiceTests
{
    [Fact]
    public async Task Factual_answer_can_be_rewritten_without_losing_protected_facts()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Ok nha, mình đã hẹn riêng Thứ 6 17/7: sau 8 giờ sẽ kiểm tra, rồi cứ mỗi 8 giờ kiểm tra lại."}}]}""");

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "cứ 8 tiếng nhắc thứ 6 nha",
            "Thanh Long",
            ZaloBotIntent.ScheduleReminder,
            "Đã lên lịch riêng cho Thứ 6 17/7: lần đầu sau 8 giờ, sau đó lặp mỗi 8 giờ."));

        Assert.NotNull(result);
        Assert.Contains("17/7", result);
        Assert.Contains("8", result);
    }

    [Fact]
    public async Task Rewrite_is_rejected_when_ai_drops_numbers_or_dates()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Ok nha, mình đã lên lịch rồi."}}]}""");

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "cứ 8 tiếng nhắc thứ 6 nha",
            "Thanh Long",
            ZaloBotIntent.ScheduleReminder,
            "Đã lên lịch riêng cho Thứ 6 17/7: lần đầu sau 8 giờ."));

        Assert.Null(result);
    }

    [Fact]
    public async Task Provider_failure_returns_null_so_caller_can_use_fallback()
    {
        var service = CreateService(HttpStatusCode.TooManyRequests, "{\"error\":\"quota\"}");

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "nhắc thứ 6",
            "Thanh Long",
            ZaloBotIntent.ScheduleReminder,
            "Đã lên lịch cho Thứ 6 sau 8 giờ."));

        Assert.Null(result);
    }

    [Fact]
    public async Task Rewrite_is_rejected_when_confirmation_command_changes()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Đội hình sẽ thay đổi, bạn xác nhận giúp mình nhé."}}]}""");

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "draft luôn đi",
            "Thanh Long",
            ZaloBotIntent.AutoDraft,
            "Đội hình sẽ thay đổi. Gõ @bot xác nhận draft để chạy."));

        Assert.Null(result);
    }

    private static AiAssistantService CreateService(HttpStatusCode statusCode, string responseBody)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        return new AiAssistantService(
            new HttpClient(handler),
            configuration,
            NullLogger<AiAssistantService>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
