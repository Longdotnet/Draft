using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareSlotIdentityBindingTests
{
    [Fact]
    public async Task Stale_label_with_AnhTu_uid_resolves_to_AnhTu_without_renaming_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(
            new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User { Id = "admin", DisplayName = "Admin", Email = "admin@identity.test", PasswordHash = "x" };
        var session = new MatchSession
        {
            Id = "session",
            Name = "T6",
            AdminUserId = admin.Id,
            AdminUser = admin,
            TeamCount = 3,
            TeamSize = 6
        };
        var anchorProfile = new PlayerProfile { Id = "profile-anchor", ZaloUserId = "uid-anchor", DisplayName = "Long" };
        var anhTuProfile = new PlayerProfile { Id = "profile-anh-tu", ZaloUserId = "uid-anh-tu", DisplayName = "Anh Tú" };
        session.Players.Add(new SessionPlayer
        {
            Id = "player-anchor",
            SessionId = session.Id,
            PlayerProfileId = anchorProfile.Id,
            PlayerProfile = anchorProfile,
            DisplayName = "Long",
            IsPresent = true
        });
        session.Players.Add(new SessionPlayer
        {
            Id = "player-anh-tu",
            SessionId = session.Id,
            PlayerProfileId = anhTuProfile.Id,
            PlayerProfile = anhTuProfile,
            DisplayName = "Anh Tú",
            IsPresent = true
        });
        db.Users.Add(admin);
        db.PlayerProfiles.AddRange(anchorProfile, anhTuProfile);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new SessionDraftService(db);

        // Anh Tú được preview nhưng người dùng bấm hủy: preview tuyệt đối không ghi dữ liệu.
        var anhTuPreview = await service.PreviewShareSlotAsync(
            admin.Id,
            session.Id,
            "Long",
            [new ShareSlotParticipantInput("Anh Tú", "uid-anh-tu")]);
        Assert.True(anhTuPreview.IsSuccess, anhTuPreview.Error);
        Assert.Empty(await db.DraftSlots.AsNoTracking().ToListAsync());

        // Dù label upstream bị stale thành Thanh Tuyền, UID structured mention vẫn
        // chọn đúng Anh Tú. Preview/write canonicalize chứ không rename identity.
        var thanhTuyenPreview = await service.PreviewShareSlotAsync(
            admin.Id,
            session.Id,
            "Long",
            [new ShareSlotParticipantInput("Thanh Tuyền", "uid-anh-tu")]);
        Assert.True(thanhTuyenPreview.IsSuccess, thanhTuyenPreview.Error);
        Assert.NotNull(thanhTuyenPreview.Value);
        Assert.Equal(["Anh Tú"], thanhTuyenPreview.Value!.PartnerPlayerNames);

        var write = await service.SharePreDraftSlotAsync(
            admin.Id,
            session.Id,
            "Long",
            [new ShareSlotParticipantInput("Thanh Tuyền", "uid-anh-tu")]);
        Assert.True(write.IsSuccess, write.Error);

        db.ChangeTracker.Clear();
        var anhTu = await db.SessionPlayers.AsNoTracking()
            .Include(player => player.PlayerProfile)
            .SingleAsync(player => player.Id == "player-anh-tu");
        Assert.Equal("Anh Tú", anhTu.DisplayName);
        Assert.Equal("Anh Tú", anhTu.PlayerProfile!.DisplayName);
        Assert.True(anhTu.IsInsideSharedSlot);
        Assert.DoesNotContain(await db.SessionPlayers.AsNoTracking().ToListAsync(),
            player => player.DisplayName == "Thanh Tuyền");
        Assert.Single(await db.DraftSlots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Existing_uid_profile_with_stale_label_creates_session_player_from_canonical_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(
            new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User { Id = "admin2", DisplayName = "Admin", Email = "admin2@identity.test", PasswordHash = "x" };
        var session = new MatchSession
        {
            Id = "session2",
            Name = "CN",
            AdminUserId = admin.Id,
            AdminUser = admin,
            TeamCount = 3,
            TeamSize = 6
        };
        var anchorProfile = new PlayerProfile { Id = "profile-anchor2", ZaloUserId = "uid-anchor2", DisplayName = "Long" };
        var staleProfile = new PlayerProfile { Id = "profile-stale", ZaloUserId = "uid-stale", DisplayName = "Anh Tú" };
        session.Players.Add(new SessionPlayer
        {
            Id = "player-anchor2",
            SessionId = session.Id,
            PlayerProfileId = anchorProfile.Id,
            PlayerProfile = anchorProfile,
            DisplayName = "Long",
            IsPresent = true
        });
        db.Users.Add(admin);
        db.PlayerProfiles.AddRange(anchorProfile, staleProfile);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new SessionDraftService(db);
        var result = await service.SharePreDraftSlotAsync(
            admin.Id,
            session.Id,
            "Long",
            [new ShareSlotParticipantInput("Thanh Tuyền", "uid-stale")]);

        Assert.True(result.IsSuccess, result.Error);
        db.ChangeTracker.Clear();
        var storedProfile = await db.PlayerProfiles.AsNoTracking().SingleAsync(profile => profile.Id == "profile-stale");
        Assert.Equal("Anh Tú", storedProfile.DisplayName);
        var created = await db.SessionPlayers.AsNoTracking()
            .SingleAsync(player => player.SessionId == session.Id && player.PlayerProfileId == storedProfile.Id);
        Assert.Equal("Anh Tú", created.DisplayName);
        Assert.True(created.IsInsideSharedSlot);
        Assert.DoesNotContain(await db.SessionPlayers.AsNoTracking().Where(player => player.SessionId == session.Id).ToListAsync(),
            player => player.DisplayName == "Thanh Tuyền");
        Assert.Single(await db.DraftSlots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Name_only_session_player_is_not_auto_bound_to_uid_profile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(
            new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User { Id = "admin3", DisplayName = "Admin", Email = "admin3@identity.test", PasswordHash = "x" };
        var session = new MatchSession
        {
            Id = "session3",
            Name = "T7",
            AdminUserId = admin.Id,
            AdminUser = admin,
            TeamCount = 3,
            TeamSize = 6
        };
        var anchorProfile = new PlayerProfile { Id = "profile-anchor3", ZaloUserId = "uid-anchor3", DisplayName = "Long" };
        var thanhTuyenProfile = new PlayerProfile { Id = "profile-thanh-tuyen", ZaloUserId = "uid-thanh-tuyen", DisplayName = "Thanh Tuyền" };
        session.Players.Add(new SessionPlayer
        {
            Id = "player-anchor3",
            SessionId = session.Id,
            PlayerProfileId = anchorProfile.Id,
            PlayerProfile = anchorProfile,
            DisplayName = "Long",
            IsPresent = true
        });
        session.Players.Add(new SessionPlayer
        {
            Id = "manual-thanh-tuyen",
            SessionId = session.Id,
            DisplayName = "Thanh Tuyền",
            IsPresent = true
        });
        db.Users.Add(admin);
        db.PlayerProfiles.AddRange(anchorProfile, thanhTuyenProfile);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new SessionDraftService(db);
        var result = await service.SharePreDraftSlotAsync(
            admin.Id,
            session.Id,
            "Long",
            [new ShareSlotParticipantInput("Thanh Tuyền", "uid-thanh-tuyen")]);

        Assert.False(result.IsSuccess);
        Assert.Contains("không tự gắn UID/profile", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
        var manual = await db.SessionPlayers.AsNoTracking().SingleAsync(player => player.Id == "manual-thanh-tuyen");
        Assert.Null(manual.PlayerProfileId);
        Assert.False(manual.IsInsideSharedSlot);
        Assert.Empty(await db.DraftSlots.AsNoTracking().ToListAsync());
    }
}
