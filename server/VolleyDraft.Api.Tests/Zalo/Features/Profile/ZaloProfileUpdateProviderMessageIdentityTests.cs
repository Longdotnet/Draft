using System.Net;
using System.Text;
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

public sealed class ZaloProfileUpdateProviderMessageIdentityTests
{
    [Fact]
    public async Task Successful_send_without_provider_message_id_does_not_persist_idempotency_key_as_bot_message()
    {
        await using var fixture = await Fixture.CreateAsync(providerMessageId: null);

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(fixture.Command("incoming-no-provider-id"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ZaloGroupMessages.AsNoTracking().Where(message => message.IsFromBot).ToListAsync());
        Assert.Equal("profile-update-conversation:incoming-no-provider-id:updated", fixture.Bridge.LastIdempotencyKey);
    }

    [Fact]
    public async Task Successful_send_with_provider_message_id_persists_real_bot_message_for_future_quote_context()
    {
        await using var fixture = await Fixture.CreateAsync(providerMessageId: "provider-message-42");

        var handled = await fixture.Service.TryHandleZaloProfileUpdatePreRouteAsync(fixture.Command("incoming-provider-id"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        var botMessage = await fixture.Db.ZaloGroupMessages.AsNoTracking().SingleAsync(message => message.IsFromBot);
        Assert.Equal("provider-message-42", botMessage.MessageId);
        Assert.NotEqual(fixture.Bridge.LastIdempotencyKey, botMessage.MessageId);
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

        private SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }

        public static async Task<Fixture> CreateAsync(string? providerMessageId)
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
                    ["Zalo:CredentialEncryptionKey"] = "profile-provider-message-id-test-key",
                    ["ZaloBot:Ambient:Enabled"] = "false"
                })
                .Build();
            var protector = new ZaloCredentialProtector(configuration);
            var bridgeHandler = new RecordingBridgeHandler(providerMessageId);
            var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            });

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"provider-message-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zaloConnection = new ZaloConnection
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
            var session = new MatchSession
            {
                Id = "session-1",
                AdminUserId = admin.Id,
                ZaloConnectionId = zaloConnection.Id,
                ZaloConnection = zaloConnection,
                ZaloGroupId = "group-1",
                Name = "Kèo test",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                BotOperatorZaloUserIdsJson = "[\"admin-zalo\"]",
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "player-hiep",
                SessionId = session.Id,
                PlayerProfileId = profile.Id,
                PlayerProfile = profile,
                DisplayName = profile.DisplayName,
                Gender = PlayerGender.Unknown,
                Role = PlayerRole.Attack,
                Level = PlayerLevel.New,
                IsPresent = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zaloConnection);
            db.PlayerProfiles.Add(profile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var integration = new ZaloIntegrationService(db, bridge, protector, null!, null!, null!);
            var service = new ZaloOverbookService(
                db,
                bridge,
                protector,
                integration,
                null!,
                configuration,
                NullLogger<ZaloOverbookService>.Instance);
            return new Fixture(connection, db, bridgeHandler, service);
        }

        public ZaloIncomingMessageEvent Command(string messageId)
        {
            const string content = "@Npc cập nhật hồ sơ @Hiệp Hoàng Phạm : nam, trung bình, full-stack";
            const string targetLabel = "@Hiệp Hoàng Phạm";
            var targetPos = content.IndexOf(targetLabel, StringComparison.Ordinal);
            return new ZaloIncomingMessageEvent(
                "bot-account",
                "bot-account",
                "group-1",
                messageId,
                "admin-zalo",
                "Admin",
                content,
                [
                    new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                    new ZaloBridgeMention("uid-hiep", targetPos, targetLabel.Length)
                ],
                mentionedBot: true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                quote: null);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    public sealed class RecordingBridgeHandler(string? providerMessageId) : HttpMessageHandler
    {
        public string LastIdempotencyKey { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                LastIdempotencyKey = document.RootElement.GetProperty("idempotencyKey").GetString() ?? string.Empty;
                var messageProperty = providerMessageId is null
                    ? string.Empty
                    : $",\"messageId\":\"{providerMessageId}\"";
                return Json(HttpStatusCode.OK, $"{{\"sent\":true,\"mock\":false{messageProperty}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}