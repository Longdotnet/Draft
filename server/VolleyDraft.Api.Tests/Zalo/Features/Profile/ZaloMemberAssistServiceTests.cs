using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistServiceTests
{
    [Theory]
    [InlineData("Em pass sỉ lót tối nay á 🥺")]
    [InlineData("tui pass slot T6 nha")]
    [InlineData("nhường suất CN nè")]
    [InlineData("pass cái kèo tối nay")]
    [InlineData("huỷ slot thôi")]
    [InlineData("huy slot T6 nha")]
    public void Pass_slot_slang_is_a_help_opportunity(string text)
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity(text));
    }

    [Theory]
    [InlineData("tối nay ai đánh vậy")]
    [InlineData("share slot với To An")]
    [InlineData("pass bóng cho tui")]
    public void Unrelated_chat_is_not_a_pass_slot_help_opportunity(string text)
    {
        Assert.False(ZaloMemberAssistService.IsPassSlotHelpOpportunity(text));
    }

    [Fact]
    public async Task Unique_owned_session_gets_short_natural_help_without_mutation()
    {
        await using var fixture = await Fixture.CreateAsync(sessionCount: 1);
        var incoming = Message("m1", "Em pass sỉ lót T6 tối nay á 🥺");

        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", incoming);

        Assert.NotNull(reply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, reply!.Kind);
        Assert.Equal("session-t6", reply.SessionId);
        Assert.Contains("pass slot T6", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.DraftSlotPlayers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Broadcast_all_mention_does_not_suppress_self_pass_help()
    {
        await using var fixture = await Fixture.CreateAsync(sessionCount: 1);
        var content = "Em pass slot T6 tối nay nha mn @All";
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m-all",
            senderId: "user-nguyen",
            senderName: "Đặng Thế Nguyễn",
            content: content,
            mentions: [new ZaloBridgeMention("broadcast-all", content.IndexOf("@All", StringComparison.Ordinal), "@All".Length)],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", incoming);

        Assert.NotNull(reply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, reply!.Kind);
        Assert.Equal("session-t6", reply.SessionId);
        Assert.Contains("pass slot T6", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unmatched_pass_sender_gets_clarification_instead_of_silence()
    {
        await using var fixture = await Fixture.CreateAsync(sessionCount: 1);
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m-unmatched",
            senderId: "unknown-user",
            senderName: "Anh Duy",
            content: "Em pass slot tối nay nha",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", incoming);

        Assert.NotNull(reply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, reply!.Kind);
        Assert.Null(reply.SessionId);
        Assert.Contains("chưa match", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiple_owned_sessions_asks_which_one_instead_of_guessing()
    {
        await using var fixture = await Fixture.CreateAsync(sessionCount: 2);
        var incoming = Message("m2", "Em pass sỉ lót tối nay á 🥺");

        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", incoming);

        Assert.NotNull(reply);
        Assert.Null(reply!.SessionId);
        Assert.Contains("Pass kèo nào", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T6", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CN", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Human_mention_suppresses_self_pass_assumption()
    {
        await using var fixture = await Fixture.CreateAsync(sessionCount: 1);
        var content = "@To An pass slot T6 á";
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "m3",
            senderId: "user-nguyen",
            senderName: "Đặng Thế Nguyễn",
            content: content,
            mentions: [new ZaloBridgeMention("user-toan", 0, "@To An".Length)],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", incoming);

        Assert.Null(reply);
    }

    private static ZaloIncomingMessageEvent Message(string id, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: id,
        senderId: "user-nguyen",
        senderName: "Đặng Thế Nguyễn",
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

        public static async Task<Fixture> CreateAsync(int sessionCount)
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
                Email = $"assist-{Guid.NewGuid():n}@example.test",
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
            var profile = new PlayerProfile
            {
                Id = "profile-nguyen",
                ZaloUserId = "user-nguyen",
                DisplayName = "Đặng Thế Nguyễn"
            };

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(profile);

            var names = sessionCount == 1 ? new[] { "T6" } : new[] { "T6", "CN" };
            for (var index = 0; index < names.Length; index++)
            {
                var session = new MatchSession
                {
                    Id = index == 0 ? "session-t6" : "session-cn",
                    Name = names[index],
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    ZaloGroupId = "g1",
                    BotEnabled = true,
                    Status = SessionStatus.Setup,
                    StartTime = DateTimeOffset.UtcNow.AddDays(index + 1),
                    TeamCount = 3,
                    TeamSize = 6
                };
                session.Players.Add(new SessionPlayer
                {
                    Id = $"player-{index}",
                    SessionId = session.Id,
                    PlayerProfileId = profile.Id,
                    PlayerProfile = profile,
                    DisplayName = profile.DisplayName,
                    IsPresent = true
                });
                db.MatchSessions.Add(session);
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
