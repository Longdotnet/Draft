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
    public async Task MentionedMemberPlusOne_IsUnderstoodAsOutsideGuestSponsor_AndDoesNotAddMemberDirectly()
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
        db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            Id = "group-member-hmy",
            ZaloConnectionId = zalo.Id,
            GroupId = "g1",
            ZaloUserId = "hmy-id",
            DisplayName = "Hmy Bk",
            IsCurrentMember = true
        });
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

        const string content = "@Npc +1 bạn @Hmy Bk hôm nay";
        var hmyPos = content.IndexOf("@Hmy Bk", StringComparison.Ordinal);
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "direct-guest-1",
            senderId: "tan-chi",
            senderName: "Tấn Chí",
            content: content,
            mentions:
            [
                new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                new ZaloBridgeMention("hmy-id", hmyPos, "@Hmy Bk".Length)
            ],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var handled = await service.TryHandleZaloConfirmationAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, bridgeHandler.SendCount);
        using (var payload = JsonDocument.Parse(Assert.Single(bridgeHandler.RequestBodies)))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.Contains("bạn ngoài group", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Hmy Bk", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("không phải thêm Hmy Bk", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reply ngay tin này", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("+1", message, StringComparison.Ordinal);
        }

        var guidance = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item =>
                item.ZaloConnectionId == zalo.Id &&
                item.MessageId == "provider-guest-gate-1");
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent(session.Id),
            guidance.SelectedIntent);
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestSignupGuidanceReplyOutcome,
            guidance.ReplyOutcome);

        // Simulate the exact production guidance sent before this hotfix: the bot
        // message exists and points back to the original request, but carries no
        // explicit guest-session metadata of its own.
        var legacyGuidance = await db.ZaloGroupMessages
            .SingleAsync(item =>
                item.ZaloConnectionId == zalo.Id &&
                item.MessageId == guidance.MessageId);
        legacyGuidance.SelectedIntent = null;
        legacyGuidance.ReplyOutcome = "sent";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        const string followupContent = "+1 bạn @Hmy Bk";
        var followupHmyPos = followupContent.IndexOf("@Hmy Bk", StringComparison.Ordinal);
        var followup = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "guest-guidance-followup-1",
            senderId: "tan-chi",
            senderName: "Tấn Chí",
            content: followupContent,
            mentions:
            [
                new ZaloBridgeMention("hmy-id", followupHmyPos, "@Hmy Bk".Length)
            ],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: new ZaloBridgeMessageQuote(
                "provider-guest-gate-1",
                "bot-account",
                "Npc",
                guidance.Content,
                "chat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null));

        Assert.True(await service.TryHandleZaloConfirmationAsync(followup));
        Assert.Equal(1, bridgeHandler.SendCount);
        var queued = await db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item =>
                item.ZaloConnectionId == zalo.Id &&
                item.MessageId == followup.MessageId);
        Assert.Null(queued.BotReplySentAt);
        Assert.Null(queued.ReplyOutcome);
        Assert.Null(queued.ProcessingToken);
        var presentCount = await db.SessionPlayers
            .AsNoTracking()
            .CountAsync(item => item.SessionId == "session-t7" && item.IsPresent);
        Assert.Equal(17, presentCount);
        Assert.False(await db.SessionPlayers
            .AsNoTracking()
            .AnyAsync(item => item.SessionId == "session-t7" && item.DisplayName == "Hmy Bk"));
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
