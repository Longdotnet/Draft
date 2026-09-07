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

public sealed class ZaloPassSlotGuidanceTrackedGroupTests
{
    [Fact]
    public async Task Tracked_group_without_match_session_still_receives_deterministic_guidance()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-guidance");

        var handled = await fixture.Service.TryHandlePassSlotGuidancePreRouteAsync(
            fixture.Incoming("bot-account_0", "g-guidance_0"));

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("pass slot T6", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.MatchSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Tracked_group_guidance_does_not_cross_provider_account_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-guidance");

        var handled = await fixture.Service.TryHandlePassSlotGuidancePreRouteAsync(
            fixture.Incoming("different-account", "g-guidance"));

        Assert.False(handled);
        Assert.Equal(0, fixture.Bridge.SendCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection sqlite,
            VolleyDraftDbContext db,
            User admin,
            ZaloConnection connection,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service)
        {
            Sqlite = sqlite;
            Db = db;
            Admin = admin;
            Connection = connection;
            Bridge = bridge;
            Service = service;
        }

        public SqliteConnection Sqlite { get; }
        public VolleyDraftDbContext Db { get; }
        public User Admin { get; }
        public ZaloConnection Connection { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(sqlite)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-guidance",
                DisplayName = "Admin",
                Email = $"pass-guidance-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connection = new ZaloConnection
            {
                Id = "conn-guidance",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(connection);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "pass-guidance-test-key",
                    ["ZaloBot:Ambient:Enabled"] = "false"
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

            return new Fixture(sqlite, db, admin, connection, bridgeHandler, service);
        }

        public async Task AddTrackedGroupAsync(string groupId)
        {
            await new ZaloAutoSessionStore(Db).EnsureAsync();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await Db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "ZaloTrackedGroups" (
                    "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "AutoSessionEnabled", "CreatedAt", "UpdatedAt")
                VALUES (
                    {{Guid.NewGuid().ToString("n")}}, {{Admin.Id}}, {{Connection.Id}}, {{groupId}}, {{groupId}}, 0, {{now}}, {{now}});
                """);
            Db.ChangeTracker.Clear();
        }

        public ZaloIncomingMessageEvent Incoming(string accountId, string groupId) => new(
            accountId,
            "bot-account",
            groupId,
            Guid.NewGuid().ToString("n"),
            "user-1",
            "Long",
            "@Npc ai pass slot thì gõ sao?",
            [new ZaloBridgeMention("bot-account", 0, "@Npc".Length)],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Sqlite.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount += 1;
                LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-guidance-{SendCount}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
