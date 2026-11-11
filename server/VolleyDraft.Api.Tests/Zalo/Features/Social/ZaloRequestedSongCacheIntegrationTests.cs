using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloRequestedSongCacheIntegrationTests
{
    [Fact]
    public async Task Authorized_cache_hit_sends_native_video_without_github_dispatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTargetAsync(db, "[\"operator\"]");

        var now = DateTimeOffset.UtcNow;
        var cacheKey = ZaloTrendMusicService.RequestedSongCacheKey(
            "Gói con tim làm quà",
            showLogo: true);
        var cache = new ZaloTrendMusicRenderCacheStore(db);
        await cache.UpsertAsync(
            CacheEntry(cacheKey, now),
            now,
            24,
            96L * 1024 * 1024,
            CancellationToken.None);

        var bridgeHandler = new RecordingBridgeHandler();
        var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });
        var factory = new RecordingHttpClientFactory();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = Configuration();
        var service = new ZaloTrendMusicService(
            db,
            factory,
            new ZaloTrendMusicRenderDispatcher(factory),
            bridge,
            memoryCache,
            configuration,
            NullLogger<ZaloTrendMusicService>.Instance);
        var incoming = Incoming("operator", "message-cache-hit");

        var handled = await service.TryHandleCommandAsync(incoming);

        Assert.True(handled);
        Assert.Equal(0, factory.CreateClientCount);
        Assert.Equal(1, bridgeHandler.VideoRequestCount);
        Assert.Equal("/v1/group-videos", bridgeHandler.LastPath);
        using (var document = JsonDocument.Parse(bridgeHandler.LastBody!))
        {
            var root = document.RootElement;
            Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), root.GetProperty("videoBase64").GetString());
            Assert.Equal(Convert.ToBase64String([5, 6]), root.GetProperty("thumbnailBase64").GetString());
            Assert.Equal(75_000, root.GetProperty("durationMs").GetInt32());
            Assert.StartsWith("trend-music:request:", root.GetProperty("idempotencyKey").GetString());
        }

        var touched = await cache.TryGetAsync(cacheKey, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(touched);
        Assert.Equal(1, touched.UseCount);
    }

    [Fact]
    public async Task Unauthorized_user_cannot_send_from_global_cache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTargetAsync(db, "[]");

        var now = DateTimeOffset.UtcNow;
        var cacheKey = ZaloTrendMusicService.RequestedSongCacheKey(
            "Gói con tim làm quà",
            showLogo: true);
        var cache = new ZaloTrendMusicRenderCacheStore(db);
        await cache.UpsertAsync(
            CacheEntry(cacheKey, now),
            now,
            24,
            96L * 1024 * 1024,
            CancellationToken.None);

        var bridgeHandler = new RecordingBridgeHandler();
        var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });
        var factory = new RecordingHttpClientFactory();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new ZaloTrendMusicService(
            db,
            factory,
            new ZaloTrendMusicRenderDispatcher(factory),
            bridge,
            memoryCache,
            Configuration(),
            NullLogger<ZaloTrendMusicService>.Instance);

        var handled = await service.TryHandleCommandAsync(Incoming("not-operator", "message-denied"));

        Assert.True(handled);
        Assert.Equal(0, bridgeHandler.VideoRequestCount);
        var cached = await cache.TryGetAsync(cacheKey, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(0, cached.UseCount);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:TrendMusic:CallbackKey"] = new string('k', 32),
                ["ZaloBot:Ambient:TrendMusic:Enabled"] = "true",
                ["ZaloBot:Ambient:TrendMusic:ShowZyroLogo"] = "true"
            })
            .Build();

    private static ZaloIncomingMessageEvent Incoming(string senderId, string messageId) =>
        new(
            "bot-account",
            "bot-account",
            "group-1",
            messageId,
            senderId,
            "Tester",
            "lấy nhạc \"Gói con tim làm quà\"",
            [],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static async Task SeedTargetAsync(VolleyDraftDbContext db, string operatorIdsJson)
    {
        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = $"cache-{Guid.NewGuid():N}@example.test",
            PasswordHash = "test"
        };
        var connection = new ZaloConnection
        {
            Id = "connection-1",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Bot",
            EncryptedCredentials = string.Empty,
            Status = ZaloConnectionStatus.Connected
        };
        var session = new MatchSession
        {
            Id = "session-1",
            Name = "Cache test",
            AdminUserId = admin.Id,
            AdminUser = admin,
            ZaloConnectionId = connection.Id,
            ZaloConnection = connection,
            ZaloGroupId = "group-1",
            BotEnabled = true,
            BotOperatorZaloUserIdsJson = operatorIdsJson,
            Status = SessionStatus.Setup,
            StartTime = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Users.Add(admin);
        db.ZaloConnections.Add(connection);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static ZaloTrendMusicRenderCacheEntry CacheEntry(string cacheKey, DateTimeOffset now) =>
        new(
            cacheKey,
            "Gói con tim làm quà",
            "itunes-6796615117",
            "Gói con tim làm quà",
            "Nguyễn Hùng",
            1,
            [1, 2, 3, 4],
            [5, 6],
            75_000,
            720,
            1280,
            "https://music.apple.com/vn/album/example",
            null,
            "Apple Music preview",
            now,
            now,
            now.AddDays(30),
            0);

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCount++;
            throw new InvalidOperationException($"HTTP client '{name}' must not be used on a cache hit.");
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int VideoRequestCount { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (LastPath == "/v1/group-videos")
            {
                VideoRequestCount++;
                return Json(HttpStatusCode.OK, "{\"sent\":true,\"mock\":true,\"messageId\":\"cache-video-1\"}");
            }
            if (LastPath == "/v1/group-messages")
                return Json(HttpStatusCode.OK, "{\"sent\":true,\"mock\":true,\"messageId\":\"text-1\"}");
            throw new InvalidOperationException($"Unexpected bridge route {LastPath}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
