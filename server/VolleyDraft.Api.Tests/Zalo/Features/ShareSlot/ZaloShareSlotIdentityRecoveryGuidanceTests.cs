using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareSlotIdentityRecoveryGuidanceTests
{
    [Fact]
    public async Task Preview_identity_conflict_points_to_in_chat_recovery_without_rebinding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(
            new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = "admin@identity-guidance.test",
            PasswordHash = "x"
        };
        var session = new MatchSession
        {
            Id = "session",
            Name = "T6",
            AdminUserId = admin.Id,
            AdminUser = admin,
            TeamCount = 3,
            TeamSize = 6
        };
        var anchorProfile = new PlayerProfile
        {
            Id = "profile-anchor",
            ZaloUserId = "uid-anchor",
            DisplayName = "Hiệp Hoàng Phạm"
        };
        var conflictingProfile = new PlayerProfile
        {
            Id = "profile-anh-tu",
            ZaloUserId = "uid-anh-tu",
            DisplayName = "Anh Tú"
        };
        session.Players.Add(new SessionPlayer
        {
            Id = "player-anchor",
            SessionId = session.Id,
            PlayerProfileId = anchorProfile.Id,
            PlayerProfile = anchorProfile,
            DisplayName = "Hiệp Hoàng Phạm",
            IsPresent = true
        });

        db.Users.Add(admin);
        db.PlayerProfiles.AddRange(anchorProfile, conflictingProfile);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new SessionDraftService(db);
        var result = await service.PreviewShareSlotAsync(
            admin.Id,
            session.Id,
            "Hiệp Hoàng Phạm",
            [new ShareSlotParticipantInput("Thanh Tuyền", "uid-anh-tu")]);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("Xung đột định danh", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cách xử lý ngay trên Zalo", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc sửa identity @TênĐúng", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 giữ identity cũ", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 đổi", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 huỷ", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var stored = await db.PlayerProfiles.AsNoTracking()
            .SingleAsync(profile => profile.Id == conflictingProfile.Id);
        Assert.Equal("Anh Tú", stored.DisplayName);
        Assert.Equal("uid-anh-tu", stored.ZaloUserId);
        Assert.Empty(await db.DraftSlots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void Non_identity_conflict_is_not_rewritten()
    {
        var result = ServiceResult<string>.Failure(StatusCodes.Status409Conflict, "Slot đã tồn tại.");

        Assert.Equal("Slot đã tồn tại.", result.Error);
    }
}
