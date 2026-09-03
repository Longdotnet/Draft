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

public sealed class ZaloAmbientSocialMeaningBanterTests
{
    [Fact]
    public async Task Active_lease_kick_after_member_withdrawal_banter_gets_social_reply_without_mutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddContextAsync(
            ("ctx-1", "user-nguyen", "Đặng Thế Nguyên", "Giờ vô nhảy hem nổi 😡", false),
            ("ctx-2", "bot-account", "Npc", "Thanh Long ơi, tui đây 😄", true),
            ("m-kick", "user-long", "Thanh Long", "Kick Đặng Thế Nguyên rút slot", false));

        var handler = new SequenceAiHandler(
            "{\"kind\":\"Banter\",\"confidence\":0.96,\"reason\":\"hyperbolic_group_teasing\"}",
            "Rút có cái slot mà án phạt bay khỏi group luôn hả =)))");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("m-kick", "Kick Đặng Thế Nguyên rút slot"),
            LeaseDecision("ctx-1", "ctx-2", "m-kick"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Rút có cái slot mà án phạt bay khỏi group luôn hả =)))", result!.Text);
        Assert.Equal("social_meaning_banter_ai", result.AddressReason);
        Assert.Equal(2, handler.CallCount);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task One_line_bot_oi_kick_banter_is_not_mistaken_for_human_vocative()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddContextAsync(
            ("m-direct", "user-long", "Thanh Long", "Bot ơi kick Đặng Thế Nguyên rút slot =))", false));

        var handler = new SequenceAiHandler(
            "{\"kind\":\"Banter\",\"confidence\":0.94,\"reason\":\"playful_overreaction\"}",
            "Ê rút slot thôi mà đòi trục xuất luôn, án hơi căng nha =)))");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var incoming = Message("m-direct", "Bot ơi kick Đặng Thế Nguyên rút slot =))");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            QuietSituation("m-direct"),
            AmbientSettings(),
            DateTimeOffset.UtcNow);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            decision,
            EnabledSettings(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("social_meaning_banter_ai", result!.AddressReason);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Genuine_kick_request_is_suppressed_after_meaning_classification()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddContextAsync(
            ("m-real", "user-long", "Thanh Long", "Bot kick Đặng Thế Nguyên khỏi group thật đi, làm ngay", false));

        var handler = new SequenceAiHandler(
            "{\"kind\":\"GenuineActionRequest\",\"confidence\":0.99,\"reason\":\"explicit_real_action\"}",
            "không được gọi generation");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var incoming = Message("m-real", "Bot kick Đặng Thế Nguyên khỏi group thật đi, làm ngay");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            QuietSituation("m-real"),
            AmbientSettings(),
            DateTimeOffset.UtcNow);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            decision,
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Business_withdrawal_statement_does_not_get_social_joke()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddContextAsync(
            ("m-fact", "user-long", "Thanh Long", "Đặng Thế Nguyên rút slot rồi", false));

        var handler = new SequenceAiHandler(
            "{\"kind\":\"BusinessFactOrHelp\",\"confidence\":0.95,\"reason\":\"slot_withdrawal_fact\"}",
            "không được gọi generation");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("m-fact", "Đặng Thế Nguyên rút slot rồi"),
            LeaseDecision("m-fact"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("Tui đã kick Nguyên khỏi group rồi =))")]
    [InlineData("Kick xong rồi nha =))")]
    [InlineData("Tui vừa remove Nguyên rồi")]
    public void Social_candidate_cannot_claim_admin_action_happened(string candidate)
    {
        Assert.False(ZaloAmbientSocialResponder.IsSafeCandidate(candidate, 180));
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

    private static ZaloAmbientSettings AmbientSettings() =>
        new(true, false, 60, 5, 40, 2, 8);

    private static ZaloAmbientParticipationDecision LeaseDecision(params string[] contextIds) => new(
        WouldReply: true,
        Score: 96,
        Kind: ZaloAmbientParticipationKind.Social,
        Intent: ZaloBotIntent.GeneralChat.ToString(),
        IntentConfidence: .96,
        Signals: ["active_conversation_lease", "lease_social_followup"],
        Situation: new ZaloAmbientGroupSituation(
            RecentMessageCount: contextIds.Length,
            RecentTwoMinuteMessageCount: contextIds.Length,
            DistinctParticipantCount: 2,
            RecentBotMessageCount: 1,
            LastBotMessageAt: DateTimeOffset.UtcNow.AddSeconds(-30),
            RecentMessageIds: contextIds));

    private static ZaloAmbientGroupSituation QuietSituation(params string[] contextIds) => new(
        RecentMessageCount: contextIds.Length,
        RecentTwoMinuteMessageCount: contextIds.Length,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: 0,
        LastBotMessageAt: null,
        RecentMessageIds: contextIds);

    private static ZaloIncomingMessageEvent Message(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Thanh Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class SequenceAiHandler(params string[] answers) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(answers);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (queue.Count == 0)
                throw new InvalidOperationException("Unexpected extra AI call.");
            var answer = queue.Dequeue();
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = answer } }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
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
                Id = "admin-banter",
                DisplayName = "Admin",
                Email = $"banter-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test"
            });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async Task AddContextAsync(params (string Id, string SenderId, string SenderName, string Content, bool IsBot)[] messages)
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(-messages.Length);
            foreach (var message in messages)
            {
                Db.ZaloGroupMessages.Add(new ZaloGroupMessage
                {
                    ZaloConnectionId = "conn-1",
                    GroupId = "g1",
                    MessageId = message.Id,
                    SenderId = message.SenderId,
                    SenderName = message.SenderName,
                    Content = message.Content,
                    IsFromBot = message.IsBot,
                    SentAt = now,
                    ReceivedAt = now
                });
                now = now.AddSeconds(1);
            }
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
