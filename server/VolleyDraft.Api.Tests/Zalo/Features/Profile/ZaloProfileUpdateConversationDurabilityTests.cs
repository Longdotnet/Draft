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

public sealed class ZaloProfileUpdateConversationDurabilityTests
{
    [Fact]
    public async Task Pending_profile_update_survives_service_reconstruction_before_short_follow_up()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstService = fixture.CreateService();

        Assert.True(await firstService.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("restart-start", "g1", "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, full-stack", "admin-zalo")));

        fixture.Db.ChangeTracker.Clear();
        Assert.Single(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());

        // Reconstruct the service to model a deploy/process restart. Durable conversation
        // state must be sufficient; no in-memory object from the first turn may be required.
        var reconstructedService = fixture.CreateService();
        Assert.True(await reconstructedService.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("restart-follow-up", "g1", "T6", "admin-zalo")));

        fixture.Db.ChangeTracker.Clear();
        var t6 = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-g1-t6-hiep");
        Assert.Equal(PlayerGender.Male, t6.Gender);
        Assert.Equal(PlayerRole.FullStack, t6.Role);
        Assert.Equal(PlayerLevel.Average, t6.Level);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Equal("profile-update-conversation:restart-follow-up:updated", fixture.Bridge.LastIdempotencyKey);
    }

    [Fact]
    public async Task Same_sender_in_another_group_cannot_consume_pending_profile_update()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        Assert.True(await service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("group-scope-start", "g1", "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, toàn diện", "admin-zalo")));

        var handledInOtherGroup = await service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("group-scope-other", "g2", "T6", "admin-zalo"));

        Assert.False(handledInOtherGroup);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal("g1", pending.GroupId);
        Assert.Equal("admin-zalo", pending.SenderZaloUserId);
        await fixture.AssertGroupStillInitialAsync("g1");
        await fixture.AssertGroupStillInitialAsync("g2");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloBridgeClient bridgeClient,
            ZaloCredentialProtector protector,
            IConfiguration configuration)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            BridgeClient = bridgeClient;
            Protector = protector;
            Configuration = configuration;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        private ZaloBridgeClient BridgeClient { get; }
        private ZaloCredentialProtector Protector { get; }
        private IConfiguration Configuration { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sql = new SqliteConnection("Data Source=:memory:");
            await sql.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(sql)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "profile-update-durability-test-key",
                    ["ZaloBot:ConversationTtlMinutes"] = "15",
                    ["ZaloBot:Ambient:Enabled"] = "false"
                })
                .Build();
            var protector = new ZaloCredentialProtector(configuration);
            var bridgeHandler = new RecordingBridgeHandler();
            var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            });

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"profile-update-durability-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connectionRow = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = protector.Protect("{}")
            };
            var profile = new PlayerProfile
            {
                Id = "profile-hiep",
                ZaloUserId = "uid-hiep",
                DisplayName = "Hiệp Hoàng Phạm"
            };

            db.Users.Add(admin);
            db.ZaloConnections.Add(connectionRow);
            db.PlayerProfiles.Add(profile);

            foreach (var group in new[] { "g1", "g2" })
            {
                foreach (var (suffix, name, day) in new[]
                         {
                             ("t6", "T6", DayOfWeek.Friday),
                             ("t7", "T7", DayOfWeek.Saturday)
                         })
                {
                    var session = new MatchSession
                    {
                        Id = $"session-{group}-{suffix}",
                        AdminUserId = admin.Id,
                        ZaloConnectionId = connectionRow.Id,
                        ZaloConnection = connectionRow,
                        ZaloGroupId = group,
                        Name = $"{name} {group}",
                        Status = SessionStatus.Setup,
                        BotEnabled = true,
                        BotOperatorZaloUserIdsJson = "[\"admin-zalo\"]",
                        StartTime = ZaloTestDates.Next(day),
                        TeamCount = 3,
                        TeamSize = 6
                    };
                    session.Players.Add(new SessionPlayer
                    {
                        Id = $"player-{group}-{suffix}-hiep",
                        SessionId = session.Id,
                        PlayerProfileId = profile.Id,
                        PlayerProfile = profile,
                        DisplayName = profile.DisplayName,
                        Gender = PlayerGender.Unknown,
                        Role = PlayerRole.Attack,
                        Level = PlayerLevel.New,
                        IsPresent = true
                    });
                    db.MatchSessions.Add(session);
                }
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(sql, db, bridgeHandler, bridge, protector, configuration);
        }

        public ZaloOverbookService CreateService()
        {
            var integration = new ZaloIntegrationService(Db, BridgeClient, Protector, null!, null!, null!);
            return new ZaloOverbookService(
                Db,
                BridgeClient,
                Protector,
                integration,
                null!,
                Configuration,
                NullLogger<ZaloOverbookService>.Instance);
        }

        public ZaloIncomingMessageEvent Command(string messageId, string groupId, string content, string senderId)
        {
            const string targetLabel = "@Hiệp Hoàng Phạm";
            var targetPos = content.IndexOf(targetLabel, StringComparison.Ordinal);
            return new ZaloIncomingMessageEvent(
                "bot-account",
                "bot-account",
                groupId,
                messageId,
                senderId,
                senderId,
                content,
                [
                    new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                    new ZaloBridgeMention("uid-hiep", targetPos, targetLabel.Length)
                ],
                mentionedBot: true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                quote: null);
        }

        public ZaloIncomingMessageEvent Plain(string messageId, string groupId, string content, string senderId) => new(
            "bot-account",
            "bot-account",
            groupId,
            messageId,
            senderId,
            senderId,
            content,
            [],
            mentionedBot: false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: null);

        public async Task AssertGroupStillInitialAsync(string groupId)
        {
            var players = await Db.SessionPlayers
                .AsNoTracking()
                .Where(player => player.Session.ZaloGroupId == groupId)
                .ToListAsync();
            Assert.Equal(2, players.Count);
            Assert.All(players, player =>
            {
                Assert.Equal(PlayerGender.Unknown, player.Gender);
                Assert.Equal(PlayerRole.Attack, player.Role);
                Assert.Equal(PlayerLevel.New, player.Level);
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string LastIdempotencyKey { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                LastIdempotencyKey = document.RootElement.GetProperty("idempotencyKey").GetString() ?? string.Empty;
                return Json(HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-durability-{SendCount}\"}}");
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.Contains("/roles", StringComparison.Ordinal) == true)
            {
                var groupId = request.RequestUri.AbsolutePath.Contains("g2", StringComparison.Ordinal) ? "g2" : "g1";
                return Json(HttpStatusCode.OK,
                    $"{{\"groupId\":\"{groupId}\",\"creatorId\":\"owner-zalo\",\"adminIds\":[]}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
