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

public sealed class ZaloAmbientWakeLiveIntegrationTests
{
    [Fact]
    public async Task Plain_text_bot_oi_bot_without_mention_sends_exactly_one_live_reply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"wake-live-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        };
        var zalo = new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc",
            EncryptedCredentials = "test"
        };
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(new MatchSession
        {
            Id = "session-1",
            AdminUserId = admin.Id,
            ZaloConnectionId = zalo.Id,
            ZaloConnection = zalo,
            ZaloGroupId = "g1",
            Name = "T6",
            Status = SessionStatus.Setup,
            BotEnabled = true,
            TeamCount = 3,
            TeamSize = 6
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Zalo:CredentialEncryptionKey"] = "ambient-wake-live-test-key",
                ["ZaloBot:Ambient:Enabled"] = "true",
                ["ZaloBot:Ambient:ShadowMode"] = "false",
                ["ZaloBot:Ambient:WouldReplyThreshold"] = "60",
                ["ZaloBot:Ambient:BotCooldownSeconds"] = "2",
                ["ZaloBot:Ambient:FactPilot:Enabled"] = "true",
                ["ZaloBot:Ambient:FactPilot:MinimumScore"] = "60",
                ["ZaloBot:Ambient:SocialPilot:Enabled"] = "false",
                ["ZaloBot:Ambient:SocialPilot:SendEnabled"] = "false"
            })
            .Build();

        var bridgeHandler = new RecordingBridgeHandler();
        var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });
        var service = new ZaloOverbookService(
            db,
            bridge,
            new ZaloCredentialProtector(configuration),
            null!,
            null!,
            configuration,
            NullLogger<ZaloOverbookService>.Instance);

        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "wake-live-1",
            senderId: "user-long",
            senderName: "Long",
            content: "Bot ơi bot",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var firstHandled = await service.TryHandleZaloConfirmationAsync(incoming);
        var secondHandled = await service.TryHandleZaloConfirmationAsync(incoming);

        Assert.False(firstHandled);
        Assert.False(secondHandled);
        Assert.Equal(1, bridgeHandler.SendCount);
        Assert.Single(bridgeHandler.RequestBodies);

        using var payload = JsonDocument.Parse(bridgeHandler.RequestBodies[0]);
        var message = payload.RootElement.GetProperty("message").GetString();
        Assert.Contains("tui đây", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Long", message, StringComparison.OrdinalIgnoreCase);

        var inbound = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == incoming.MessageId);
        Assert.NotNull(inbound.BotReplySentAt);
        Assert.Equal("ambient_sent", inbound.ReplyOutcome);
        Assert.Equal(ZaloBotIntent.Help.ToString(), inbound.SelectedIntent);
        Assert.False(inbound.AiCalled);
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount += 1;
                RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.OK, "{\"sent\":true,\"mock\":false,\"messageId\":\"provider-wake-1\"}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
