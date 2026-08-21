using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloCommunityNudgeTests
{
    [Theory]
    [InlineData("@Npc stt 1", 1)]
    [InlineData("@Npc STT 3", 3)]
    [InlineData("@Npc stt 5", 5)]
    public void Stt_command_parses_daily_count_from_one_to_five(string text, int expected)
    {
        var command = ZaloOperatorPermissionCommandService.TryParse(Message(text));

        Assert.NotNull(command);
        Assert.Equal(ZaloOperatorPermissionCommandKind.CommunityTipDailyCount, command!.Kind);
        Assert.Equal(expected, command.CommunityTipDailyCount);
        Assert.Empty(command.TargetZaloUserIds);
    }

    [Theory]
    [InlineData("@Npc stt 0")]
    [InlineData("@Npc stt 6")]
    [InlineData("@Npc stt")]
    [InlineData("@Npc stt 2 lan")]
    public void Invalid_stt_frequency_is_not_treated_as_setting_command(string text)
    {
        Assert.Null(ZaloOperatorPermissionCommandService.TryParse(Message(text)));
    }

    [Fact]
    public async Task Authorized_stt_command_persists_group_daily_count()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new ZaloOperatorPermissionCommandService(fixture.Db);
        var command = ZaloOperatorPermissionCommandService.TryParse(Message("@Npc stt 4"));

        var result = await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc stt 4"),
            command!,
            canManagePermissions: true);

        Assert.True(result.Handled);
        Assert.Equal("CommunityTipSettings", result.Intent);
        Assert.Contains("4 lần/ngày", result.Response!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, await new ZaloCommunityNudgeStore(fixture.Db).GetDailyCountAsync("conn-1", "g1"));
    }

    [Fact]
    public async Task Ordinary_operator_cannot_change_stt_frequency()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new ZaloOperatorPermissionCommandService(fixture.Db);
        var command = ZaloOperatorPermissionCommandService.TryParse(Message("@Npc stt 5"));

        var result = await service.ApplyAsync(
            "conn-1",
            "g1",
            Message("@Npc stt 5", senderId: "ordinary-user"),
            command!,
            canManagePermissions: false);

        Assert.True(result.Handled);
        Assert.Contains("trưởng/phó", result.Response!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await new ZaloCommunityNudgeStore(fixture.Db).GetDailyCountAsync("conn-1", "g1"));
    }

    [Fact]
    public async Task Store_keeps_frequency_group_scoped_and_clamped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloCommunityNudgeStore(fixture.Db);

        Assert.Equal(5, await store.SetDailyCountAsync("conn-1", "g1", 99, "owner"));
        Assert.Equal(5, await store.GetDailyCountAsync("conn-1", "g1"));
        Assert.Equal(1, await store.GetDailyCountAsync("conn-1", "other-group"));
    }

    private static ZaloIncomingMessageEvent Message(string content, string senderId = "owner-user") => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: Guid.NewGuid().ToString("n"),
        senderId: senderId,
        senderName: "Long",
        content: content,
        mentions: [new ZaloBridgeMention("bot-account", 0, "@Npc".Length)],
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
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"community-tip-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.MatchSessions.Add(new MatchSession
            {
                Id = "session-1",
                Name = "T6",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = DateTimeOffset.UtcNow.AddDays(1)
            });
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
