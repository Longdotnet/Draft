using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionUnknownRoleTests
{
    [Fact]
    public async Task Unknown_live_group_role_does_not_change_operator_list()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var admin = new User { Id = "a", DisplayName = "A", Email = "a@role.test", PasswordHash = "x" };
        var zalo = new ZaloConnection { Id = "c", AdminUserId = "a", AdminUser = admin, AccountZaloId = "bot", DisplayName = "Npc", EncryptedCredentials = "x" };
        db.Users.Add(admin); db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(new MatchSession { Id = "s", Name = "T6", AdminUserId = "a", AdminUser = admin, ZaloConnectionId = "c", ZaloConnection = zalo, ZaloGroupId = "g", BotEnabled = true });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var incoming = new ZaloIncomingMessageEvent("bot", "bot", "g", "m", "u", "Long", "@Npc cấp quyền @To An", [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var result = await new ZaloOperatorPermissionCommandService(db).ApplyAsync(
            "c", "g", incoming,
            new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Grant, ["toan"]),
            canManagePermissions: null);

        Assert.Contains("chưa check", result.Response!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ZaloOperatorPermissionCommandService.ParseOperatorIds((await db.MatchSessions.SingleAsync()).BotOperatorZaloUserIdsJson));
    }
}
