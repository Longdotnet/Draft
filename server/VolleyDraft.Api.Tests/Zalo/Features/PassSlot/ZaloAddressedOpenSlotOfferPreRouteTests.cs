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

public sealed class ZaloAddressedOpenSlotOfferPreRouteTests
{
    [Fact]
    public async Task Explicit_bot_mention_claims_existing_offer_without_AI()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenOfferAsync();
        var incoming = fixture.ExplicitClaim("claim-addressed-1");

        var handled = await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("bỏ vote", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dịch vụ AI", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var pending = await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending!.Status);
        Assert.Equal("claimant", pending.ClaimantZaloUserId);
    }

    [Fact]
    public async Task Reply_to_bot_claims_existing_offer_without_fresh_textual_mention()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenOfferAsync();
        var incoming = fixture.ReplyClaim("claim-reply-1");

        var handled = await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.True(incoming.MentionedBot);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("bỏ vote", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dịch vụ AI", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scoped_claim_after_offer_disappears_gets_grounded_deterministic_clarification()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = fixture.ExplicitClaim("claim-stale-1");

        var handled = await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("không còn thấy slot pass", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.SessionName, fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dịch vụ AI", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unmentioned_or_unrelated_turn_is_not_stolen_from_existing_routing()
    {
        await using var fixture = await Fixture.CreateAsync();

        var ambient = fixture.UnmentionedClaim("ambient-1");
        var unrelated = fixture.Explicit("unrelated-1", $"@Npc ai đang đánh {fixture.SessionName}?");

        Assert.False(await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(ambient));
        Assert.False(await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(unrelated));
        Assert.Equal(0, fixture.Bridge.SendCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service,
            string sessionName)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            Service = service;
            SessionName = sessionName;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }
        public string SessionName { get; }

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
                Id = "admin",
                DisplayName = "Admin",
                Email = $"addressed-open-slot-{Guid.NewGuid():n}@example.test",
                PasswordHash = "x"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "x"
            };
            var ownerProfile = new PlayerProfile
            {
                Id = "owner-profile",
                ZaloUserId = "owner",
                DisplayName = "Trí"
            };
            var claimantProfile = new PlayerProfile
            {
                Id = "claimant-profile",
                ZaloUserId = "claimant",
                DisplayName = "Trương Công Danh"
            };
            var localStart = NextSundayAt18();
            var sessionName = $"CN {localStart.Day}/{localStart.Month}";
            var session = new MatchSession
            {
                Id = "session-cn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = sessionName,
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = localStart.ToUniversalTime(),
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.AddRange([
                new SessionPlayer
                {
                    Id = "owner-player",
                    SessionId = session.Id,
                    PlayerProfileId = ownerProfile.Id,
                    PlayerProfile = ownerProfile,
                    DisplayName = ownerProfile.DisplayName,
                    IsPresent = true,
                    Role = PlayerRole.Attack,
                    Level = PlayerLevel.Average,
                    Gender = PlayerGender.Male,
                    Score = 2
                },
                new SessionPlayer
                {
                    Id = "claimant-player",
                    SessionId = session.Id,
                    PlayerProfileId = claimantProfile.Id,
                    PlayerProfile = claimantProfile,
                    DisplayName = claimantProfile.DisplayName,
                    IsPresent = false,
                    Role = PlayerRole.Defense,
                    Level = PlayerLevel.Average,
                    Gender = PlayerGender.Male,
                    Score = 2
                }
            ]);

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(ownerProfile, claimantProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "addressed-open-slot-test-key",
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
            return new Fixture(sqlite, db, bridgeHandler, service, sessionName);
        }

        public async Task OpenOfferAsync()
        {
            var assist = new ZaloMemberAssistService(Db);
            var opened = await assist.TryBuildAsync(
                "conn",
                "g1",
                new ZaloIncomingMessageEvent(
                    "bot-account",
                    "bot-account",
                    "g1",
                    "owner-pass",
                    "owner",
                    "Trí",
                    "pass slot CN",
                    [],
                    false,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.NotNull(opened);
            Db.ChangeTracker.Clear();
        }

        public ZaloIncomingMessageEvent ExplicitClaim(string id) =>
            Explicit(id, $"@Npc tui nhận {SessionName}");

        public ZaloIncomingMessageEvent Explicit(string id, string content) => new(
            "bot-account",
            "bot-account",
            "g1",
            id,
            "claimant",
            "Trương Công Danh",
            content,
            [new ZaloBridgeMention("bot-account", 0, "@Npc".Length)],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public ZaloIncomingMessageEvent ReplyClaim(string id) => new(
            "bot-account",
            "bot-account",
            "g1",
            id,
            "claimant",
            "Trương Công Danh",
            $"tui nhận {SessionName}",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "provider-rescue",
                "bot-account",
                "Npc",
                $"Slot vẫn đang mở, ai hốt nói ‘tui nhận {SessionName}’.",
                "chat",
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                null));

        public ZaloIncomingMessageEvent UnmentionedClaim(string id) => new(
            "bot-account",
            "bot-account",
            "g1",
            id,
            "claimant",
            "Trương Công Danh",
            $"tui nhận {SessionName}",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        private static DateTimeOffset NextSundayAt18()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
            var days = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
            if (days == 0) days = 7;
            var date = now.Date.AddDays(days);
            return new DateTimeOffset(date.Year, date.Month, date.Day, 18, 0, 0, TimeSpan.FromHours(7));
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
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-addressed-slot-{SendCount}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
