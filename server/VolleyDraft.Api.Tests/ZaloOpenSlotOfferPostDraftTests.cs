using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotOfferPostDraftTests
{
    [Fact]
    public async Task Claim_requires_confirmation_then_uses_existing_safe_slot_transfer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assist = new ZaloMemberAssistService(fixture.Db);

        var opened = await assist.TryBuildAsync("conn", "g1", Message("m1", "owner", "Hoàng Nguyên", "pass slot T6 nha"));
        Assert.NotNull(opened);
        Assert.Contains("tui nhận", opened!.Text, StringComparison.OrdinalIgnoreCase);

        var claimed = await assist.TryBuildAsync("conn", "g1", Message("m2", "claimant", "Vivian", "tui nhận"));
        Assert.NotNull(claimed);
        Assert.Contains("chốt", claimed!.Text, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var beforeLink = await fixture.Db.DraftSlotPlayers
            .Include(item => item.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .SingleAsync(item => item.DraftSlotId == "owner-slot");
        Assert.Equal("owner-player", beforeLink.SessionPlayerId);
        Assert.Equal("owner", beforeLink.SessionPlayer.PlayerProfile!.ZaloUserId);
        Assert.False(await fixture.Db.PlayerProfiles.AnyAsync(item => item.ZaloUserId == "claimant"));

        var confirmed = await assist.TryBuildAsync("conn", "g1", Message("m3", "claimant", "Vivian", "chốt"));
        Assert.NotNull(confirmed);
        Assert.Contains("Done", confirmed!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vivian", confirmed.Text, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var afterLink = await fixture.Db.DraftSlotPlayers
            .Include(item => item.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .SingleAsync(item => item.DraftSlotId == "owner-slot");
        Assert.Equal("claimant", afterLink.SessionPlayer.PlayerProfile!.ZaloUserId);
        Assert.Equal("Vivian", afterLink.SessionPlayer.DisplayName);
        Assert.False(await fixture.Db.SessionPlayers.Where(item => item.Id == "owner-player").Select(item => item.IsPresent).SingleAsync());

        var history = await fixture.Db.ZaloBotActionHistory
            .Where(item => item.SessionId == "s1" && item.ActionType == "SlotTransfer")
            .ToListAsync();
        Assert.Single(history);
        Assert.Contains("Open-slot offer", history[0].Summary, StringComparison.OrdinalIgnoreCase);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        Assert.Null(await store.LoadPendingClaimAsync("g1", "claimant"));
        Assert.Empty(await store.ListClaimableAsync("g1", "another"));
    }

    [Fact]
    public async Task Second_member_cannot_steal_offer_after_first_claimant_reserves_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assist = new ZaloMemberAssistService(fixture.Db);
        Assert.NotNull(await assist.TryBuildAsync("conn", "g1", Message("m1", "owner", "Hoàng Nguyên", "pass slot T6")));
        Assert.NotNull(await assist.TryBuildAsync("conn", "g1", Message("m2", "claimant", "Vivian", "tui nhận")));

        var second = await assist.TryBuildAsync("conn", "g1", Message("m3", "second", "Long", "tui nhận"));

        Assert.Null(second);
        var pending = await new ZaloOpenSlotOfferStore(fixture.Db).LoadPendingClaimAsync("g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal("claimant", pending!.ClaimantZaloUserId);
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
                Email = $"open-slot-post-{Guid.NewGuid():n}@example.test",
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
                DisplayName = "Hoàng Nguyên",
                Gender = PlayerGender.Male,
                DefaultRole = PlayerRole.Attack,
                DefaultLevel = PlayerLevel.Average
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
                Status = SessionStatus.Finished,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddHours(6),
                TeamCount = 3,
                TeamSize = 6
            };
            var owner = new SessionPlayer
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
                Score = 2,
                IsCaptainEligible = false
            };
            var team = new Team
            {
                Id = "team-a",
                SessionId = session.Id,
                Session = session,
                Name = "Team A",
                TotalAverageScore = 2
            };
            var slot = new DraftSlot
            {
                Id = "owner-slot",
                SessionId = session.Id,
                Session = session,
                Type = DraftSlotType.Single,
                DisplayName = owner.DisplayName,
                Role = owner.Role,
                Gender = owner.Gender,
                AverageScore = owner.Score,
                AssignedTeamId = team.Id,
                AssignedTeam = team,
                IsCaptainSlot = false
            };
            slot.Players.Add(new DraftSlotPlayer
            {
                Id = "owner-link",
                DraftSlotId = slot.Id,
                DraftSlot = slot,
                SessionPlayerId = owner.Id,
                SessionPlayer = owner,
                RotationOrder = 1
            });
            session.Players.Add(owner);
            session.Teams.Add(team);
            session.DraftSlots.Add(slot);

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(ownerProfile);
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
