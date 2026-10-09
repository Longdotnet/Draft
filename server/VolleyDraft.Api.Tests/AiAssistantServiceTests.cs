using System.Net;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData("{\"relation\":\"APART\",\"speechAct\":\"REQUEST\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.99,\"needsClarification\":false}")]
    [InlineData("{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"proposal\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.99,\"needsClarification\":false}")]
    public async Task Team_preference_unknown_operation_or_speech_act_fails_closed(string semanticJson)
    {
        var escaped = semanticJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var service = CreateService(
            HttpStatusCode.OK,
            $"{{\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}}}}]}}");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "오늘 tui don't wanna be same team với To An",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.True(result!.NeedsClarification);
        Assert.True(
            result.Operation == ZaloTeamRelationshipOperation.Unknown ||
            result.SpeechAct == ZaloTeamRelationshipSpeechAct.Unknown);
    }

    [Fact]
    public async Task Team_preference_code_switch_apart_request_keeps_structured_semantics()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"REQUEST\",\"players\":[\"Thanh Long\",\"To An\"],\"sessionReference\":\"T6\",\"confidence\":0.97,\"needsClarification\":false,\"clarificationQuestion\":null}"}}]}""");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "오늘 tui don't wanna be same team với To An T6",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.Equal(ZaloTeamRelationshipKind.Apart, result!.Relation);
        Assert.Equal(ZaloTeamRelationshipOperation.Set, result.Operation);
        Assert.Equal(ZaloTeamRelationshipSpeechAct.Request, result.SpeechAct);
        Assert.False(result.NeedsClarification);
        Assert.Equal(.97, result.Confidence, 2);
    }

    [Fact]
    public async Task Team_preference_semantic_output_drops_hallucinated_player_and_fails_closed()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"REQUEST\",\"players\":[\"Thanh Long\",\"Nick Tran\"],\"confidence\":0.99,\"needsClarification\":false}"}}]}""");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "tui muốn khác team với To An",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.DoesNotContain("Nick Tran", result!.PlayerReferences);
        Assert.True(result.NeedsClarification);
        Assert.Single(result.PlayerReferences);
    }

    [Fact]
    public async Task Team_preference_semantic_output_drops_hallucinated_session_reference()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"REQUEST\",\"players\":[\"Thanh Long\",\"To An\"],\"sessionReference\":\"CN\",\"confidence\":0.99,\"needsClarification\":false}"}}]}""");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "tui không muốn chung team với To An T6",
            "Thanh Long",
            [],
            [new ZaloAiSessionReference("cn", "CN", null)]));

        Assert.NotNull(result);
        Assert.Null(result!.SessionReference);
    }

    [Fact]
    public async Task Team_preference_query_preserves_non_mutating_question_semantics()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"operation\":\"QUERY\",\"relation\":\"APART\",\"speechAct\":\"QUESTION\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.95,\"needsClarification\":false}"}}]}""");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "Can I avoid To An?",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.Equal(ZaloTeamRelationshipOperation.Query, result!.Operation);
        Assert.Equal(ZaloTeamRelationshipSpeechAct.Question, result.SpeechAct);
        Assert.Equal(ZaloTeamRelationshipKind.Apart, result.Relation);
    }

    [Theory]
    [InlineData("{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"REQUEST\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.55,\"needsClarification\":false}")]
    [InlineData("{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"SUGGESTION\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.95,\"needsClarification\":false}")]
    [InlineData("{\"operation\":\"SET\",\"relation\":\"APART\",\"speechAct\":\"UNCERTAIN\",\"players\":[\"Thanh Long\",\"To An\"],\"confidence\":0.95,\"needsClarification\":false}")]
    public async Task Team_preference_low_confidence_suggestion_or_uncertain_fails_closed(string semanticJson)
    {
        var escaped = semanticJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var service = CreateService(
            HttpStatusCode.OK,
            $"{{\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}}}}]}}");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            "tui muốn khác team với To An",
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.True(result!.NeedsClarification);
    }

    [Fact]
    public async Task Team_preference_semantic_output_with_more_than_twelve_targets_fails_closed()
    {
        var players = Enumerable.Range(1, 13).Select(index => $"Player {index}").ToArray();
        var question = $"xếp {string.Join(", ", players)} chung team";
        var semanticJson = JsonSerializer.Serialize(new
        {
            operation = "SET",
            relation = "TOGETHER",
            speechAct = "REQUEST",
            players,
            confidence = 0.99,
            needsClarification = false
        });
        var escaped = semanticJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var service = CreateService(
            HttpStatusCode.OK,
            $"{{\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}}}}]}}");

        var result = await service.ParseTeamPreferenceCommandAsync(new ZaloNaturalTeamPreferenceContext(
            question,
            "Thanh Long",
            [],
            []));

        Assert.NotNull(result);
        Assert.True(result!.NeedsClarification);
        Assert.Equal(12, result.PlayerReferences.Count);
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

    [Fact]
    public async Task General_answer_rejects_internal_reasoning_leak()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"The user is asking to see more. I should continue with members 11-20, but I need the previous context."}}]}""");

        var result = await service.AnswerAsync(CreateGeneralContext("xem thêm"));

        Assert.DoesNotContain("The user", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I should", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Mình chưa hiểu chắc yêu cầu này. Bạn nói lại ngắn gọn hoặc gõ help nhé.", result);
    }

    [Fact]
    public async Task General_answer_keeps_normal_vietnamese_response()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Chào Thanh Long, mình vẫn ở đây nè 😊"}}]}""");

        var result = await service.AnswerAsync(CreateGeneralContext("bot còn đó không?"));

        Assert.Equal("Chào Thanh Long, mình vẫn ở đây nè 😊", result);
    }

    [Fact]
    public async Task General_answer_rejects_truncated_provider_output_instead_of_sending_partial_text()
    {
        var service = CreateService([
            """{"choices":[{"message":{"content":"Haha, love the energy! 🫶 I can"},"finish_reason":"length"}]}""",
            """{"choices":[{"message":{"content":"en"},"finish_reason":"stop"}]}"""
        ]);

        var result = await service.AnswerAsync(CreateGeneralContext("Can you keep us on the same team?"));

        Assert.Equal("I couldn't complete that reply. Please send it again in a shorter sentence.", result);
        Assert.DoesNotContain("I can", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factual_rewrite_rejects_truncated_provider_output_and_keeps_caller_fallback_available()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Got it, [[VD_FACT_0]] and I"},"finish_reason":"max_tokens"}]}""");

        var result = await service.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
            "Please keep us together",
            "Thanh Long",
            ZaloBotIntent.SessionSchedule,
            "Recorded Thanh Long and To An for Friday 9/10.",
            ["Thanh Long and To An"]));

        Assert.Null(result);
    }

    [Fact]
    public async Task General_answer_prompt_follows_english_message_language()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Sure, I can help with that."},"finish_reason":"stop"}]}""",
            out var handler);

        var result = await service.AnswerAsync(CreateGeneralContext("Can you help me today?"));

        Assert.Equal("Sure, I can help with that.", result);
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        var systemPrompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("Reply in the same natural language as Question", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("not from a keyword list", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task General_answer_prompt_follows_korean_message_language()
    {
        var service = CreateService(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"네, 도와드릴게요."},"finish_reason":"stop"}]}""",
            out var handler);

        var result = await service.AnswerAsync(CreateGeneralContext("오늘 도와줄 수 있어?"));

        Assert.Equal("네, 도와드릴게요.", result);
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        var systemPrompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("Reply in the same natural language as Question", systemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hello bot", "en", "English")]
    [InlineData("good evening, how is everyone?", "en", "English")]
    [InlineData("chao bot hom nay khoe khong", "vi", "Vietnamese")]
    [InlineData("tui muon chung team voi To An", "vi", "Vietnamese")]
    public async Task Ambiguous_latin_script_language_is_classified_semantically(
        string question,
        string providerLanguage,
        string expected)
    {
        var service = CreateService(HttpStatusCode.OK,
            $$"""{"choices":[{"message":{"content":"{{providerLanguage}}"},"finish_reason":"stop"}]}""");

        var result = await service.ResolveReplyLanguageAsync(question);

        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("안녕하세요 bot", "Korean")]
    [InlineData("đừng xếp tui chung team", "Vietnamese")]
    public async Task Unicode_script_language_does_not_need_ai_classification(string question, string expected)
    {
        var service = CreateService(HttpStatusCode.InternalServerError, "{}");

        var result = await service.ResolveReplyLanguageAsync(question);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public async Task Explicit_english_team_preference_keeps_english_when_ai_provider_is_unavailable()
    {
        var service = CreateService(HttpStatusCode.InternalServerError, "{}");

        var language = await service.ResolveReplyLanguageAsync("I'd like to be on the same team as @To An today");

        Assert.Equal("English", language.ToString());
    }

    private static ZaloAiContext CreateGeneralContext(string question) =>
        new(
            "group-1",
            new ZaloAiSender("sender-1", "Thanh Long"),
            question,
            [],
            [],
            null,
            [],
            new DateTimeOffset(2026, 7, 25, 15, 0, 0, TimeSpan.FromHours(7)));

    private static AiAssistantService CreateService(HttpStatusCode statusCode, string responseBody) =>
        CreateService(statusCode, responseBody, out _);

    private static AiAssistantService CreateService(IReadOnlyList<string> responseBodies)
    {
        var index = 0;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        var handler = new StubHandler(_ =>
        {
            var body = responseBodies[Math.Min(index, responseBodies.Count - 1)];
            index += 1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        return new AiAssistantService(
            new HttpClient(handler),
            configuration,
            NullLogger<AiAssistantService>.Instance);
    }

    private static AiAssistantService CreateService(
        HttpStatusCode statusCode,
        string responseBody,
        out StubHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
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
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
