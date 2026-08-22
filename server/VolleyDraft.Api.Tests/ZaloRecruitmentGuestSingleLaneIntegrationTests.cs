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

public sealed class ZaloRecruitmentGuestSingleLaneIntegrationTests
{
    [Fact]
    public async Task MentionedNaturalOutsideGuestRequest_IsGuidanceOnly_AndDoesNotChangeRoster()
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
            Id = "admin-guest-gate",
            DisplayName = "Admin",
            Email = $"guest-gate-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        };
        var zalo = new ZaloConnection
        {
            Id = "conn-guest-gate",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc",
            EncryptedCredentials = "test"
        };
        var session = new MatchSession
        {
            Id = "session-t7",
            AdminUserId = admin.Id,
            ZaloConnectionId = zalo.Id,
            ZaloConnection = zalo,
            ZaloGroupId = "g1",
            Name = "Thứ 7 22/8",
            Status = SessionStatus.Setup,
            BotEnabled = true,
            TeamCount = 3,
            TeamSize = 6,
            StartTime = DateTimeOffset.UtcNow.AddHours(3)
        };
        for (var index = 1; index <= 17; index += 1)
        {
            session.Players.Add(new SessionPlayer
            {
                Id = $"p-{index}",
                DisplayName = $"Player {index}",
                IsPresent = true
            });
        }
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Zalo:CredentialEncryptionKey"] = "guest-single-lane-test-key",
                ["ZaloBot:Ambient:Enabled"] = "false",
                ["ZaloBot:Ambient:ShadowMode"] = "true",
                ["ZaloBot:DraftAutopilot:Enabled"] = "false"
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
            messageId: "direct-guest-1",
            senderId: "tan-chi",
            senderName: "Tấn Chí",
            content: "@Npc nay tui đi chung với 1 bạn ở ngoài gr",
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var handled = await service.TryHandleZaloConfirmationAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, bridgeHandler.SendCount);
        using (var payload = JsonDocument.Parse(Assert.Single(bridgeHandler.RequestBodies)))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.Contains("chưa cộng slot", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reply đúng tin", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("+1", message, StringComparison.Ordinal);
        }

        var presentCount = await db.SessionPlayers
            .AsNoTracking()
            .CountAsync(item => item.SessionId == "session-t7" && item.IsPresent);
        Assert.Equal(17, presentCount);
        Assert.False(await db.ZaloGuestReservations.AsNoTracking().AnyAsync());

        var stored = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == zalo.Id && item.MessageId == incoming.MessageId);
        Assert.Equal("RecruitmentGuestReplyRequired", stored.SelectedIntent);
        Assert.Equal("sent", stored.ReplyOutcome);
        Assert.False(stored.AiCalled);
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
                return Json(
                    HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-guest-gate-{SendCount}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
