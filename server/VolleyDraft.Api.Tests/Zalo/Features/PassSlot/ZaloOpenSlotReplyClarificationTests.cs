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

public sealed class ZaloOpenSlotReplyClarificationTests
{
    [Fact]
    public async Task Reply_to_grounded_slot_message_can_select_session_with_short_reference()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenOfferAsync();

        var incoming = fixture.Reply("short-select", "CN", $"Slot Trí ở {fixture.SessionName} vẫn đang mở, ai hốt reply kèo này nha.");
        var handled = await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("bỏ vote", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var pending = await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal("session-cn", pending!.SessionId);
    }

    [Fact]
    public async Task Reply_to_unrelated_bot_message_does_not_turn_short_reference_into_claim()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.OpenOfferAsync();

        var incoming = fixture.Reply("short-unrelated", "CN", "Lịch tuần này tui gửi ở trên nha.");
        var handled = await fixture.Service.TryHandleAddressedOpenSlotOfferPreRouteAsync(incoming);

        Assert.False(handled);
        Assert.Equal(0, fixture.Bridge.SendCount);
        fixture.Db.ChangeTracker.Clear();
        var pending = await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.Null(pending);
    }

    [Theory]
    [InlineData("T6")]
    [InlineData("thứ 6")]
    [InlineData("CN")]
    [InlineData("chủ nhật")]
    public void Short_session_references_are_narrow_and_deterministic(string text)
    {
        Assert.True(ZaloOverbookService.IsShortOpenSlotSelector(text));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("tui vô")]
    [InlineData("ai đánh CN")]
    [InlineData("CN ăn gì")]
    public void Ordinary_chat_is_not_a_short_slot_selector(string text)
    {
        Assert.False(ZaloOverbookService.IsShortOpenSlotSelector(text));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db, RecordingBridge bridge, ZaloOverbookService service, string sessionName)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            Service = service;
            SessionName = sessionName;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridge Bridge { get; }
        public ZaloOverbookService Service { get; }
        public string SessionName { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(sqlite).Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User { Id = "admin", DisplayName = "Admin", Email = $"slot-reply-{Guid.NewGuid():n}@example.test", PasswordHash = "x" };
            var zalo = new ZaloConnection
            {
                Id = "conn", AdminUserId = admin.Id, AdminUser = admin,
                AccountZaloId = "bot-account", DisplayName = "Npc", EncryptedCredentials = "x"
            };
            var owner = new PlayerProfile { Id = "owner-profile", ZaloUserId = "owner", DisplayName = "Trí" };
            var claimant = new PlayerProfile { Id = "claimant-profile", ZaloUserId = "claimant", DisplayName = "Danh" };
            var start = NextSundayAt18();
            var sessionName = $"CN {start.Day}/{start.Month}";
            var session = new MatchSession
            {
                Id = "session-cn", AdminUserId = admin.Id, AdminUser = admin,
                ZaloConnectionId = zalo.Id, ZaloConnection = zalo, ZaloGroupId = "g1",
                Name = sessionName, Status = SessionStatus.Setup, BotEnabled = true,
                StartTime = start.ToUniversalTime(), TeamCount = 3, TeamSize = 6
            };
            session.Players.AddRange([
                new SessionPlayer
                {
                    Id = "owner-player", SessionId = session.Id, PlayerProfileId = owner.Id, PlayerProfile = owner,
                    DisplayName = owner.DisplayName, IsPresent = true, Role = PlayerRole.Attack,
                    Level = PlayerLevel.Average, Gender = PlayerGender.Male, Score = 2
                },
                new SessionPlayer
                {
                    Id = "claimant-player", SessionId = session.Id, PlayerProfileId = claimant.Id, PlayerProfile = claimant,
                    DisplayName = claimant.DisplayName, IsPresent = false, Role = PlayerRole.Defense,
                    Level = PlayerLevel.Average, Gender = PlayerGender.Male, Score = 2
                }
            ]);
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(owner, claimant);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "slot-reply-test-key",
                    ["ZaloBot:Ambient:Enabled"] = "false"
                }).Build();
            var recording = new RecordingBridge();
            var bridge = new ZaloBridgeClient(new HttpClient(recording) { BaseAddress = new Uri("https://bridge.test/") });
            var service = new ZaloOverbookService(db, bridge, new ZaloCredentialProtector(configuration), null!, null!, configuration, NullLogger<ZaloOverbookService>.Instance);
            return new Fixture(sqlite, db, recording, service, sessionName);
        }

        public async Task OpenOfferAsync()
        {
            var opened = await new ZaloMemberAssistService(Db).TryBuildAsync(
                "conn", "g1",
                new ZaloIncomingMessageEvent("bot-account", "bot-account", "g1", "owner-pass", "owner", "Trí", "pass slot CN", [], false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.NotNull(opened);
            Db.ChangeTracker.Clear();
        }

        public ZaloIncomingMessageEvent Reply(string messageId, string content, string quotedContent) => new(
            "bot-account", "bot-account", "g1", messageId, "claimant", "Danh", content, [], false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote("provider-slot-context", "bot-account", "Npc", quotedContent, "chat", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(), null));

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

    private sealed class RecordingBridge : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount++;
                var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var payload = JsonDocument.Parse(raw);
                LastBody = payload.RootElement.GetProperty("message").GetString() ?? string.Empty;
                return Json(HttpStatusCode.OK, $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-slot-reply-{SendCount}\"}}");
            }
            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
