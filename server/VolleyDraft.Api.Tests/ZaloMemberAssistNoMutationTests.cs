using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistNoMutationTests
{
    [Fact]
    public async Task Help_detection_alone_never_changes_player_presence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User { Id = "admin", DisplayName = "Admin", Email = "assist-readonly@test.local", PasswordHash = "x" };
        var zalo = new ZaloConnection { Id = "conn", AdminUserId = admin.Id, AdminUser = admin, AccountZaloId = "bot", DisplayName = "Npc", EncryptedCredentials = "x" };
        var profile = new PlayerProfile { Id = "p", ZaloUserId = "u", DisplayName = "Nguyên" };
        var session = new MatchSession
        {
            Id = "s", Name = "T6", AdminUserId = admin.Id, AdminUser = admin,
            ZaloConnectionId = zalo.Id, ZaloConnection = zalo, ZaloGroupId = "g",
            BotEnabled = true, StartTime = DateTimeOffset.UtcNow.AddDays(1)
        };
        session.Players.Add(new SessionPlayer
        {
            Id = "sp", SessionId = session.Id, PlayerProfileId = profile.Id,
            PlayerProfile = profile, DisplayName = profile.DisplayName, IsPresent = true
        });
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.PlayerProfiles.Add(profile);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g", "m", "u", "Nguyên", "em pass slot T6 nha", [], false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reply = await new ZaloMemberAssistService(db).TryBuildAsync("conn", "g", incoming);

        Assert.NotNull(reply);
        db.ChangeTracker.Clear();
        Assert.True(await db.SessionPlayers.Where(item => item.Id == "sp").Select(item => item.IsPresent).SingleAsync());
        Assert.Empty(await db.TeamPreferenceGroups.ToListAsync());
    }
}
