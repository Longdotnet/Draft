using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloIdentityCorrectionConversationTests
{
    [Fact]
    public async Task Conflicting_mention_starts_choices_without_mutating_identity()
    {
        await using var fixture = await Fixture.CreateAsync(operatorEnabled: true);
        var result = await fixture.Service.TryHandleAsync(fixture.RepairIncoming());

        Assert.True(result.Handled);
        Assert.Contains("1. Giữ `Anh Tú`", result.Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("2. Đổi identity", result.Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("3. Huỷ", result.Response ?? string.Empty, StringComparison.Ordinal);

        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-target");
        Assert.Equal("Anh Tú", profile.DisplayName);
        Assert.Equal("uid-target", profile.ZaloUserId);
        var state = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloIdentityCorrectionConversation.PendingIntent, state.PendingIntent);
    }

    [Fact]
    public async Task Authorized_choice_two_renames_label_for_same_uid_and_current_group_player()
    {
        await using var fixture = await Fixture.CreateAsync(operatorEnabled: true);
        await fixture.Service.TryHandleAsync(fixture.RepairIncoming());

        var applied = await fixture.Service.TryHandleAsync(fixture.ChoiceIncoming("2", "choice-2"));

        Assert.True(applied.Handled);
        Assert.Contains("Anh Tú` → `Thanh Tuyền", applied.Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("UID không bị đổi", applied.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-target");
        Assert.Equal("Thanh Tuyền", profile.DisplayName);
        Assert.Equal("uid-target", profile.ZaloUserId);
        var player = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-target");
        Assert.Equal("Thanh Tuyền", player.DisplayName);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Unauthorized_choice_two_keeps_pending_and_does_not_rename()
    {
        await using var fixture = await Fixture.CreateAsync(operatorEnabled: false);
        await fixture.Service.TryHandleAsync(fixture.RepairIncoming());

        var denied = await fixture.Service.TryHandleAsync(fixture.ChoiceIncoming("2", "choice-denied"));

        Assert.True(denied.Handled);
        Assert.Contains("không có quyền", denied.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Anh Tú", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-target")).DisplayName);
        Assert.Single(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Choice_one_keeps_old_identity_and_choice_three_cancels_without_mutation()
    {
        await using var fixture = await Fixture.CreateAsync(operatorEnabled: true);
        await fixture.Service.TryHandleAsync(fixture.RepairIncoming());
        var keep = await fixture.Service.TryHandleAsync(fixture.ChoiceIncoming("1", "choice-keep"));

        Assert.True(keep.Handled);
        Assert.Contains("Giữ nguyên identity `Anh Tú`", keep.Response ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Anh Tú", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-target")).DisplayName);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());

        await fixture.Service.TryHandleAsync(fixture.RepairIncoming("repair-again"));
        var cancel = await fixture.Service.TryHandleAsync(fixture.ChoiceIncoming("3", "choice-cancel"));
        Assert.True(cancel.Handled);
        Assert.Contains("Đã huỷ", cancel.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Anh Tú", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-target")).DisplayName);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("2 người vẫn chơi")]
    [InlineData("1 slot")]
    [InlineData("3 team")]
    public void Numeric_choice_parser_does_not_steal_ordinary_numbered_chat(string text)
    {
        Assert.False(ZaloIdentityCorrectionConversation.TryParseChoice(text, out _));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            this.connection = connection;
            Db = db;
            Service = new ZaloIdentityCorrectionConversation(db);
        }

        public VolleyDraftDbContext Db { get; }
        public ZaloIdentityCorrectionConversation Service { get; }

        public static async Task<Fixture> CreateAsync(bool operatorEnabled)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"admin-{Guid.NewGuid():n}@identity-choice.test",
                PasswordHash = "x"
            };
            var zalo = new ZaloConnection
            {
                Id = "connection",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test"
            };
            var session = new MatchSession
            {
                Id = "session",
                Name = "T6",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "group",
                BotEnabled = true,
                BotOperatorZaloUserIdsJson = operatorEnabled
                    ? JsonSerializer.Serialize(new[] { "operator" })
                    : "[]",
                TeamCount = 3,
                TeamSize = 6
            };
            var targetProfile = new PlayerProfile
            {
                Id = "profile-target",
                ZaloUserId = "uid-target",
                DisplayName = "Anh Tú"
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "player-target",
                SessionId = session.Id,
                PlayerProfileId = targetProfile.Id,
                PlayerProfile = targetProfile,
                DisplayName = "Anh Tú",
                IsPresent = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(targetProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public ZaloIncomingMessageEvent RepairIncoming(string messageId = "repair")
        {
            const string content = "@Npc sửa identity @Thanh Tuyền";
            var targetPos = content.IndexOf("@Thanh Tuyền", StringComparison.Ordinal);
            return new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: "group",
                messageId: messageId,
                senderId: "operator",
                senderName: "Operator",
                content: content,
                mentions:
                [
                    new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                    new ZaloBridgeMention("uid-target", targetPos, "@Thanh Tuyền".Length)
                ],
                mentionedBot: true,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public ZaloIncomingMessageEvent ChoiceIncoming(string choice, string messageId)
        {
            var content = $"@Npc {choice}";
            return new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: "group",
                messageId: messageId,
                senderId: "operator",
                senderName: "Operator",
                content: content,
                mentions: [new ZaloBridgeMention("bot-account", 0, "@Npc".Length)],
                mentionedBot: true,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
