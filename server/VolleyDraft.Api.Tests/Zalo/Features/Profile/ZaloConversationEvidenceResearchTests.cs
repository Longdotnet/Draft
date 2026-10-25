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
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationEvidenceResearchTests
{
    [Fact]
    public void Historical_evidence_question_routes_before_team_preference()
    {
        var decision = ZaloBotIntelligence.ClassifyDeterministically(
            "bot ơi cho tui hỏi Nguyễn Hoàng Vũ trước đó có nói xếp team để đánh giải không?");

        Assert.Equal(ZaloBotIntent.SearchMemberConversationEvidence, decision.Intent);
    }

    [Theory]
    [InlineData("hồi bữa @Vũ có bảo muốn gom team đi giải đúng không?")]
    [InlineData("tìm lại đoạn @Vũ nói về 4 người đánh giải")]
    [InlineData("check lại @Vũ từng nhắc chuyện đánh giải chưa")]
    public void Natural_historical_phrasings_route_to_evidence_research(string question)
    {
        Assert.Equal(
            ZaloBotIntent.SearchMemberConversationEvidence,
            ZaloBotIntelligence.ClassifyDeterministically(question).Intent);
    }

    [Fact]
    public async Task Ordinary_member_can_research_tagged_member_beyond_recent_20_and_uid_wins_duplicate_name()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.AddRange(
            Member("target-real", "Nguyễn Hoàng Vũ", now),
            Member("target-decoy", "Nguyễn Hoàng Vũ", now),
            Member("ordinary-sender", "Người hỏi", now));
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "evidence-1",
            SenderId = "target-real",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "@Thanh Long sr a nay e tính chung để tập bài sắp tới 4ng đánh giải á a",
            SentAt = now.AddDays(-7),
            ReceivedAt = now.AddDays(-7)
        });
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "decoy-1",
            SenderId = "target-decoy",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "tui mới là người nói xếp team để đánh giải",
            SentAt = now.AddDays(-6),
            ReceivedAt = now.AddDays(-6)
        });
        for (var index = 0; index < 25; index++)
        {
            fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                MessageId = $"noise-{index}",
                SenderId = "target-real",
                SenderName = "Nguyễn Hoàng Vũ",
                Content = $"tin mới không liên quan số {index}",
                SentAt = now.AddMinutes(-index),
                ReceivedAt = now.AddMinutes(-index)
            });
        }
        await fixture.Db.SaveChangesAsync();

        var quoteEvent = new ZaloIncomingMessageEvent(
            "bot-a",
            "bot-a",
            "group-1",
            "evidence-1",
            "target-real",
            "Nguyễn Hoàng Vũ",
            "@Thanh Long sr a nay e tính chung để tập bài sắp tới 4ng đánh giải á a",
            [],
            false,
            now.AddDays(-7).ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "quoted-1",
                "leader-1",
                "Thanh Long",
                "@Nguyễn Hoàng Vũ dã Duy Nam, r còn đòi Duy Hoang với Sin nữa",
                "chat",
                now.AddDays(-7).AddMinutes(-1).ToUnixTimeMilliseconds(),
                null));
        await new ZaloMessageGraphStore(fixture.Db)
            .RememberIncomingQuoteAsync("connection-a", quoteEvent);

        var question = "@Bott cho tui hỏi @Nguyễn Hoàng Vũ trước đó bạn đó có nói xếp team để đánh giải không?";
        var targetLabel = "@Nguyễn Hoàng Vũ";
        var incoming = new ZaloIncomingMessageEvent(
            "bot-a",
            "bot-a",
            "group-1",
            "question-1",
            "ordinary-sender",
            "Người hỏi",
            question,
            [
                new ZaloBridgeMention("bot-a", 0, 5),
                new ZaloBridgeMention(
                    "target-real",
                    question.IndexOf(targetLabel, StringComparison.Ordinal),
                    targetLabel.Length)
            ],
            true,
            now.ToUnixTimeMilliseconds());

        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a",
            "group-1",
            incoming,
            question,
            false,
            CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SearchMemberConversationEvidence, answer.Intent);
        Assert.DoesNotContain("trưởng nhóm", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4ng đánh giải", answer.Text);
        Assert.DoesNotContain("tui mới là người nói xếp team", answer.Text);
        Assert.Contains("đang trả lời Thanh Long", answer.Text);
        Assert.Contains("Duy Nam", answer.Text);
    }

    [Fact]
    public async Task Evidence_search_is_scoped_to_current_group_and_returns_grounded_not_found()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.Add(Member("target", "Nguyễn Hoàng Vũ", now));
        fixture.Db.ZaloGroupMessages.AddRange(
            new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                MessageId = "local-unrelated",
                SenderId = "target",
                SenderName = "Nguyễn Hoàng Vũ",
                Content = "mai tui đi làm sớm",
                SentAt = now.AddDays(-1),
                ReceivedAt = now.AddDays(-1)
            },
            new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-other",
                MessageId = "other-group-match",
                SenderId = "target",
                SenderName = "Nguyễn Hoàng Vũ",
                Content = "tui muốn xếp team để đánh giải",
                SentAt = now.AddDays(-2),
                ReceivedAt = now.AddDays(-2)
            });
        await fixture.Db.SaveChangesAsync();

        var question = "@Bott trước đó @Nguyễn Hoàng Vũ có nói xếp team để đánh giải không?";
        var label = "@Nguyễn Hoàng Vũ";
        var incoming = new ZaloIncomingMessageEvent(
            "bot-a",
            "bot-a",
            "group-1",
            "question-2",
            "ordinary-sender",
            "Người hỏi",
            question,
            [
                new ZaloBridgeMention("bot-a", 0, 5),
                new ZaloBridgeMention(
                    "target",
                    question.IndexOf(label, StringComparison.Ordinal),
                    label.Length)
            ],
            true,
            now.ToUnixTimeMilliseconds());

        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a",
            "group-1",
            incoming,
            question,
            false,
            CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("chưa tìm thấy", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có nghĩa", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tui muốn xếp team để đánh giải", answer.Text);
    }

    [Fact]
    public async Task Evidence_redacts_secrets_before_display()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.Add(Member("target", "Nguyễn Hoàng Vũ", now));
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "secret-evidence",
            SenderId = "target",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "tui xếp team đánh giải, api_key=super-secret-token-value; " +
                      "Authorization: Bearer eyJabcDEF123456789.payloadSIG987654321.tokenSIGNATURE123; " +
                      "client_secret=my-client-secret; " +
                      "postgresql://draft_user:db-password@db.example.test/draft",
            SentAt = now.AddDays(-1),
            ReceivedAt = now.AddDays(-1)
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new ZaloConversationEvidenceResearchService(fixture.Db, null)
            .ResearchAsync(
                "connection-a",
                "group-1",
                "target",
                "Nguyễn Hoàng Vũ",
                "trước đó có nói xếp team đánh giải không?",
                null,
                false,
                CancellationToken.None);

        Assert.Contains("xếp team đánh giải", result.Text);
        Assert.Contains("[đã ẩn]", result.Text);
        Assert.DoesNotContain("super-secret-token-value", result.Text);
        Assert.DoesNotContain("payloadSIG987654321", result.Text);
        Assert.DoesNotContain("my-client-secret", result.Text);
        Assert.DoesNotContain("db-password", result.Text);
    }

    [Fact]
    public async Task Historical_bridge_quote_is_persisted_in_message_graph()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        var source = new BridgeHistoricalMessage(
            "historical-1",
            "target",
            "Nguyễn Hoàng Vũ",
            "tui tính chung để tập bài 4 người đánh giải",
            "chat",
            false,
            now.ToUnixTimeMilliseconds(),
            new BridgeMessageQuote(
                "historical-parent",
                "leader",
                "Thanh Long",
                "r còn đòi Duy Hoang với Sin nữa",
                "chat",
                now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                null));

        var graph = new ZaloMessageGraphStore(fixture.Db);
        await graph.RememberHistoricalQuoteAsync(
            "connection-a",
            "group-1",
            source,
            CancellationToken.None);
        var relation = await graph.LoadRelationAsync(
            "connection-a",
            "group-1",
            "historical-1",
            CancellationToken.None);

        Assert.NotNull(relation);
        Assert.Equal("ReplyTo", relation.RelationType);
        Assert.Equal("historical-parent", relation.ToMessageId);
        Assert.Equal("Thanh Long", relation.QuotedSenderName);
        Assert.Contains("Duy Hoang", relation.QuotedContentSnapshot);
    }

    [Fact]
    public async Task Quote_context_participates_in_ranking_when_target_reply_is_short()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.Add(Member("target", "Nguyễn Hoàng Vũ", now));
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "short-reply",
            SenderId = "target",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "đúng rồi",
            SentAt = now.AddDays(-5),
            ReceivedAt = now.AddDays(-5)
        });
        for (var index = 0; index < 25; index++)
        {
            fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                MessageId = $"quote-noise-{index}",
                SenderId = "target",
                SenderName = "Nguyễn Hoàng Vũ",
                Content = $"tin không liên quan {index}",
                SentAt = now.AddMinutes(-index),
                ReceivedAt = now.AddMinutes(-index)
            });
        }
        await fixture.Db.SaveChangesAsync();
        await new ZaloMessageGraphStore(fixture.Db).RememberIncomingQuoteAsync(
            "connection-a",
            new ZaloIncomingMessageEvent(
                "bot-a",
                "bot-a",
                "group-1",
                "short-reply",
                "target",
                "Nguyễn Hoàng Vũ",
                "đúng rồi",
                [],
                false,
                now.AddDays(-5).ToUnixTimeMilliseconds(),
                new ZaloBridgeMessageQuote(
                    "parent",
                    "leader",
                    "Thanh Long",
                    "xếp team để 4 người tập bài đánh giải nha",
                    "chat",
                    now.AddDays(-5).AddMinutes(-1).ToUnixTimeMilliseconds(),
                    null)));

        var question = "@Bott trước đó @Nguyễn Hoàng Vũ có nói về xếp team đánh giải không?";
        var label = "@Nguyễn Hoàng Vũ";
        var incoming = IncomingWithMentions(question, "question-quote", "ordinary-sender", [
            new ZaloBridgeMention("bot-a", 0, 5),
            new ZaloBridgeMention("target", question.IndexOf(label, StringComparison.Ordinal), label.Length)
        ], now);

        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a", "group-1", incoming, question, false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("đúng rồi", answer.Text);
        Assert.Contains("4 người tập bài đánh giải", answer.Text);
    }

    [Fact]
    public async Task Duplicate_name_followup_stays_on_public_evidence_path()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.AddRange(
            Member("dup-a", "Nguyễn Hoàng Vũ", now),
            Member("dup-b", "Nguyen Hoang Vu", now));
        foreach (var uid in new[] { "dup-a", "dup-b" })
        {
            fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                MessageId = $"evidence-{uid}",
                SenderId = uid,
                SenderName = "Nguyễn Hoàng Vũ",
                Content = "4 người tập bài để đánh giải",
                SentAt = now.AddDays(-2),
                ReceivedAt = now.AddDays(-2)
            });
        }
        await fixture.Db.SaveChangesAsync();

        var firstQuestion = "@Bott trước đó Nguyễn Hoàng Vũ có nói về đánh giải không?";
        var first = IncomingWithMentions(
            firstQuestion,
            "question-dup-1",
            "ordinary-sender",
            [new ZaloBridgeMention("bot-a", 0, 5)],
            now);
        var clarification = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a", "group-1", first, firstQuestion, false, CancellationToken.None);

        Assert.NotNull(clarification);
        Assert.Contains("Bạn đang hỏi người nào", clarification.Text);

        var followup = IncomingWithMentions(
            "@Bott 2",
            "question-dup-2",
            "ordinary-sender",
            [new ZaloBridgeMention("bot-a", 0, 5)],
            now.AddSeconds(5));
        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a", "group-1", followup, "2", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SearchMemberConversationEvidence, answer.Intent);
        Assert.DoesNotContain("trưởng nhóm", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4 người tập bài để đánh giải", answer.Text);
    }

    [Fact]
    public async Task Multiple_human_mentions_require_one_explicit_target()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.AddRange(
            Member("target-a", "Nguyễn Hoàng Vũ", now),
            Member("target-b", "Nam", now));
        await fixture.Db.SaveChangesAsync();

        var question = "@Bott tui hỏi @Nguyễn Hoàng Vũ trước đó có nói đánh giải với @Nam không?";
        var vu = "@Nguyễn Hoàng Vũ";
        var nam = "@Nam";
        var incoming = IncomingWithMentions(question, "question-multi", "ordinary-sender", [
            new ZaloBridgeMention("bot-a", 0, 5),
            new ZaloBridgeMention("target-a", question.IndexOf(vu, StringComparison.Ordinal), vu.Length),
            new ZaloBridgeMention("target-b", question.IndexOf(nam, StringComparison.Ordinal), nam.Length)
        ], now);

        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a", "group-1", incoming, question, false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("tag nhiều người", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag đúng 1 người", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ai_disabled_by_policy_never_calls_provider_for_evidence_judgement()
    {
        var (ai, handler) = CreateConfiguredAi(
            """{"verdict":"Supported","confidence":0.99,"messageIds":["evidence"]}""");
        await using var fixture = await Fixture.CreateAsync(ai);
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "evidence",
            SenderId = "target",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "4 người đánh giải",
            SentAt = now.AddDays(-1),
            ReceivedAt = now.AddDays(-1)
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new ZaloConversationEvidenceResearchService(fixture.Db, ai).ResearchAsync(
            "connection-a",
            "group-1",
            "target",
            "Nguyễn Hoàng Vũ",
            "trước đó có nói đánh giải không?",
            null,
            false,
            CancellationToken.None);

        Assert.False(result.AiCalled);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Ai_classifier_can_rescue_historical_paraphrase_outside_fixed_phrase_bank()
    {
        var classifierJson =
            """{"intent":"SearchMemberConversationEvidence","confidence":0.96,"memberReference":"Nguyễn Hoàng Vũ","timeRange":null,"limit":null,"needsClarification":false}""";
        var judgementJson =
            """{"verdict":"Supported","confidence":0.95,"messageIds":["semantic-evidence"]}""";
        var (ai, handler) = CreateConfiguredAi(classifierJson, judgementJson);
        await using var fixture = await Fixture.CreateAsync(ai);
        var now = DateTimeOffset.Parse("2026-10-25T13:00:00Z");
        fixture.Db.ZaloGroupMembers.Add(Member("target", "Nguyễn Hoàng Vũ", now));
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "connection-a",
            GroupId = "group-1",
            MessageId = "semantic-evidence",
            SenderId = "target",
            SenderName = "Nguyễn Hoàng Vũ",
            Content = "bữa đó tui tính gom 4 người tập bài rồi đi đánh giải",
            SentAt = now.AddDays(-3),
            ReceivedAt = now.AddDays(-3)
        });
        await fixture.Db.SaveChangesAsync();

        var question = "@Bott @Nguyễn Hoàng Vũ từng tính lập đội thi đấu hả?";
        Assert.False(ZaloConversationEvidenceIntentPolicy.LooksLikeQuestion(question));
        Assert.True(ZaloConversationEvidenceIntentPolicy.MightBeQuestion(question));
        var label = "@Nguyễn Hoàng Vũ";
        var incoming = IncomingWithMentions(question, "question-semantic", "ordinary-sender", [
            new ZaloBridgeMention("bot-a", 0, 5),
            new ZaloBridgeMention("target", question.IndexOf(label, StringComparison.Ordinal), label.Length)
        ], now);

        var answer = await fixture.Bot.TryHandleConversationEvidenceAsync(
            "connection-a", "group-1", incoming, question, true, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.True(answer.AiCalled);
        Assert.Contains("gom 4 người", answer.Text);
        Assert.Equal(2, handler.CallCount);
    }

    private static ZaloGroupMember Member(string userId, string displayName, DateTimeOffset now) => new()
    {
        ZaloConnectionId = "connection-a",
        GroupId = "group-1",
        ZaloUserId = userId,
        DisplayName = displayName,
        FirstSeenAt = now.AddDays(-30),
        LastSeenAt = now,
        LastSyncedAt = now,
        IsCurrentMember = true
    };

    private static ZaloIncomingMessageEvent IncomingWithMentions(
        string content,
        string messageId,
        string senderId,
        IReadOnlyList<ZaloBridgeMention> mentions,
        DateTimeOffset sentAt) =>
        new(
            "bot-a",
            "bot-a",
            "group-1",
            messageId,
            senderId,
            "Người hỏi",
            content,
            mentions,
            true,
            sentAt.ToUnixTimeMilliseconds());

    private static (AiAssistantService Service, SequenceAiHandler Handler) CreateConfiguredAi(
        params string[] responseContents)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        var handler = new SequenceAiHandler(responseContents);
        return (
            new AiAssistantService(
                new HttpClient(handler),
                configuration,
                NullLogger<AiAssistantService>.Instance),
            handler);
    }

    private sealed class Fixture(
        SqliteConnection connection,
        VolleyDraftDbContext db,
        ZaloMemberIntelligenceBotService bot) : IAsyncDisposable
    {
        public VolleyDraftDbContext Db { get; } = db;
        public ZaloMemberIntelligenceBotService Bot { get; } = bot;

        public static async Task<Fixture> CreateAsync(AiAssistantService? aiOverride = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = "admin@conversation-evidence.test",
                PasswordHash = "hash"
            });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection-a",
                AdminUserId = "admin",
                AccountZaloId = "bot-a",
                DisplayName = "Bott",
                EncryptedCredentials = "x",
                Status = ZaloConnectionStatus.Connected
            });
            await db.SaveChangesAsync();

            var ai = aiOverride ?? new AiAssistantService(
                new HttpClient(),
                new ConfigurationBuilder().Build(),
                NullLogger<AiAssistantService>.Instance);
            var bot = new ZaloMemberIntelligenceBotService(
                db,
                null!,
                null!,
                null!,
                null!,
                ai,
                NullLogger<ZaloMemberIntelligenceBotService>.Instance);
            return new Fixture(connection, db, bot);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SequenceAiHandler(IReadOnlyList<string> responseContents) : HttpMessageHandler
    {
        private int index;
        public int CallCount => index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = responseContents[Math.Min(index, responseContents.Count - 1)];
            index += 1;
            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
