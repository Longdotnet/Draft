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

public sealed class ZaloDirectSocialResilienceTests
{
    [Fact]
    public async Task Direct_story_request_keeps_long_multisentence_ai_reply()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string answer =
            "Có ông kia vào sân tuyên bố hôm nay đánh nhẹ thôi.\n" +
            "Set đầu ảnh cứu bóng lăn ba vòng xong đứng dậy phủi áo như chưa có gì, cả sân cười muốn xỉu.\n" +
            "Tới cuối trận mới phát hiện người đánh sung nhất từ đầu tới giờ cũng chính là ông nội bảo đánh nhẹ =))";
        var handler = new StaticAiHandler(HttpStatusCode.OK, answer);
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("story-1", "Bot ơi kể chuyện nghe"),
            SocialDecision("story-1") with
            {
                Score = 5,
                Kind = ZaloAmbientParticipationKind.None,
                Signals = ["bot_cooldown", "busy_group"]
            },
            new ZaloAmbientSocialPilotSettings(
                Enabled: true,
                SendEnabled: true,
                MinimumScore: 90,
                MaxContextMessages: 8,
                MaxReplyChars: 180,
                DirectMaxReplyChars: 420));

        Assert.NotNull(result);
        Assert.Equal("direct_social_ai", result!.AddressReason);
        Assert.True(result.AiCalled);
        Assert.Null(result.GenerationFallbackReason);
        Assert.True(result.Text.Length > 180);
        Assert.True(result.Text.Length <= 420);
        Assert.DoesNotContain('\n', result.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Direct_social_provider_failure_retries_transport_but_returns_one_visible_fallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler(HttpStatusCode.ServiceUnavailable, null);
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("story-503", "Bot ơi kể chuyện nghe"),
            SocialDecision("story-503"),
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.NotNull(result);
        Assert.Equal("direct_social_ai_generation_fallback", result!.AddressReason);
        Assert.True(result.AiCalled);
        Assert.Equal("ai_generation_failed", result.GenerationFallbackReason);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Direct_social_without_ai_configuration_acknowledges_user_instead_of_silence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler(HttpStatusCode.OK, "không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("story-no-ai", "Bot ơi kể chuyện nghe"),
            SocialDecision("story-no-ai"),
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.NotNull(result);
        Assert.Equal("direct_social_ai_unavailable", result!.AddressReason);
        Assert.False(result.AiCalled);
        Assert.Equal("ai_not_configured", result.GenerationFallbackReason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unaddressed_social_text_still_does_not_wake_bot_when_ai_is_unavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler(HttpStatusCode.OK, "không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("human-chat", "kể chuyện nghe"),
            SocialDecision("human-chat"),
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    private static IConfiguration EnabledConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model",
                ["Ai:RetryCount"] = "1"
            })
            .Build();

    private static ZaloAmbientParticipationDecision SocialDecision(string messageId) => new(
        WouldReply: false,
        Score: 30,
        Kind: ZaloAmbientParticipationKind.Social,
        Intent: ZaloBotIntent.GeneralChat.ToString(),
        IntentConfidence: .5,
        Signals: ["question"],
        Situation: new ZaloAmbientGroupSituation(
            RecentMessageCount: 1,
            RecentTwoMinuteMessageCount: 1,
            DistinctParticipantCount: 1,
            RecentBotMessageCount: 0,
            LastBotMessageAt: null,
            RecentMessageIds: [messageId]));

    private static ZaloIncomingMessageEvent Message(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class StaticAiHandler(HttpStatusCode statusCode, string? answer) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = answer ?? string.Empty } }
                    }
                });
                response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
            else
            {
                response.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
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
                Id = "admin-direct-social",
                DisplayName = "Admin",
                Email = $"direct-social-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
