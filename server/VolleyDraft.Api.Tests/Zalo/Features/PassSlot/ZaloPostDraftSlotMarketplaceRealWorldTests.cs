using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPostDraftSlotMarketplaceRealWorldTests
{
    [Fact]
    public async Task Owner_repeating_pass_does_not_erase_live_claim()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assist = new ZaloMemberAssistService(fixture.Db);

        var opened = await assist.TryBuildAsync("conn", "g1", Message("m1", "owner", "Hoàng Nguyên", "pass slot T6"));
        Assert.NotNull(opened);
        var claimed = await assist.TryBuildAsync("conn", "g1", Message("m2", "claimant", "Vivian", "tui nhận"));
        Assert.NotNull(claimed);

        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var before = await store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(before);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, before!.Status);

        var repeated = await assist.TryBuildAsync("conn", "g1", Message("m3", "owner", "Hoàng Nguyên", "pass slot T6 lần nữa nha"));

        Assert.NotNull(repeated);
        Assert.Contains("reservation", repeated!.Text, StringComparison.OrdinalIgnoreCase);
        var after = await store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(after);
        Assert.Equal(before.Id, after!.Id);
        Assert.Equal("claimant", after.ClaimantZaloUserId);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, after.Status);
    }

    [Fact]
    public async Task Claimant_cannot_cancel_once_apply_has_started()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant"));

        var service = new ZaloOpenSlotOfferService(fixture.Db);
        var cancelled = await service.TryHandleAsync("conn", "g1", Message("m3", "claimant", "Vivian", "hủy"));

        Assert.True(cancelled.Handled);
        Assert.Contains("không nhả", cancelled.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var pending = await store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.Applying, pending!.Status);
    }

    [Fact]
    public async Task Owner_cannot_cancel_once_apply_has_started()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant"));

        var service = new ZaloOpenSlotOfferService(fixture.Db);
        var cancelled = await service.TryHandleAsync("conn", "g1", Message("m3", "owner", "Hoàng Nguyên", "hủy pass slot"));

        Assert.True(cancelled.Handled);
        Assert.Contains("không huỷ ngang", cancelled.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var active = Assert.Single(await store.ListOwnedActiveAsync("conn", "g1", "owner"));
        Assert.Equal(ZaloOpenSlotOfferStatus.Applying, active.Status);
        Assert.Equal("claimant", active.ClaimantZaloUserId);
    }

    [Fact]
    public async Task Owner_cancel_before_apply_cancels_pending_reservation_explicitly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", DateTimeOffset.UtcNow.AddMinutes(10)));

        var service = new ZaloOpenSlotOfferService(fixture.Db);
        var cancelled = await service.TryHandleAsync("conn", "g1", Message("m3", "owner", "Hoàng Nguyên", "hủy pass slot"));

        Assert.True(cancelled.Handled);
        Assert.Contains("Vivian", cancelled.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.ListOwnedActiveAsync("conn", "g1", "owner"));
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
    }

    [Fact]
    public async Task Safety_open_refresh_preserves_claim_even_without_member_assist()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(offer, "claimant", "Vivian", "m2", DateTimeOffset.UtcNow.AddMinutes(10)));

        var safety = new ZaloOpenSlotMarketplaceSafetyStore(fixture.Db);
        var result = await safety.OpenOrRefreshAsync(
            "conn", "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m3",
            DateTimeOffset.UtcNow.AddHours(3), DateTimeOffset.UtcNow.AddMinutes(40));

        Assert.Equal(ZaloOpenSlotOpenDisposition.ClaimPreserved, result.Disposition);
        Assert.Equal("claimant", result.Offer.ClaimantZaloUserId);
        var pending = await store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending!.Status);
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
                Email = $"marketplace-real-{Guid.NewGuid():n}@example.test",
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
