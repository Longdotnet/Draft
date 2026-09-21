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

public sealed class ZaloTeamPreferenceSelfServiceDraftE2ETests
{
    [Fact]
    public async Task Direct_self_service_non_operator_request_reaches_pending_on_sqlite()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Bot.HandleIncomingAsync(fixture.PreferenceRequest());

        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleAsync(item =>
                item.ZaloConnectionId == Fixture.ConnectionId &&
                item.GroupId == Fixture.GroupId &&
                item.SenderZaloUserId == Fixture.LongZaloId);
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);
        using var payload = JsonDocument.Parse(pending.PendingPayloadJson);
        Assert.True(payload.RootElement.GetProperty("SelfService").GetBoolean());
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Self_service_non_operator_confirmed_preference_survives_auto_draft_on_one_team()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync(
            sourceMessageId: "team-pref-proposal",
            providerReplyMessageId: "provider-team-pref-proposal");
        var confirmation = fixture.Confirmation("provider-team-pref-proposal");

        var preRoute = await new ZaloMemoryV2Service(fixture.Db)
            .ProcessAsync(Fixture.GroupId, confirmation, confirmation.Content);

        Assert.False(preRoute.Handled);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleAsync(item =>
                item.ZaloConnectionId == Fixture.ConnectionId &&
                item.GroupId == Fixture.GroupId &&
                item.SenderZaloUserId == Fixture.LongZaloId);
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);
        using (var payload = JsonDocument.Parse(pending.PendingPayloadJson))
        {
            Assert.True(payload.RootElement.GetProperty("SelfService").GetBoolean());
        }
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());

        await fixture.Bot.HandleIncomingAsync(confirmation);

        fixture.Db.ChangeTracker.Clear();
        var group = await fixture.Db.TeamPreferenceGroups
            .AsNoTracking()
            .SingleAsync(item => item.SessionId == fixture.SessionId);
        var preferredPlayerIds = await fixture.Db.TeamPreferenceGroupPlayers
            .AsNoTracking()
            .Where(item => item.TeamPreferenceGroupId == group.Id)
            .OrderBy(item => item.RotationOrder)
            .Select(item => item.SessionPlayerId)
            .ToListAsync();
        Assert.Equal([fixture.LongPlayerId, fixture.ToAnPlayerId], preferredPlayerIds);

        var drafted = await new SessionDraftService(fixture.Db)
            .AutoRunDraftAsync(Fixture.AdminId, fixture.SessionId);

        Assert.True(drafted.IsSuccess, drafted.Error);
        Assert.Equal(SessionStatus.Finished, drafted.Value!.SessionStatus);

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.TeamPreferenceGroups
            .AsNoTracking()
            .AnyAsync(item => item.Id == group.Id && item.SessionId == fixture.SessionId));
        Assert.Equal(
            preferredPlayerIds,
            await fixture.Db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Where(item => item.TeamPreferenceGroupId == group.Id)
                .OrderBy(item => item.RotationOrder)
                .Select(item => item.SessionPlayerId)
                .ToListAsync());

        var assignedTeamIds = await fixture.Db.DraftSlotPlayers
            .AsNoTracking()
            .Where(link =>
                preferredPlayerIds.Contains(link.SessionPlayerId) &&
                link.DraftSlot.SessionId == fixture.SessionId)
            .Select(link => link.DraftSlot.AssignedTeamId)
            .Distinct()
            .ToListAsync();
        var assignedTeamId = Assert.Single(assignedTeamIds);
        Assert.False(string.IsNullOrWhiteSpace(assignedTeamId));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string AdminId = "admin-team-pref-e2e";
        public const string ConnectionId = "conn-team-pref-e2e";
        public const string GroupId = "g-team-pref-e2e";
        public const string LongZaloId = "user-long";
        public const string ToAnZaloId = "user-toan";

        private readonly SqliteConnection connection;
        private readonly HttpClient bridgeHttpClient;
        private readonly HttpClient aiHttpClient;

        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            ZaloBotService bot,
            HttpClient bridgeHttpClient,
            HttpClient aiHttpClient,
            string sessionId,
            string longPlayerId,
            string toAnPlayerId)
        {
            this.connection = connection;
            Db = db;
            Bot = bot;
            this.bridgeHttpClient = bridgeHttpClient;
            this.aiHttpClient = aiHttpClient;
            SessionId = sessionId;
            LongPlayerId = longPlayerId;
            ToAnPlayerId = toAnPlayerId;
        }

        public VolleyDraftDbContext Db { get; }
        public ZaloBotService Bot { get; }
        public string SessionId { get; }
        public string LongPlayerId { get; }
        public string ToAnPlayerId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ZaloBot:ExactCommandCooldownSeconds"] = "0",
                    ["ZaloBot:AiStyleEnabled"] = "false"
                })
                .Build();

            var admin = new User
            {
                Id = AdminId,
                DisplayName = "Admin",
                Email = $"team-pref-draft-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connectionRow = new ZaloConnection
            {
                Id = ConnectionId,
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(connectionRow);
            await db.SaveChangesAsync();

            var draft = new SessionDraftService(db);
            var created = await draft.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("T6", 3, 3));
            Assert.True(created.IsSuccess, created.Error);
            var session = await db.MatchSessions.SingleAsync(item => item.Id == created.Value!.Id);
            var sessionId = session.Id;
            session.ZaloConnectionId = ConnectionId;
            session.ZaloConnection = connectionRow;
            session.ZaloGroupId = GroupId;
            session.BotEnabled = true;
            session.BotOperatorZaloUserIdsJson = "[]";
            session.StartTime = ZaloTestDates.Next(DayOfWeek.Friday);
            await db.SaveChangesAsync();

            var players = new[]
            {
                "Captain 1",
                "Captain 2",
                "Captain 3",
                "Long",
                "To An",
                "Player 6",
                "Player 7",
                "Player 8",
                "Player 9"
            };
            var playerIds = new List<string>();
            foreach (var name in players)
            {
                var added = await draft.AddPlayerAsync(
                    AdminId,
                    sessionId,
                    new AddPlayerRequest(
                        name,
                        PlayerRole.New,
                        PlayerLevel.New,
                        PlayerGender.Male));
                Assert.True(added.IsSuccess, added.Error);
                playerIds.Add(added.Value!.Id);
            }
            var longPlayerId = playerIds[3];
            var toAnPlayerId = playerIds[4];

            var longProfile = new PlayerProfile
            {
                Id = "profile-long",
                ZaloUserId = LongZaloId,
                DisplayName = "Long",
                Gender = PlayerGender.Male,
                DefaultRole = PlayerRole.New,
                DefaultLevel = PlayerLevel.New
            };
            var toAnProfile = new PlayerProfile
            {
                Id = "profile-toan",
                ZaloUserId = ToAnZaloId,
                DisplayName = "To An",
                Gender = PlayerGender.Male,
                DefaultRole = PlayerRole.New,
                DefaultLevel = PlayerLevel.New
            };
            db.PlayerProfiles.AddRange(longProfile, toAnProfile);
            var longPlayer = await db.SessionPlayers.SingleAsync(item => item.Id == longPlayerId);
            longPlayer.PlayerProfileId = longProfile.Id;
            longPlayer.PlayerProfile = longProfile;
            var toAnPlayer = await db.SessionPlayers.SingleAsync(item => item.Id == toAnPlayerId);
            toAnPlayer.PlayerProfileId = toAnProfile.Id;
            toAnPlayer.PlayerProfile = toAnProfile;
            await db.SaveChangesAsync();

            var captains = await draft.SetManualCaptainsAsync(
                AdminId,
                sessionId,
                new ManualCaptainsRequest(playerIds.Take(3).ToList()));
            Assert.True(captains.IsSuccess, captains.Error);
            db.ChangeTracker.Clear();

            var bridgeHttpClient = new HttpClient(new BridgeSendHandler())
            {
                BaseAddress = new Uri("https://bridge.test/")
            };
            var aiHttpClient = new HttpClient(new NoAiHandler())
            {
                BaseAddress = new Uri("https://ai.test/")
            };
            var ai = new AiAssistantService(
                aiHttpClient,
                configuration,
                NullLogger<AiAssistantService>.Instance,
                db);
            var actionHistory = new ZaloBotActionHistoryService(
                db,
                NullLogger<ZaloBotActionHistoryService>.Instance);
            var memberIntelligence = new ZaloMemberIntelligenceBotService(
                db,
                null!,
                null!,
                null!,
                ai,
                NullLogger<ZaloMemberIntelligenceBotService>.Instance);
            var bot = new ZaloBotService(
                db,
                new ZaloBridgeClient(bridgeHttpClient),
                ai,
                null!,
                draft,
                null!,
                null!,
                null!,
                actionHistory,
                memberIntelligence,
                null!,
                null!,
                configuration,
                NullLogger<ZaloBotService>.Instance);

            return new Fixture(
                connection,
                db,
                bot,
                bridgeHttpClient,
                aiHttpClient,
                sessionId,
                longPlayerId,
                toAnPlayerId);
        }

        public ZaloIncomingMessageEvent PreferenceRequest()
        {
            const string content = "@Npc xếp tui chung team với @To An ở T6 đi";
            return new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: GroupId,
                messageId: "team-pref-request",
                senderId: LongZaloId,
                senderName: "Long",
                content: content,
                mentions:
                [
                    new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                    new ZaloBridgeMention(
                        ToAnZaloId,
                        content.IndexOf("@To An", StringComparison.Ordinal),
                        "@To An".Length)
                ],
                mentionedBot: true,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public async Task SaveReadyProposalAsync(
            string sourceMessageId,
            string providerReplyMessageId)
        {
            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = LongZaloId,
                requesterDisplayName = "Long",
                partnerZaloUserId = ToAnZaloId,
                partnerDisplayName = "To An",
                sessionId = SessionId,
                sessionName = "T6",
                metadata = new
                {
                    speechAct = "proposal",
                    writeAuthorized = false,
                    domain = "TeamPreference"
                }
            });
            await new ZaloConversationStateV2Store(Db).SaveActiveAsync(
                GroupId,
                LongZaloId,
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                collected,
                "[]",
                "[]",
                sourceMessageId,
                sourceMessageId,
                DateTimeOffset.UtcNow.AddMinutes(5));
            await new ZaloMessageGraphStore(Db).RememberOutboundAsync(
                ConnectionId,
                GroupId,
                providerReplyMessageId,
                sourceMessageId);
        }

        public ZaloIncomingMessageEvent Confirmation(string quotedMessageId)
        {
            const string content = "xác nhận";
            return new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: GroupId,
                messageId: "team-pref-confirm",
                senderId: LongZaloId,
                senderName: "Long",
                content: content,
                mentions: [],
                mentionedBot: false,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                quote: new ZaloBridgeMessageQuote(
                    quotedMessageId,
                    "bot-account",
                    "Npc",
                    "Long + To An muốn chung team ở T6. Reply tin này và xác nhận để áp dụng.",
                    "chat",
                    DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
                    null));
        }

        public async ValueTask DisposeAsync()
        {
            bridgeHttpClient.Dispose();
            aiHttpClient.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class BridgeSendHandler : HttpMessageHandler
    {
        private int sendCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/v1/group-messages", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            sendCount += 1;
            var payload = JsonSerializer.Serialize(new
            {
                sent = true,
                mock = true,
                messageId = $"provider-team-pref-{sendCount}"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NoAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI must not be called by deterministic team-preference flow.");
    }
}
