using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOperatorPermissionGroupScopeTests
{
    [Fact]
    public async Task Grant_does_not_leak_to_another_group()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var admin = new User { Id = "a", DisplayName = "A", Email = "a@scope.test", PasswordHash = "x" };
        var zalo = new ZaloConnection { Id = "c", AdminUserId = "a", AdminUser = admin, AccountZaloId = "bot", DisplayName = "Npc", EncryptedCredentials = "x" };
        db.Users.Add(admin); db.ZaloConnections.Add(zalo);
        db.MatchSessions.AddRange(
            new MatchSession { Id = "g1s", Name = "T6", AdminUserId = "a", AdminUser = admin, ZaloConnectionId = "c", ZaloConnection = zalo, ZaloGroupId = "g1", BotEnabled = true },
            new MatchSession { Id = "g2s", Name = "T6", AdminUserId = "a", AdminUser = admin, ZaloConnectionId = "c", ZaloConnection = zalo, ZaloGroupId = "g2", BotEnabled = true });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var incoming = new ZaloIncomingMessageEvent("bot", "bot", "g1", "m", "owner", "Long", "@Npc cấp quyền @To An", [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await new ZaloOperatorPermissionCommandService(db).ApplyAsync(
            "c", "g1", incoming,
            new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Grant, ["toan"]),
            true);
        db.ChangeTracker.Clear();

        Assert.Contains("toan", ZaloOperatorPermissionCommandService.ParseOperatorIds((await db.MatchSessions.SingleAsync(item => item.Id == "g1s")).BotOperatorZaloUserIdsJson));
        Assert.Empty(ZaloOperatorPermissionCommandService.ParseOperatorIds((await db.MatchSessions.SingleAsync(item => item.Id == "g2s")).BotOperatorZaloUserIdsJson));
    }
}
