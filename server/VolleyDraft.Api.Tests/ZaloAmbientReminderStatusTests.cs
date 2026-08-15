using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientReminderStatusTests
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
    public async Task Untagged_reminder_status_reads_enabled_schedule_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("reminder-1", "khi nào nhắc T6?");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.ReminderStatus.ToString(), decision.Intent);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Equal("session-t6", reply!.SessionId);
        Assert.Contains("Lịch nhắc T6", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ khi còn thiếu slot", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled-marker", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generic_untagged_reminder_status_summarizes_group_without_mutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Incoming("reminder-2", "xem lịch nhắc");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.ReminderStatus.ToString(), decision.Intent);

        var before = await fixture.Db.ZaloReminderSchedules.AsNoTracking().CountAsync();
        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);
        var after = await fixture.Db.ZaloReminderSchedules.AsNoTracking().CountAsync();

        Assert.NotNull(reply);
        Assert.Contains("Các lịch nhắc đang bật", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T6", reply.Text);
        Assert.Equal(before, after);
    }

    private static ZaloAmbientParticipationDecision Decide(ZaloIncomingMessageEvent incoming) =>
        ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            new ZaloAmbientGroupSituation(1, 1, 1, 0, null, [incoming.MessageId]),
            Settings,
            DateTimeOffset.UtcNow);

    private static ZaloIncomingMessageEvent Incoming(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
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
                Email = $"ambient-reminder-{Guid.NewGuid():n}@example.test",
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
            var session = new MatchSession
            {
                Id = "session-t6",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 3,
                TeamSize = 6
            };
            session.ReminderSchedules.Add(new ZaloReminderSchedule
            {
                Id = "reminder-enabled",
                SessionId = session.Id,
                CreatedBySenderId = "user-long",
                CreatedBySenderName = "Long",
                Message = "enabled-marker",
                Enabled = true,
                Repeats = true,
                IntervalMinutes = 60,
                OnlyIfMissingSlots = true,
                NextRunAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            session.ReminderSchedules.Add(new ZaloReminderSchedule
            {
                Id = "reminder-disabled",
                SessionId = session.Id,
                CreatedBySenderId = "user-long",
                CreatedBySenderName = "Long",
                Message = "disabled-marker",
                Enabled = false,
                Repeats = false,
                NextRunAt = DateTimeOffset.UtcNow.AddHours(2)
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.MatchSessions.Add(session);
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
