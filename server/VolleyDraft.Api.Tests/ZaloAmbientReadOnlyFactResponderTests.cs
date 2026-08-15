using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientReadOnlyFactResponderTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 60,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 2,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public async Task Untagged_self_membership_uses_stable_zalo_uid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("membership-1", "tui có tên T6 chưa?", "user-long", "Long");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.SelfMembership.ToString(), decision.Intent);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.SelfMembership, reply!.Intent);
        Assert.Equal("session-t6", reply.SessionId);
        Assert.Contains("đang có tên", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Untagged_weekly_count_lists_only_current_vietnam_week()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("weekly-1", "tuần này đánh mấy trận?", "user-long", "Long");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.WeeklySessionCount.ToString(), decision.Intent);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Contains("2 kèo", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T6", reply.Text);
        Assert.Contains("CN", reply.Text);
        Assert.DoesNotContain("NEXT", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Untagged_waitlist_status_reports_sender_position_from_database()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("waitlist-1", "T6 ai đang chờ waitlist?", "user-wait", "Wait User");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.WaitlistStatus.ToString(), decision.Intent);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Equal("session-t6", reply!.SessionId);
        Assert.Contains("vị trí 2", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wait User", reply.Text);
        Assert.Contains("Nam", reply.Text);
    }

    [Fact]
    public async Task Membership_never_falls_back_to_same_display_name_with_different_uid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("membership-spoof", "tui có tên T6 chưa?", "different-uid", "Long");
        var decision = Decide(incoming);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Contains("chưa thấy", reply!.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloAmbientParticipationDecision Decide(ZaloIncomingMessageEvent incoming) =>
        ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            new ZaloAmbientGroupSituation(1, 1, 1, 0, null, [incoming.MessageId]),
            Settings,
            DateTimeOffset.UtcNow);

    private static ZaloIncomingMessageEvent Incoming(
        string messageId,
        string content,
        string senderId,
        string senderName) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: senderId,
        senderName: senderName,
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

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"ambient-facts-{Guid.NewGuid():n}@example.test",
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
            var longProfile = new PlayerProfile
            {
                Id = "profile-long",
                ZaloUserId = "user-long",
                DisplayName = "Long"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(longProfile);

            var now = DateTimeOffset.UtcNow;
            var t6 = Session("session-t6", "T6", now.AddHours(2), zalo);
            t6.Players.Add(new SessionPlayer
            {
                Id = "t6-long",
                SessionId = t6.Id,
                PlayerProfileId = longProfile.Id,
                PlayerProfile = longProfile,
                DisplayName = "Long",
                IsPresent = true
            });
            t6.WaitlistEntries.Add(new SessionWaitlistEntry
            {
                Id = "wait-nam",
                SessionId = t6.Id,
                ZaloUserId = "user-nam",
                DisplayName = "Nam",
                Status = SessionWaitlistStatus.Waiting,
                CreatedAt = now.AddMinutes(-2)
            });
            t6.WaitlistEntries.Add(new SessionWaitlistEntry
            {
                Id = "wait-user",
                SessionId = t6.Id,
                ZaloUserId = "user-wait",
                DisplayName = "Wait User",
                Status = SessionWaitlistStatus.Waiting,
                CreatedAt = now.AddMinutes(-1)
            });

            var cn = Session("session-cn", "CN", now.AddHours(3), zalo);
            var next = Session("session-next", "NEXT", now.AddDays(8), zalo);
            db.MatchSessions.AddRange(t6, cn, next);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        private static MatchSession Session(
            string id,
            string name,
            DateTimeOffset start,
            ZaloConnection connection) => new()
        {
            Id = id,
            AdminUserId = "admin-1",
            ZaloConnectionId = connection.Id,
            ZaloConnection = connection,
            ZaloGroupId = "g1",
            Name = name,
            Status = SessionStatus.Setup,
            BotEnabled = true,
            StartTime = start,
            TeamCount = 3,
            TeamSize = 6
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
