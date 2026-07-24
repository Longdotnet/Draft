using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiAssistantServiceTests
{
    [Fact]
    public async Task Reminder_extraction_accepts_null_optional_json_values()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"kind\":\"Schedule\",\"delayMinutes\":null,\"repeats\":false,\"localTime\":\"17:00\",\"explicitLocalDate\":null,\"useSessionDate\":true,\"customMessage\":\"remember water\",\"audience\":\"All\",\"onlyIfMissingSlots\":false,\"sessionReferences\":[\"T4\"]}"}}]}""");

        var result = await service.ParseReminderCommandAsync(new ZaloNaturalReminderContext(
            "nhắc 5h chiều T4 nhớ mang nước",
            "Thanh Long",
            [],
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7))));

        Assert.NotNull(result);
        Assert.Equal(new TimeOnly(17, 0), result!.LocalTime);
        Assert.Null(result.DelayMinutes);
        Assert.Equal(["T4"], result.SessionReferences);
        Assert.False(result.StopWhenFull);
    }

    [Fact]
    public async Task Reminder_extraction_reads_stop_when_full()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"kind\":\"Schedule\",\"delayMinutes\":360,\"repeats\":true,\"localTime\":null,\"explicitLocalDate\":null,\"useSessionDate\":false,\"customMessage\":\"Mọi người vào vote giúp nhé!\",\"audience\":\"All\",\"onlyIfMissingSlots\":true,\"sessionReferences\":[\"T6\"],\"stopWhenFull\":true}"}}]}""");

        var result = await service.ParseReminderCommandAsync(new ZaloNaturalReminderContext(
            "cứ 6h nhắc vote T6, đủ thì thôi",
            "Thanh Long",
            [],
            new DateTimeOffset(2026, 7, 14, 20, 43, 0, TimeSpan.FromHours(7))));

        Assert.NotNull(result);
        Assert.True(result!.StopWhenFull);
        Assert.True(result.OnlyIfMissingSlots);
    }

    [Fact]
    public async Task Share_extraction_uses_partner_count_when_ai_returns_null_count()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"anchor\":\"Nick Tran\",\"partners\":[\"An\",\"Bình\"],\"requestedPartnerCount\":null}"}}]}""");

        var result = await service.ParseShareSlotCommandAsync(new ZaloNaturalShareContext(
            "Nick Tran xin +2 cho An và Bình",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.Equal(2, result!.RequestedPartnerCount);
        Assert.Equal(["An", "Bình"], result.Partners);
    }

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

    [Fact]
    public async Task Protected_business_block_can_be_styled_without_changing_names_or_scores()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Chốt phương án này là đẹp nha:\n[[VD_FACT_0]]\nBạn xác nhận giúp mình."}}]}""");
        const string facts = "- Vinh / Vivian: Team C → Team B (2 điểm)\n- Nick Tran: Team B → Team C (3 điểm)";

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "cân bằng team 2 và team 3",
            "Thanh Long",
            ZaloBotIntent.RebalanceTeams,
            $"Mình có phương án:\n{facts}\nBạn xác nhận giúp mình.",
            [facts]));

        Assert.NotNull(result);
        Assert.Contains("Vinh / Vivian", result);
        Assert.Contains("Nick Tran", result);
        Assert.Contains("Team C → Team B (2 điểm)", result);
    }

    [Fact]
    public async Task Protected_business_block_rewrite_is_rejected_when_placeholder_is_changed()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Đã cân bằng Vinh qua Team B rồi nha."}}]}""");
        const string facts = "- Vinh / Vivian: Team C → Team B (2 điểm)";

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "cân bằng team 2 và team 3",
            "Thanh Long",
            ZaloBotIntent.RebalanceTeams,
            $"Phương án:\n{facts}",
            [facts]));

        Assert.Null(result);
    }

    [Fact]
    public async Task Member_activity_classifier_keeps_four_months_as_calendar_months()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"intent\":\"ListMembersWithoutRecentVote\",\"confidence\":0.97,\"memberReference\":null,\"timeRange\":{\"kind\":\"PreviousCalendarMonths\",\"amount\":4,\"startDate\":null,\"endDate\":null},\"limit\":10,\"needsClarification\":false}"}}]}""");

        var result = await service.ClassifyMemberActivityAsync(new ZaloMemberActivityClassifierContext(
            "ai 4 tháng rồi chưa vote?",
            "sender",
            "Thanh Long",
            new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(7))));

        Assert.NotNull(result);
        Assert.Equal(ZaloBotIntent.ListMembersWithoutRecentVote, result!.Intent);
        Assert.Equal(ZaloActivityTimeRangeKind.PreviousCalendarMonths, result.TimeRange?.Kind);
        Assert.Equal(4, result.TimeRange?.Amount);
    }

    [Fact]
    public async Task Invalid_member_activity_range_is_rejected()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"intent\":\"AnalyzeMemberVoteActivity\",\"confidence\":0.95,\"memberReference\":\"Long\",\"timeRange\":{\"kind\":\"ExplicitRange\",\"amount\":null,\"startDate\":\"2026-07-20\",\"endDate\":\"2026-03-01\"},\"limit\":null,\"needsClarification\":false}"}}]}""");

        var result = await service.ClassifyMemberActivityAsync(new ZaloMemberActivityClassifierContext(
            "phân tích vote của Long",
            "sender",
            "Thanh Long",
            DateTimeOffset.UtcNow));

        Assert.Null(result);
    }

    [Fact]
    public async Task Malformed_member_activity_json_falls_back_to_null()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"not-json"}}]}""");

        var result = await service.ClassifyMemberActivityAsync(new ZaloMemberActivityClassifierContext(
            "ai lâu rồi không vote?",
            "sender",
            "Thanh Long",
            DateTimeOffset.UtcNow));

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
