using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientSocialTargetResolverTests
{
    [Fact]
    public async Task Recent_proactive_bot_message_plus_pronoun_resolves_bot_mild_complaint_with_grounded_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new StubGateway(
            "{\"subject\":\"Bot\",\"stance\":\"MildComplaint\",\"confidence\":0.97,\"reason\":\"pronoun_continues_bot_tease\",\"evidenceMessageIds\":[\"bot-proactive-1\",\"human-1\"]}");
        var resolver = new ZaloAmbientSocialTargetResolver(gateway, NullLogger.Instance);
        var recent = new ZaloAmbientSocialContextMessage[]
        {
            new("bot-account", "Bott", "😄 @Nguyễn Ánh biết sơ nguồn gốc hội luôn cho vui...", true,
                "bot-proactive-1", now.AddSeconds(-54), "proactive:member_integration:group-origins"),
            new("user-vivian", "Vivian", "Nhỏ Bot nay nch nhiều dữ á ta", false,
                "human-1", now.AddSeconds(-24), "group_message")
        };

        var decision = await resolver.ResolveAsync(
            Message("human-2", "user-vivian", "Vivian", "Mở tn lên ko thấy người nói mà toàn ẻm nói hông hà", now),
            recent,
            CancellationToken.None);

        Assert.Equal(ZaloAmbientSocialSubject.Bot, decision.Subject);
        Assert.Equal(ZaloAmbientSocialStance.MildComplaint, decision.Stance);
        Assert.Equal(.97, decision.Confidence, 2);
        Assert.True(decision.AllowsImmediateBotBanter);
        Assert.Equal(["bot-proactive-1", "human-1"], decision.EvidenceMessageIds);
        Assert.Single(gateway.Requests);
        Assert.Equal(ZaloAiWorkload.StructuredExtraction, gateway.Requests[0].Workload);
        using var requestPayload = JsonDocument.Parse(gateway.Requests[0].Messages[1].Content);
        Assert.Equal(
            "Mở tn lên ko thấy người nói mà toàn ẻm nói hông hà",
            requestPayload.RootElement.GetProperty("CurrentMessage").GetProperty("Content").GetString());
        Assert.Contains(
            requestPayload.RootElement.GetProperty("RecentMessages").EnumerateArray(),
            item => item.GetProperty("Source").GetString() == "proactive:member_integration:group-origins");
    }

    [Fact]
    public async Task High_confidence_with_fake_evidence_is_downgraded_below_reply_threshold()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new StubGateway(
            "{\"subject\":\"Bot\",\"stance\":\"PlayfulTease\",\"confidence\":0.99,\"reason\":\"guess\",\"evidenceMessageIds\":[\"made-up-id\"]}");
        var resolver = new ZaloAmbientSocialTargetResolver(gateway, NullLogger.Instance);
        var recent = new[]
        {
            new ZaloAmbientSocialContextMessage(
                "bot-account", "Bott", "Bot vừa nói gì đó", true,
                "real-bot-id", now.AddSeconds(-20), "proactive:community:tip")
        };

        var decision = await resolver.ResolveAsync(
            Message("human-3", "user-1", "User", "lại nữa hả trời", now),
            recent,
            CancellationToken.None);

        Assert.Equal(.89, decision.Confidence, 2);
        Assert.Empty(decision.EvidenceMessageIds);
        Assert.False(decision.AllowsImmediateBotBanter);
    }

    [Fact]
    public async Task No_recent_bot_context_skips_gateway_and_fails_closed()
    {
        var now = DateTimeOffset.UtcNow;
        var gateway = new StubGateway(
            "{\"subject\":\"Bot\",\"stance\":\"PlayfulTease\",\"confidence\":0.99,\"reason\":\"should_not_run\",\"evidenceMessageIds\":[\"human-1\"]}");
        var resolver = new ZaloAmbientSocialTargetResolver(gateway, NullLogger.Instance);
        var recent = new[]
        {
            new ZaloAmbientSocialContextMessage(
                "user-2", "Nam", "nay đánh mấy giờ", false,
                "human-1", now.AddSeconds(-20), "group_message")
        };

        var decision = await resolver.ResolveAsync(
            Message("human-2", "user-1", "User", "nó nói nhiều ghê", now),
            recent,
            CancellationToken.None);

        Assert.Equal(ZaloAmbientSocialSubject.Unknown, decision.Subject);
        Assert.False(decision.AllowsImmediateBotBanter);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task No_tag_tease_after_durable_proactive_message_replies_without_webhook_echo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.AddObservedAsync(
            "human-1",
            "user-vivian",
            "Vivian",
            "Nhỏ Bot nay nch nhiều dữ á ta",
            now.AddSeconds(-24));
        await fixture.AddProactiveAsync(
            "bot-proactive-1",
            "😄 @Nguyễn Ánh biết sơ nguồn gốc hội luôn cho vui: Nick Tran lập group này.",
            now.AddSeconds(-54));

        var handler = new SequenceAiHandler(
            "{\"subject\":\"Bot\",\"stance\":\"MildComplaint\",\"confidence\":0.97,\"reason\":\"same_speaker_pronoun_continuation\",\"evidenceMessageIds\":[\"bot-proactive-1\",\"human-1\"]}",
            "Ừ tui tăng ca hơi sung =)) mà bà audit airtime tui kỹ dữ ha.");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("human-2", "user-vivian", "Vivian", "Mở tn lên ko thấy người nói mà toàn ẻm nói hông hà", now),
            UnaddressedDecision("human-1", "human-2"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("contextual_bot_mildcomplaint_ai", result!.AddressReason);
        Assert.Equal(97, result.EffectiveScore);
        Assert.Equal(["bot-proactive-1", "human-1"], result.EvidenceMessageIds);
        Assert.Equal(2, handler.CallCount);
        using var outerRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        var innerJson = outerRequest.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.NotNull(innerJson);
        using var innerRequest = JsonDocument.Parse(innerJson!);
        var recentMessages = innerRequest.RootElement.GetProperty("RecentMessages").EnumerateArray().ToArray();
        Assert.Contains(recentMessages, item => item.GetProperty("MessageId").GetString() == "bot-proactive-1");
        Assert.Contains(recentMessages, item => item.GetProperty("Source").GetString() == "proactive:member_integration:member_integration_group-origins");
        Assert.Contains(recentMessages, item => item.GetProperty("Content").GetString() == "Nhỏ Bot nay nch nhiều dữ á ta");
    }

    [Fact]
    public async Task Soft_textual_bot_reference_without_tag_is_classified_before_reply()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var handler = new SequenceAiHandler(
            "{\"subject\":\"Bot\",\"stance\":\"MildComplaint\",\"confidence\":0.96,\"reason\":\"soft_bot_complaint\",\"evidenceMessageIds\":[\"human-soft-1\"]}",
            "Rồi rồi tui bớt tăng ca =)) mà bà để ý tui dữ ha.");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("human-soft-1", "user-vivian", "Vivian", "Dạo nayd con bot nó nói nhiều dữ luôn", now),
            UnaddressedDecision("human-soft-1"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("contextual_bot_mildcomplaint_ai", result!.AddressReason);
        Assert.Equal(96, result.EffectiveScore);
        Assert.Equal(["human-soft-1"], result.EvidenceMessageIds);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Serious_business_bot_reference_without_tag_does_not_get_social_reply()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var handler = new SequenceAiHandler(
            "{\"subject\":\"Bot\",\"stance\":\"BusinessDiscussion\",\"confidence\":0.99,\"reason\":\"bot_role_in_team_balance\",\"evidenceMessageIds\":[\"human-business-current\"]}");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message(
                "human-business-current",
                "user-vivian",
                "Vivian",
                "BOT ra đời trong nhóm mình là để hỗ trợ cho sự phân chia công bằng",
                now),
            UnaddressedDecision("human-business-current"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Contextual_business_discussion_about_bot_does_not_open_roast_lane()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.AddObservedAsync(
            "human-business-1",
            "user-vivian",
            "Vivian",
            "BOT ra đời để hỗ trợ phân chia công bằng",
            now.AddSeconds(-20));
        await fixture.AddProactiveAsync(
            "bot-proactive-2",
            "@Anhkhoii check poll lại giúp nha.",
            now.AddSeconds(-40));

        var handler = new SequenceAiHandler(
            "{\"subject\":\"Bot\",\"stance\":\"BusinessDiscussion\",\"confidence\":0.99,\"reason\":\"serious_team_balance_discussion\",\"evidenceMessageIds\":[\"human-business-1\"]}");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("human-business-2", "user-vivian", "Vivian", "Cái đó cứ để nó cân team như quy trình cũ thôi", now),
            UnaddressedDecision("human-business-1", "human-business-2"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    private static IConfiguration EnabledConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();

    private static ZaloAmbientSocialPilotSettings EnabledSettings() =>
        new(true, false, 90, 8, 180);

    private static ZaloAmbientParticipationDecision UnaddressedDecision(params string[] ids) => new(
        WouldReply: false,
        Score: 5,
        Kind: ZaloAmbientParticipationKind.None,
        Intent: ZaloBotIntent.Unknown.ToString(),
        IntentConfidence: .05,
        Signals: ["quiet_group"],
        Situation: new ZaloAmbientGroupSituation(
            RecentMessageCount: ids.Length,
            RecentTwoMinuteMessageCount: ids.Length,
            DistinctParticipantCount: 1,
            RecentBotMessageCount: 0,
            LastBotMessageAt: null,
            RecentMessageIds: ids));

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content,
        DateTimeOffset sentAt) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: senderId,
        senderName: senderName,
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: sentAt.ToUnixTimeMilliseconds());

    private sealed class StubGateway(params string[] answers) : IZaloAiGateway
    {
        private readonly Queue<string> queue = new(answers);
        public bool IsConfigured => true;
        public List<ZaloAiCompletionRequest> Requests { get; } = [];

        public Task<ZaloAiCompletionResult> CompleteAsync(
            ZaloAiCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (queue.Count == 0) throw new InvalidOperationException("Unexpected AI call.");
            return Task.FromResult(new ZaloAiCompletionResult(
                true,
                queue.Dequeue(),
                ZaloAiFailureKind.None,
                "test",
                "test-model",
                1,
                200,
                TimeSpan.Zero,
                false));
        }
    }

    private sealed class SequenceAiHandler(params string[] answers) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(answers);
        public int CallCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (queue.Count == 0) throw new InvalidOperationException("Unexpected extra AI call.");
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = queue.Dequeue() } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var admin = new User
            {
                Id = "admin-contextual-social",
                DisplayName = "Admin",
                Email = $"contextual-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Bott",
                EncryptedCredentials = "test"
            });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async Task AddObservedAsync(
            string messageId,
            string senderId,
            string senderName,
            string content,
            DateTimeOffset sentAt)
        {
            Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = messageId,
                SenderId = senderId,
                SenderName = senderName,
                Content = content,
                IsFromBot = false,
                SentAt = sentAt,
                ReceivedAt = sentAt
            });
            await Db.SaveChangesAsync();
        }

        public Task AddProactiveAsync(string providerMessageId, string text, DateTimeOffset sentAt) =>
            new ZaloProactiveMessageStore(Db).RecordAsync(new ZaloProactiveMessageHistoryData(
                Guid.NewGuid().ToString("n"),
                "conn-1",
                "g1",
                sentAt.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd"),
                ZaloProactiveLane.MemberIntegration,
                "member_integration_group-origins",
                "member-integration:group-origins",
                "member-new",
                "Nguyễn Ánh",
                text,
                sentAt,
                providerMessageId,
                $"test:{providerMessageId}"));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
