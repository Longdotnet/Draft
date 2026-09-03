using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfServiceIdentityDisplayNameTests
{
    [Fact]
    public async Task Accent_normalization_still_requires_full_exact_name_not_partial_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User { Id = "a", DisplayName = "A", Email = "a@exact.test", PasswordHash = "x" };
        var zalo = new ZaloConnection { Id = "c", AdminUserId = admin.Id, AdminUser = admin, AccountZaloId = "bot", DisplayName = "Npc", EncryptedCredentials = "x" };
        var profile = new PlayerProfile { Id = "p", ZaloUserId = "", DisplayName = "Đặng Thế Nguyễn" };
        var session = new MatchSession { Id = "s", Name = "CN", AdminUserId = admin.Id, AdminUser = admin, ZaloConnectionId = zalo.Id, ZaloConnection = zalo, ZaloGroupId = "g", BotEnabled = true };
        session.Players.Add(new SessionPlayer { Id = "sp", SessionId = "s", PlayerProfileId = "p", PlayerProfile = profile, DisplayName = profile.DisplayName, IsPresent = true });
        db.Users.Add(admin); db.ZaloConnections.Add(zalo); db.PlayerProfiles.Add(profile); db.MatchSessions.Add(session);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var partial = new ZaloIncomingMessageEvent("bot", "bot", "g", "m1", "uid", "Nguyễn", "x", [], false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var partialResult = await new ZaloSelfServiceIdentityLinker(db).TryLinkAsync("c", "g", partial);
        Assert.Equal(ZaloSelfServiceIdentityLinkResult.NotApplicable, partialResult);

        var exact = new ZaloIncomingMessageEvent("bot", "bot", "g", "m2", "uid", "Dang The Nguyen", "x", [], false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var exactResult = await new ZaloSelfServiceIdentityLinker(db).TryLinkAsync("c", "g", exact);
        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Linked, exactResult);
    }
}
