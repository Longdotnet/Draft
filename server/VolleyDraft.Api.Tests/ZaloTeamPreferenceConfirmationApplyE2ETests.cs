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

public sealed class ZaloTeamPreferenceConfirmationApplyE2ETests
{
    [Fact]
    public async Task Exact_reply_confirmation_is_applied_once_and_replay_is_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync(
            senderId: "user-long",
            sourceMessageId: "proposal-source",
            providerReplyMessageId: "provider-proposal");

        var incoming = Confirmation(
            messageId: "confirm-once",
            quotedMessageId: "provider-proposal");

        // This is the same pre-routing handoff used by the webhook before the legacy
        // ZaloBotService domain router. It must not mutate TeamPreference yet.
        var preRoute = await new ZaloMemoryV2Service(fixture.Db)
            .ProcessAsync("g1", incoming, incoming.Content);
        Assert.False(preRoute.Handled);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        Assert.Equal(
            ZaloBotIntent.TeamPreferenceConfirm.ToString(),
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);

        var bridgeHandler = new BridgeSendHandler();
        using var bridgeHttpClient = new HttpClient(bridgeHandler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        };
        using var aiHttpClient = new HttpClient(new NoAiHandler())
        {
            BaseAddress = new Uri("https://ai.test/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:ExactCommandCooldownSeconds"] = "0",
                ["ZaloBot:AiStyleEnabled"] = "false"
            })
            .Build();
        var ai = new AiAssistantService(
            aiHttpClient,
            configuration,
            NullLogger<AiAssistantService>.Instance,
            fixture.Db);
        var actionHistory = new ZaloBotActionHistoryService(
            fixture.Db,
            NullLogger<ZaloBotActionHistoryService>.Instance);
        var memberIntelligence = new ZaloMemberIntelligenceBotService(
            fixture.Db,
            null!,
            null!,
            null!,
            ai,
            NullLogger<ZaloMemberIntelligenceBotService>.Instance);
        var bot = new ZaloBotService(
            fixture.Db,
            new ZaloBridgeClient(bridgeHttpClient),
            ai,
            null!,
            new SessionDraftService(fixture.Db),
            null!,
            null!,
            null!,
            actionHistory,
            memberIntelligence,
            null!,
            null!,
            configuration,
            NullLogger<ZaloBotService>.Instance);

        await bot.HandleIncomingAsync(incoming);

        fixture.Db.ChangeTracker.Clear();
        var group = await fixture.Db.TeamPreferenceGroups
            .AsNoTracking()
            .SingleAsync(item => item.SessionId == "session-t6");
        var groupPlayers = await fixture.Db.TeamPreferenceGroupPlayers
            .AsNoTracking()
            .Where(item => item.TeamPreferenceGroupId == group.Id)
            .OrderBy(item => item.RotationOrder)
            .Select(item => item.SessionPlayerId)
            .ToListAsync();
        Assert.Equal(2, groupPlayers.Count);
        Assert.Contains("session-t6-long", groupPlayers);
        Assert.Contains("session-t6-toan", groupPlayers);

        var history = await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == "session-t6")
            .ToListAsync();
        var action = Assert.Single(history);
        Assert.Equal("TeamPreference", action.ActionType);
        Assert.Equal("user-long", action.ActorZaloUserId);
        Assert.Contains("Long", action.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("To An", action.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Null(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));

        var storedConfirmation = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item =>
                item.ZaloConnectionId == "conn-1" &&
                item.MessageId == "confirm-once");
        Assert.NotNull(storedConfirmation.BotReplySentAt);
        Assert.Equal("sent", storedConfirmation.ReplyOutcome);
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), storedConfirmation.SelectedIntent);
        Assert.Equal(1, bridgeHandler.SendCount);
        Assert.Contains("Đã ghi nhận", bridgeHandler.LastMessage, StringComparison.OrdinalIgnoreCase);

        // Replay the exact same provider webhook delivery. Message-id persistence and
        // BotReplySentAt must stop routing before a second domain write or send.
        await bot.HandleIncomingAsync(incoming);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.TeamPreferenceGroups.AsNoTracking()
            .CountAsync(item => item.SessionId == "session-t6"));
        Assert.Equal(2, await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking().CountAsync());
        Assert.Equal(1, await fixture.Db.ZaloBotActionHistory.AsNoTracking()
            .CountAsync(item => item.SessionId == "session-t6"));
        Assert.Equal(1, bridgeHandler.SendCount);
    }

    private static ZaloIncomingMessageEvent Confirmation(
        string messageId,
        string quotedMessageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: "xác nhận",
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        quote: new ZaloBridgeMessageQuote(
            quotedMessageId,
            "bot-account",
            "Volley Bot",
            "Long + To An đều ở roster T6. Reply đúng tin này và xác nhận để áp dụng.",
            "chat",
            DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
            null));

    private sealed class BridgeSendHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/v1/group-messages", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            SendCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            LastMessage = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
            var payload = JsonSerializer.Serialize(new
            {
                sent = true,
                mock = true,
                messageId = $"provider-bot-reply-{SendCount}"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NoAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI must not be called by this confirmation flow.");
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
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"confirm-e2e-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            };
            var longProfile = new PlayerProfile
            {
                Id = "profile-long",
                ZaloUserId = "user-long",
                DisplayName = "Long"
            };
            var toAnProfile = new PlayerProfile
            {
                Id = "profile-toan",
                ZaloUserId = "user-toan",
                DisplayName = "To An"
            };
            var session = new MatchSession
            {
                Id = "session-t6",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "session-t6-long",
                SessionId = session.Id,
                PlayerProfileId = longProfile.Id,
                PlayerProfile = longProfile,
                DisplayName = "Long",
                Role = PlayerRole.Attack,
                Level = PlayerLevel.Average,
                Gender = PlayerGender.Male,
                Score = 2,
                IsPresent = true,
                IsCaptainEligible = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "session-t6-toan",
                SessionId = session.Id,
                PlayerProfileId = toAnProfile.Id,
                PlayerProfile = toAnProfile,
                DisplayName = "To An",
                Role = PlayerRole.Defense,
                Level = PlayerLevel.Average,
                Gender = PlayerGender.Female,
                Score = 2,
                IsPresent = true,
                IsCaptainEligible = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(longProfile, toAnProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async Task SaveReadyProposalAsync(
            string senderId,
            string sourceMessageId,
            string providerReplyMessageId)
        {
            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = senderId,
                requesterDisplayName = "Long",
                partnerZaloUserId = "user-toan",
                partnerDisplayName = "To An",
                sessionId = "session-t6",
                sessionName = "T6",
                metadata = new
                {
                    speechAct = "proposal",
                    writeAuthorized = false,
                    domain = "TeamPreference"
                }
            });
            await new ZaloConversationStateV2Store(Db).SaveActiveAsync(
                "g1",
                senderId,
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                collected,
                "[]",
                "[]",
                sourceMessageId,
                sourceMessageId,
                DateTimeOffset.UtcNow.AddMinutes(5));
            await new ZaloMessageGraphStore(Db).RememberOutboundAsync(
                "conn-1",
                "g1",
                providerReplyMessageId,
                sourceMessageId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
