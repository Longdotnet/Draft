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

        // Reproduce the production ingress state: the coordinator has already claimed
        // this row with a token before pre-routing calls the guest gate. The tracked
        // entity inside the service may still be stale, so queueing must release the
        // durable token with ExecuteUpdate rather than relying on SaveChanges.
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = zalo.Id,
            GroupId = "g1",
            MessageId = followup.MessageId,
            SenderId = followup.SenderId,
            SenderName = followup.SenderName,
            Content = followup.Content,
            IsFromBot = false,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStartedAt = DateTimeOffset.UtcNow,
            ProcessingToken = "ingress-claim-token"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await service.TryHandleZaloConfirmationAsync(followup));
        Assert.Equal(1, bridgeHandler.SendCount);

        // This mirrors CompletePreRouteAsync's claim-token fence. Once the guest gate
        // queues the mutation, the coordinator must no longer be able to mark the row
        // pre_route_handled; otherwise the background guest worker will never see it.
        var staleCoordinatorCompletion = await db.ZaloGroupMessages
            .Where(item =>
                item.ZaloConnectionId == zalo.Id &&
                item.GroupId == "g1" &&
                item.MessageId == followup.MessageId &&
                item.ProcessingToken == "ingress-claim-token")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.ReplyOutcome, "pre_route_handled")
                .SetProperty(item => item.SelectedIntent, "pre-route")
                .SetProperty(item => item.ProcessingToken, (string?)null));
        Assert.Equal(0, staleCoordinatorCompletion);

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

        // Emulate the background worker successfully creating the external guest.
        // Ownership remains Hmy; the requester only initiated the delegated +1.
        var liveSession = await db.MatchSessions.SingleAsync(item => item.Id == session.Id);
        var guestResult = await new ZaloGuestReservationService(db).AddAsync(
            liveSession,
            "hmy-id",
            "Hmy Bk",
            followup.MessageId,
            guidance.MessageId,
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Quantity: 1,
                Guests: [new ZaloRecruitmentGuestSpec()]),
            CancellationToken.None);
        var guest = Assert.Single(guestResult.Added);
        Assert.Equal("hmy-id", guest.SponsorZaloUserId);
        liveSession.BotOperatorZaloUserIdsJson = JsonSerializer.Serialize(new[] { "tan-chi" });
        await db.SaveChangesAsync();

        // The successful add reply asks the requester to choose who supplies the
        // external guest profile. Seed that exact grounded prompt and choose option 2.
        const string profileChoiceMessageId = "provider-profile-choice-1";
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = zalo.Id,
            GroupId = "g1",
            MessageId = profileChoiceMessageId,
            SenderId = "bot-account",
            SenderName = "Npc",
            Content = "Chọn 1 nếu admin cập nhật hộ; chọn 2 để tui tag @Hmy Bk cập nhật.",
            IsFromBot = true,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            SelectedIntent = ZaloRecruitmentGuestGatePolicy.GuestProfileContextSelectedIntent(
                session.Id,
                followup.MessageId),
            ReplyOutcome = ZaloRecruitmentGuestGatePolicy.GuestProfileChoiceReplyOutcome
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Option 1 is the requester/admin path. The requester is configured as a bot
        // operator for this test, so the bot should keep the same source guest and ask
        // that requester to fill the profile on Hmy's behalf without changing sponsor.
        var chooseAdmin = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "guest-profile-choice-1",
            senderId: "tan-chi",
            senderName: "Tấn Chí",
            content: "1",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: new ZaloBridgeMessageQuote(
                profileChoiceMessageId,
                "bot-account",
                "Npc",
                "Chọn 1 nếu admin cập nhật hộ; chọn 2 để tui tag @Hmy Bk cập nhật.",
                "chat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null));

        Assert.True(await service.TryHandleZaloConfirmationAsync(chooseAdmin));
        Assert.Equal(2, bridgeHandler.SendCount);
        using (var payload = JsonDocument.Parse(bridgeHandler.RequestBodies[1]))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.StartsWith("@Tấn Chí", message, StringComparison.Ordinal);
            Assert.Contains("cập nhật giúp", message, StringComparison.OrdinalIgnoreCase);
            var mention = Assert.Single(payload.RootElement.GetProperty("mentions").EnumerateArray());
            Assert.Equal("tan-chi", mention.GetProperty("uid").GetString());
            Assert.Equal(0, mention.GetProperty("pos").GetInt32());
        }
        var adminPrompt = await db.ZaloGroupMessages.AsNoTracking().SingleAsync(item =>
            item.ZaloConnectionId == zalo.Id && item.MessageId == "provider-guest-gate-2");
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestProfileAdminPromptReplyOutcome,
            adminPrompt.ReplyOutcome);
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestProfileContextSelectedIntent(session.Id, followup.MessageId),
            adminPrompt.SelectedIntent);

        var chooseSponsor = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "guest-profile-choice-2",
            senderId: "tan-chi",
            senderName: "Tấn Chí",
            content: "2",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: new ZaloBridgeMessageQuote(
                profileChoiceMessageId,
                "bot-account",
                "Npc",
                "Chọn 1 nếu admin cập nhật hộ; chọn 2 để tui tag @Hmy Bk cập nhật.",
                "chat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null));

        Assert.True(await service.TryHandleZaloConfirmationAsync(chooseSponsor));
        Assert.Equal(3, bridgeHandler.SendCount);
        using (var payload = JsonDocument.Parse(bridgeHandler.RequestBodies[2]))
        {
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.StartsWith("@Hmy Bk", message, StringComparison.Ordinal);
            Assert.Contains("cập nhật", message, StringComparison.OrdinalIgnoreCase);
            var mention = Assert.Single(payload.RootElement.GetProperty("mentions").EnumerateArray());
            Assert.Equal("hmy-id", mention.GetProperty("uid").GetString());
            Assert.Equal(0, mention.GetProperty("pos").GetInt32());
        }
        var sponsorPrompt = await db.ZaloGroupMessages.AsNoTracking().SingleAsync(item =>
            item.ZaloConnectionId == zalo.Id && item.MessageId == "provider-guest-gate-3");
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestProfileSponsorPromptReplyOutcome,
            sponsorPrompt.ReplyOutcome);
        Assert.Equal(
            ZaloRecruitmentGuestGatePolicy.GuestProfileContextSelectedIntent(session.Id, followup.MessageId),
            sponsorPrompt.SelectedIntent);

        // Hmy owns the guest and may now supply the profile naturally. Updating the
        // profile must never transfer sponsorship to the requester.
        var sponsorProfileReply = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "guest-profile-hmy-1",
            senderId: "hmy-id",
            senderName: "Hmy Bk",
            content: "#1 tên Mai, nữ, chủ công, khá",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: new ZaloBridgeMessageQuote(
                sponsorPrompt.MessageId,
                "bot-account",
                "Npc",
                sponsorPrompt.Content,
                "chat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null));

        Assert.True(await service.TryHandleZaloConfirmationAsync(sponsorProfileReply));
        Assert.Equal(4, bridgeHandler.SendCount);
        var updatedGuest = await db.ZaloGuestReservations.AsNoTracking().SingleAsync(item =>
            item.SessionId == session.Id && item.SourceMessageId == followup.MessageId);
        Assert.Equal("hmy-id", updatedGuest.SponsorZaloUserId);
        Assert.Equal("Mai", updatedGuest.DisplayName);
        Assert.Equal(PlayerGender.Female, updatedGuest.Gender);
        Assert.Equal(PlayerRole.Attack, updatedGuest.Role);
        Assert.Equal(PlayerLevel.Good, updatedGuest.Level);
        var updatedPlayer = await db.SessionPlayers.AsNoTracking().SingleAsync(item =>
            item.Id == updatedGuest.SessionPlayerId);
        Assert.Equal("Mai", updatedPlayer.DisplayName);
        Assert.Equal(PlayerGender.Female, updatedPlayer.Gender);
        Assert.Equal(PlayerRole.Attack, updatedPlayer.Role);
        Assert.Equal(PlayerLevel.Good, updatedPlayer.Level);
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
