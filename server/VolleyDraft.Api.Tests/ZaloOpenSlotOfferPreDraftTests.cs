using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotOfferPreDraftTests
{
    [Fact]
    public async Task Pass_then_claim_then_poll_verification_completes_without_bot_roster_mutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assist = new ZaloMemberAssistService(fixture.Db);

        var opened = await assist.TryBuildAsync("conn", "g1", Message("m1", "owner", "Hoàng Nguyên", "em pass slot T6 nha"));
        Assert.NotNull(opened);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, opened!.Kind);
        Assert.Contains("tui nhận", opened.Text, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.SessionPlayers.Where(item => item.Id == "owner-player").Select(item => item.IsPresent).SingleAsync());
        Assert.False(await fixture.Db.SessionPlayers.Where(item => item.Id == "claimant-player").Select(item => item.IsPresent).SingleAsync());

        var claimed = await assist.TryBuildAsync("conn", "g1", Message("m2", "claimant", "Vivian", "tui nhận"));
        Assert.NotNull(claimed);
        Assert.Equal(ZaloMemberAssistKind.OpenSlotClaim, claimed!.Kind);
        Assert.Contains("bỏ vote", claimed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vote T6", claimed.Text, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var stillWaiting = await assist.TryBuildAsync("conn", "g1", Message("m3", "claimant", "Vivian", "xong"));
        Assert.NotNull(stillWaiting);
        Assert.Contains("chưa thấy", stillWaiting!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(await fixture.Db.SessionPlayers.Where(item => item.Id == "owner-player").Select(item => item.IsPresent).SingleAsync());
        Assert.False(await fixture.Db.SessionPlayers.Where(item => item.Id == "claimant-player").Select(item => item.IsPresent).SingleAsync());

        await fixture.Db.SessionPlayers.Where(item => item.Id == "owner-player")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsPresent, false));
        await fixture.Db.SessionPlayers.Where(item => item.Id == "claimant-player")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsPresent, true));
        fixture.Db.ChangeTracker.Clear();

        var completed = await assist.TryBuildAsync("conn", "g1", Message("m4", "claimant", "Vivian", "xong"));
        Assert.NotNull(completed);
        Assert.Contains("coi như chốt", completed!.Text, StringComparison.OrdinalIgnoreCase);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        Assert.Null(await store.LoadPendingClaimAsync("g1", "claimant"));
        Assert.Empty(await store.ListClaimableAsync("g1", "another"));
    }

    [Fact]
    public async Task Claim_that_mentions_unrelated_human_does_not_consume_offer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assist = new ZaloMemberAssistService(fixture.Db);
        Assert.NotNull(await assist.TryBuildAsync("conn", "g1", Message("m1", "owner", "Hoàng Nguyên", "pass slot T6")));

        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m2",
            senderId: "claimant",
            senderName: "Vivian",
            content: "tui nhận slot T6 @Nam",
            mentions: [new ZaloBridgeMention("nam-uid", "tui nhận slot T6 ".Length, "@Nam".Length)],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var result = await assist.TryBuildAsync("conn", "g1", incoming);

        Assert.Null(result);
        Assert.Single(await new ZaloOpenSlotOfferStore(fixture.Db).ListClaimableAsync("g1", "claimant"));
    }

    private static ZaloIncomingMessageEvent Message(string id, string senderId, string senderName, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: id,
        senderId: senderId,
        senderName: senderName,
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

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
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"open-slot-{Guid.NewGuid():n}@example.test",
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
                DisplayName = "Hoàng Nguyên"
            };
            var claimantProfile = new PlayerProfile
            {
                Id = "claimant-profile",
                ZaloUserId = "claimant",
                DisplayName = "Vivian"
            };
            var session = new MatchSession
            {
                Id = "s1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddHours(6)
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
                    Gender = PlayerGender.Female,
                    Score = 2
                }
            ]);

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(ownerProfile, claimantProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
