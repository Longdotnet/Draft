using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOperatorPermissionCommandServiceTests
{
    [Fact]
    public void Grant_command_uses_structured_mention_uid()
    {
        const string content = "@Npc cấp quyền cho @To An";
        var incoming = Message(
            content,
            [
                new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                new ZaloBridgeMention("user-toan", content.IndexOf("@To An", StringComparison.Ordinal), "@To An".Length)
            ]);

        var command = ZaloOperatorPermissionCommandService.TryParse(incoming);

        Assert.NotNull(command);
        Assert.Equal(ZaloOperatorPermissionCommandKind.Grant, command!.Kind);
        Assert.Equal(["user-toan"], command.TargetZaloUserIds);
    }

    [Fact]
    public void Revoke_and_list_commands_are_deterministic()
    {
        const string revokeContent = "@Npc thu quyền @To An";
        var revoke = ZaloOperatorPermissionCommandService.TryParse(Message(
            revokeContent,
            [
                new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                new ZaloBridgeMention("user-toan", revokeContent.IndexOf("@To An", StringComparison.Ordinal), "@To An".Length)
            ]));
        var list = ZaloOperatorPermissionCommandService.TryParse(Message("@Npc ai đang có quyền?", []));

        Assert.Equal(ZaloOperatorPermissionCommandKind.Revoke, revoke!.Kind);
        Assert.Equal(ZaloOperatorPermissionCommandKind.List, list!.Kind);
    }

    [Fact]
    public async Task Authorized_grant_updates_existing_operator_source_for_all_group_sessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new ZaloOperatorPermissionCommandService(fixture.Db);
        var command = new ZaloOperatorPermissionCommand(
            ZaloOperatorPermissionCommandKind.Grant,
            ["user-toan"]);

        var result = await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc cấp quyền cho @To An", []),
            command,
            canManagePermissions: true);

        Assert.True(result.Handled);
        Assert.Contains("đã cấp quyền", result.Response!, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var sessions = await fixture.Db.MatchSessions.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session =>
            Assert.Contains("user-toan", ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson)));

        // Idempotent repeated grants must not duplicate UID entries.
        await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc cấp quyền cho @To An", []),
            command,
            canManagePermissions: true);
        fixture.Db.ChangeTracker.Clear();
        Assert.All(await fixture.Db.MatchSessions.AsNoTracking().ToListAsync(), session =>
            Assert.Single(ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson)));
    }

    [Fact]
    public async Task Ordinary_operator_cannot_grant_more_operators_without_group_role_authority()
    {
        await using var fixture = await Fixture.CreateAsync(existingOperator: "user-admin-helper");
        var service = new ZaloOperatorPermissionCommandService(fixture.Db);

        var result = await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc cấp quyền cho @To An", [], senderId: "user-admin-helper"),
            new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Grant, ["user-toan"]),
            canManagePermissions: false);

        Assert.True(result.Handled);
        Assert.Contains("trưởng/phó", result.Response!, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.All(await fixture.Db.MatchSessions.AsNoTracking().ToListAsync(), session =>
        {
            var ids = ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson);
            Assert.Contains("user-admin-helper", ids);
            Assert.DoesNotContain("user-toan", ids);
        });
    }

    [Fact]
    public async Task Authorized_revoke_removes_target_from_all_group_sessions()
    {
        await using var fixture = await Fixture.CreateAsync(existingOperator: "user-toan");
        var service = new ZaloOperatorPermissionCommandService(fixture.Db);

        var result = await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc thu quyền @To An", []),
            new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Revoke, ["user-toan"]),
            canManagePermissions: true);

        Assert.Contains("thu quyền", result.Response!, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.All(await fixture.Db.MatchSessions.AsNoTracking().ToListAsync(), session =>
            Assert.DoesNotContain("user-toan", ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson)));
    }

    private static ZaloIncomingMessageEvent Message(
        string content,
        IReadOnlyList<ZaloBridgeMention> mentions,
        string senderId = "user-owner") => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: Guid.NewGuid().ToString("n"),
        senderId: senderId,
        senderName: "Long",
        content: content,
        mentions: mentions,
        mentionedBot: true,
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

        public static async Task<Fixture> CreateAsync(string? existingOperator = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"permission-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);

            foreach (var seed in new[] { (Id: "s1", Name: "T6"), (Id: "s2", Name: "CN") })
            {
                db.MatchSessions.Add(new MatchSession
                {
                    Id = seed.Id,
                    Name = seed.Name,
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    ZaloGroupId = "g1",
                    BotEnabled = true,
                    Status = SessionStatus.Setup,
                    StartTime = DateTimeOffset.UtcNow.AddDays(1),
                    BotOperatorZaloUserIdsJson = existingOperator is null
                        ? "[]"
                        : $"[\"{existingOperator}\"]"
                });
            }

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
