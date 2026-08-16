using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotRescueLifecycleTests
{
    [Fact]
    public async Task Connection_scope_prevents_same_group_id_from_leaking_offers()
    {
        await using var fixture = await Fixture.CreateEmptyAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var now = DateTimeOffset.UtcNow;

        await store.OpenAsync("conn-a", "g1", "owner-a", "A", "s-a", "T6-A", "m1", now.AddHours(2), now.AddMinutes(30));
        await store.OpenAsync("conn-b", "g1", "owner-b", "B", "s-b", "T6-B", "m2", now.AddHours(2), now.AddMinutes(30));

        var a = Assert.Single(await store.ListClaimableAsync("conn-a", "g1", "claimant"));
        var b = Assert.Single(await store.ListClaimableAsync("conn-b", "g1", "claimant"));
        Assert.Equal("T6-A", a.SessionName);
        Assert.Equal("T6-B", b.SessionName);
    }

    [Fact]
    public async Task Due_open_offer_is_resurfaced_once_and_nudge_state_advances()
    {
        await using var fixture = await Fixture.CreateSessionAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(3), now.AddMinutes(-1));

        var handler = new RecordingHandler();
        var service = CreateService(fixture.Db, handler);
        var result = await service.RunDueAsync(now);

        Assert.Equal(1, result.NudgedCount);
        Assert.Equal(1, handler.SendCount);
        Assert.Contains("trôi", handler.LastBody ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var offer = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.Equal(1, offer.NudgeCount);
        Assert.NotNull(offer.LastNudgeAt);
        Assert.True(offer.NextNudgeAt is null || offer.NextNudgeAt > now);

        var second = await service.RunDueAsync(now);
        Assert.Equal(0, second.NudgedCount);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task Recent_bot_message_defers_rescue_to_avoid_group_spam()
    {
        await using var fixture = await Fixture.CreateSessionAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(3), now.AddMinutes(-1));
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn",
            GroupId = "g1",
            MessageId = "recent-bot",
            SenderId = "bot-account",
            SenderName = "Npc",
            Content = "reminder khác",
            IsFromBot = true,
            SentAt = now.AddMinutes(-1),
            ReceivedAt = now.AddMinutes(-1)
        });
        await fixture.Db.SaveChangesAsync();

        var handler = new RecordingHandler();
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(0, result.NudgedCount);
        Assert.True(result.SkippedCount >= 1);
        Assert.Equal(0, handler.SendCount);
        var offer = Assert.Single(await store.ListClaimableAsync("conn", "g1", "someone"));
        Assert.NotNull(offer.NextNudgeAt);
        Assert.True(offer.NextNudgeAt > now);
    }

    [Fact]
    public async Task Claim_timeout_reopens_offer_instead_of_blocking_everyone()
    {
        await using var fixture = await Fixture.CreateSessionAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(3), now.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(
            offer, "claimant", "Vivian", "m2", now.AddMinutes(1)));

        var handler = new RecordingHandler();
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now.AddMinutes(2));

        Assert.Equal(1, result.ClaimReleasedCount);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        var reopened = Assert.Single(await store.ListClaimableAsync("conn", "g1", "another"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, reopened.Status);
        Assert.Null(reopened.ClaimantZaloUserId);
        Assert.Equal(1, handler.SendCount);
        Assert.Contains("mở lại", handler.LastBody ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rescue_closes_stale_offer_when_owner_is_no_longer_present()
    {
        await using var fixture = await Fixture.CreateSessionAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            now.AddHours(3), now.AddMinutes(-1));

        var player = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "owner-player");
        player.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var handler = new RecordingHandler();
        var result = await CreateService(fixture.Db, handler).RunDueAsync(now);

        Assert.Equal(1, result.ClosedCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Empty(await store.ListOwnedActiveAsync("conn", "g1", "owner"));
        Assert.Empty(await store.ListClaimableAsync("conn", "g1", "someone"));
    }

    private static ZaloOpenSlotRescueService CreateService(
        VolleyDraftDbContext db,
        RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:MemberAssist:Rescue:Enabled"] = "true",
                ["ZaloBot:Ambient:MemberAssist:Rescue:MaxNudges"] = "3",
                ["ZaloBot:Ambient:MemberAssist:Rescue:GroupCooldownMinutes"] = "10",
                ["ZaloBot:Ambient:MemberAssist:Rescue:RetryMinutes"] = "10"
            })
            .Build();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new ZaloOpenSlotRescueService(
            db,
            new ZaloBridgeClient(client),
            configuration,
            NullLogger<ZaloOpenSlotRescueService>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount += 1;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sent\":true,\"mock\":true,\"messageId\":\"rescue-message\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
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

        public static async Task<Fixture> CreateEmptyAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public static async Task<Fixture> CreateSessionAsync()
        {
            var fixture = await CreateEmptyAsync();
            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"open-slot-rescue-{Guid.NewGuid():n}@example.test",
                PasswordHash = "x"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "x",
                Status = ZaloConnectionStatus.Connected
            };
            var profile = new PlayerProfile
            {
                Id = "owner-profile",
                ZaloUserId = "owner",
                DisplayName = "Hoàng Nguyên",
                Gender = PlayerGender.Male,
                DefaultRole = PlayerRole.Attack,
                DefaultLevel = PlayerLevel.Average
            };
            var session = new MatchSession
            {
                Id = "s1",
                Name = "T6",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddHours(3),
                Status = SessionStatus.Setup
            };
            var player = new SessionPlayer
            {
                Id = "owner-player",
                SessionId = session.Id,
                Session = session,
                PlayerProfileId = profile.Id,
                PlayerProfile = profile,
                DisplayName = "Hoàng Nguyên",
                IsPresent = true,
                Gender = PlayerGender.Male,
                Role = PlayerRole.Attack,
                Level = PlayerLevel.Average
            };
            session.Players.Add(player);
            fixture.Db.AddRange(admin, zalo, profile, session, player);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
