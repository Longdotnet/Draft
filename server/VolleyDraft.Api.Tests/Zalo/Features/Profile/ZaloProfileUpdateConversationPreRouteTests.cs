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

public sealed class ZaloProfileUpdateConversationPreRouteTests
{
    [Fact]
    public async Task Screenshot_shaped_explicit_Friday_update_owns_turn_and_accepts_full_stack_hyphen()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, full-stack thứ 6";

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("update-t6", command, "admin-zalo"));

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("Đã cập nhật", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bot tiếp tục xử lý theo trạng thái hiện tại", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("profile-update-conversation:update-t6:updated", fixture.Bridge.LastIdempotencyKey);

        fixture.Db.ChangeTracker.Clear();
        var t6 = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-t6-hiep");
        var t7 = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-t7-hiep");
        Assert.Equal(PlayerGender.Male, t6.Gender);
        Assert.Equal(PlayerRole.FullStack, t6.Role);
        Assert.Equal(PlayerLevel.Average, t6.Level);
        Assert.Equal(PlayerGender.Unknown, t7.Gender);
        Assert.Equal(PlayerRole.Attack, t7.Role);
        Assert.Equal(PlayerLevel.New, t7.Level);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_update_persists_typed_arguments_and_short_follow_up_applies_without_AI()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, full-stack";

        var firstHandled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("update-ambiguous", command, "admin-zalo"));

        Assert.True(firstHandled);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.UpdatePlayerProfile.ToString(), pending.PendingIntent);
        Assert.Contains("uid-hiep", pending.PendingPayloadJson, StringComparison.Ordinal);
        Assert.Contains("không cần gõ lại lệnh", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("profile-update-conversation:update-ambiguous:session_clarification", fixture.Bridge.LastIdempotencyKey);
        await fixture.AssertAllSessionFieldsStillInitialAsync();

        var followUpHandled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("update-follow-up", "T6", "admin-zalo"));

        Assert.True(followUpHandled);
        fixture.Db.ChangeTracker.Clear();
        var t6 = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-t6-hiep");
        Assert.Equal(PlayerGender.Male, t6.Gender);
        Assert.Equal(PlayerRole.FullStack, t6.Role);
        Assert.Equal(PlayerLevel.Average, t6.Level);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Equal(2, fixture.Bridge.SendCount);
        Assert.Equal("profile-update-conversation:update-follow-up:updated", fixture.Bridge.LastIdempotencyKey);
    }

    [Fact]
    public async Task Unrelated_chat_does_not_consume_pending_profile_update()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, toàn diện";
        Assert.True(await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("pending-start", command, "admin-zalo")));

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("ordinary-chat", "nay em lên trễ tầm 30p nha", "admin-zalo"));

        Assert.False(handled);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.UpdatePlayerProfile.ToString(), pending.PendingIntent);
        await fixture.AssertAllSessionFieldsStillInitialAsync();
    }

    [Fact]
    public async Task Another_sender_cannot_consume_someone_elses_pending_profile_update()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, toàn diện";
        Assert.True(await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("pending-owner", command, "admin-zalo")));

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("other-user-follow-up", "T6", "other-zalo"));

        Assert.False(handled);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal("admin-zalo", pending.SenderZaloUserId);
        await fixture.AssertAllSessionFieldsStillInitialAsync();
    }

    [Fact]
    public async Task Unauthorized_third_party_cannot_update_another_members_profile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, toàn diện thứ 6";

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("unauthorized-update", command, "other-zalo"));

        Assert.True(handled);
        Assert.Contains("chỉ admin", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("profile-update-conversation:unauthorized-update:unauthorized", fixture.Bridge.LastIdempotencyKey);
        fixture.Db.ChangeTracker.Clear();
        await fixture.AssertAllSessionFieldsStillInitialAsync();
    }

    [Fact]
    public async Task Expired_pending_update_is_discarded_and_cannot_mutate_from_late_selector()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, toàn diện";
        Assert.True(await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Command("expiring-start", command, "admin-zalo")));

        var state = await fixture.Db.ZaloBotConversationStates.SingleAsync();
        state.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(
            fixture.Plain("late-selector", "T6", "admin-zalo"));

        Assert.False(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        await fixture.AssertAllSessionFieldsStillInitialAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            Service = service;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }

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
                    ["Zalo:CredentialEncryptionKey"] = "profile-update-conversation-test-key",
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
                Email = $"profile-update-{Guid.NewGuid():n}@example.test",
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
                DisplayName = "Hiệp Hoàng Phạm",
                Gender = null,
                DefaultRole = null,
                DefaultLevel = null
            };

            db.Users.Add(admin);
            db.ZaloConnections.Add(connectionRow);
            db.PlayerProfiles.Add(profile);
            foreach (var (id, name, day) in new[]
                     {
                         ("session-t6", "T6 04/09 17:30", DayOfWeek.Friday),
                         ("session-t7", "T7 05/09 17:30", DayOfWeek.Saturday),
                         ("session-cn", "CN 06/09 17:30", DayOfWeek.Sunday)
                     })
            {
                var session = new MatchSession
                {
                    Id = id,
                    AdminUserId = admin.Id,
                    ZaloConnectionId = connectionRow.Id,
                    ZaloConnection = connectionRow,
                    ZaloGroupId = "g1",
                    Name = name,
                    Status = SessionStatus.Setup,
                    BotEnabled = true,
                    BotOperatorZaloUserIdsJson = "[\"admin-zalo\"]",
                    StartTime = ZaloTestDates.Next(day),
                    TeamCount = 3,
                    TeamSize = 6
                };
                session.Players.Add(new SessionPlayer
                {
                    Id = $"player-{(id == "session-cn" ? "cn" : id[^2..])}-hiep",
                    SessionId = id,
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
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var integration = new ZaloIntegrationService(
                db,
                bridge,
                protector,
                null!,
                null!,
                null!);
            var service = new ZaloOverbookService(
                db,
                bridge,
                protector,
                integration,
                null!,
                configuration,
                NullLogger<ZaloOverbookService>.Instance);
            return new Fixture(sql, db, bridgeHandler, service);
        }

        public ZaloIncomingMessageEvent Command(string messageId, string content, string senderId)
        {
            const string targetLabel = "@Hiệp Hoàng Phạm";
            var targetPos = content.IndexOf(targetLabel, StringComparison.Ordinal);
            return new ZaloIncomingMessageEvent(
                "bot-account",
                "bot-account",
                "g1",
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

        public ZaloIncomingMessageEvent Plain(string messageId, string content, string senderId) => new(
            "bot-account",
            "bot-account",
            "g1",
            messageId,
            senderId,
            senderId,
            content,
            [],
            mentionedBot: false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: null);

        public async Task AssertAllSessionFieldsStillInitialAsync()
        {
            var players = await Db.SessionPlayers.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
            Assert.Equal(3, players.Count);
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
        public string LastMessage { get; private set; } = string.Empty;
        public string LastIdempotencyKey { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount += 1;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                LastMessage = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
                LastIdempotencyKey = document.RootElement.GetProperty("idempotencyKey").GetString() ?? string.Empty;
                return Json(HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-profile-update-{SendCount}\"}}");
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/groups/g1/roles", StringComparison.Ordinal) == true)
            {
                return Json(HttpStatusCode.OK,
                    "{\"groupId\":\"g1\",\"creatorId\":\"owner-zalo\",\"adminIds\":[]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
