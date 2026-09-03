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
    public async Task Plain_text_wake_opens_same_sender_fact_conversation_without_more_mentions()
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
        db.MatchSessions.AddRange(
            new MatchSession
            {
                Id = "session-t6",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                TeamCount = 3,
                TeamSize = 6
            },
            new MatchSession
            {
                Id = "session-cn",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "CN",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                TeamCount = 3,
                TeamSize = 6,
                Players =
                {
                    new SessionPlayer
                    {
                        Id = "cn-p1",
                        DisplayName = "Long",
                        IsPresent = true
                    },
                    new SessionPlayer
                    {
                        Id = "cn-p2",
                        DisplayName = "Nam",
                        IsPresent = true
                    }
                }
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

        var wake = Incoming("wake-live-1", "Bot ơi bot");
        var firstHandled = await service.TryHandleZaloConfirmationAsync(wake);
        var duplicateHandled = await service.TryHandleZaloConfirmationAsync(wake);

        Assert.False(firstHandled);
        Assert.False(duplicateHandled);
        Assert.Equal(1, bridgeHandler.SendCount);

        using (var payload = JsonDocument.Parse(bridgeHandler.RequestBodies[0]))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.Contains("tui đây", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Long", message, StringComparison.OrdinalIgnoreCase);
        }

        // Same sender continues naturally without @Npc, question mark or repeating
        // "bot". The active wake lease makes this elliptical slot turn explicit
        // conversation context, while the answer still comes only from DB state.
        var followUp = Incoming("wake-follow-1", "CN còn nhiều slot");
        var followHandled = await service.TryHandleZaloConfirmationAsync(followUp);

        Assert.False(followHandled);
        Assert.Equal(2, bridgeHandler.SendCount);
        Assert.Equal(2, bridgeHandler.RequestBodies.Count);
        using (var payload = JsonDocument.Parse(bridgeHandler.RequestBodies[1]))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.Contains("CN", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2/18", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("16 slot", message, StringComparison.OrdinalIgnoreCase);
        }

        var wakeInbound = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == wake.MessageId);
        var followInbound = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == followUp.MessageId);
        Assert.Equal("ambient_sent", wakeInbound.ReplyOutcome);
        Assert.Equal(ZaloBotIntent.Help.ToString(), wakeInbound.SelectedIntent);
        Assert.Equal("ambient_sent", followInbound.ReplyOutcome);
        Assert.Equal(ZaloBotIntent.MissingSlots.ToString(), followInbound.SelectedIntent);
        Assert.False(followInbound.AiCalled);
    }

    private static ZaloIncomingMessageEvent Incoming(string messageId, string content) => new(
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
                return Json(
                    HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-wake-{SendCount}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
