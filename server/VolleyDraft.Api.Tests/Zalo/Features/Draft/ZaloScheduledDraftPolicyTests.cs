using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Draft;

public sealed class ZaloScheduledDraftPolicyTests
{
    [Fact]
    public async Task Scheduler_does_not_draft_yesterdays_session_when_today_has_no_match()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var localNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var yesterday = new DateTimeOffset(localNow.Date.AddDays(-1).AddHours(19), TimeSpan.FromHours(7));
        var tomorrow = new DateTimeOffset(localNow.Date.AddDays(1).AddHours(19), TimeSpan.FromHours(7));
        db.Users.Add(new User { Id = "owner", DisplayName = "Owner" });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "connection", AdminUserId = "owner", AccountZaloId = "bot"
        });
        db.MatchSessions.AddRange(
            new MatchSession
            {
                Id = "yesterday-drafted", AdminUserId = "owner", ZaloConnectionId = "connection",
                ZaloGroupId = "group", BotEnabled = true, Status = SessionStatus.Finished,
                StartTime = yesterday
            },
            new MatchSession
            {
                Id = "yesterday-stale", AdminUserId = "owner", ZaloConnectionId = "connection",
                ZaloGroupId = "group", BotEnabled = true, Status = SessionStatus.Setup,
                StartTime = yesterday
            },
            new MatchSession
            {
                Id = "tomorrow", AdminUserId = "owner", ZaloConnectionId = "connection",
                ZaloGroupId = "group", BotEnabled = true, Status = SessionStatus.Setup,
                StartTime = tomorrow
            });
        db.ZaloScheduledDraftPolicies.Add(new ZaloScheduledDraftPolicy
        {
            ZaloConnectionId = "connection", GroupId = "group", Enabled = true,
            LocalDraftMinuteOfDay = 17 * 60 + 30, ReminderMinutes = 30,
            TimeZoneId = "Asia/Ho_Chi_Minh", Version = 2
        });
        db.ZaloScheduledDraftRuns.Add(new ZaloScheduledDraftRun
        {
            SessionId = "yesterday-drafted", PolicyVersion = 2,
            DraftDueAt = yesterday.AddMinutes(-90), ReminderDueAt = yesterday.AddMinutes(-120),
            ReminderSentAt = yesterday.AddMinutes(-120), DraftedAt = yesterday.AddMinutes(-90),
            ResultMessageSentAt = yesterday.AddMinutes(-90), State = ZaloScheduledDraftRunState.Drafted
        });
        await db.SaveChangesAsync();

        // No live bridge is supplied: attempting to redraft yesterday or draft
        // tomorrow early must make this scheduler cycle fail the regression.
        var service = new ZaloScheduledDraftService(db, null!, null!, null!,
            NullLoggerFactory.Instance, NullLogger<ZaloScheduledDraftService>.Instance);
        var result = await service.RunDueAsync();

        Assert.Equal(0, result.Drafted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Skipped);
        var onlyRun = Assert.Single(await db.ZaloScheduledDraftRuns.ToListAsync());
        Assert.Equal("yesterday-drafted", onlyRun.SessionId);
        Assert.Equal(ZaloScheduledDraftRunState.Drafted, onlyRun.State);
    }

    [Theory]
    [InlineData("bật tự draft", "Enable", 17 * 60 + 30)]
    [InlineData("bật tự draft lúc 18h", "Enable", 18 * 60)]
    [InlineData("bật tự draft lúc 17:45", "Enable", 17 * 60 + 45)]
    [InlineData("tắt tự draft", "Disable", null)]
    [InlineData("hôm nay không tự draft", "SkipSession", null)]
    [InlineData("hoãn draft đến 18h", "DeferSession", 18 * 60)]
    [InlineData("hoãn tự draft đến 18h30", "DeferSession", 18 * 60 + 30)]
    [InlineData("đổi draft đến 18:05", "DeferSession", 18 * 60 + 5)]
    [InlineData("hoãn draft đến 18", "DeferSession", 18 * 60)]
    public void Scheduled_draft_commands_are_deterministic(
        string input,
        string expectedKind,
        int? expectedMinute)
    {
        Assert.True(ZaloScheduledDraftService.TryParseCommand(input, out var command));
        Assert.Equal(expectedKind, command.Kind.ToString());
        if (expectedKind == "Enable" && expectedMinute == 17 * 60 + 30 && command.LocalMinuteOfDay is null)
            return;
        Assert.Equal(expectedMinute, command.LocalMinuteOfDay);
    }

    [Theory]
    [InlineData("draft đi")]
    [InlineData("mai chơi nha")]
    [InlineData("hôm nay không đi")]
    [InlineData("hoãn lịch nhắc đến 18h")]
    [InlineData("hoãn draft đến 25h")]
    [InlineData("hoãn draft đến 18h75")]
    public void Unrelated_messages_do_not_change_scheduled_draft_policy(string input)
    {
        Assert.False(ZaloScheduledDraftService.TryParseCommand(input, out _));
    }

    [Fact]
    public void Changed_policy_version_resets_old_warning_and_due_time_without_reopening_completed_drafts()
    {
        var initial = new DateTimeOffset(2026, 9, 24, 17, 0, 0, TimeSpan.FromHours(7));
        var run = new ZaloScheduledDraftRun
        {
            PolicyVersion = 2,
            ReminderDueAt = initial,
            DraftDueAt = initial.AddMinutes(30),
            ReminderSentAt = initial,
            State = ZaloScheduledDraftRunState.Blocked,
            LastError = "poll_sync_failed",
            RosterFingerprint = "stale-roster"
        };

        Assert.True(ZaloScheduledDraftService.ResetForPolicy(
            run, 3, initial.AddHours(1), initial.AddHours(1).AddMinutes(30), initial.AddMinutes(10)));
        Assert.Equal(3, run.PolicyVersion);
        Assert.Null(run.ReminderSentAt);
        Assert.Equal(initial.AddHours(1), run.ReminderDueAt);
        Assert.Equal(initial.AddHours(1).AddMinutes(30), run.DraftDueAt);
        Assert.Equal(ZaloScheduledDraftRunState.Pending, run.State);
        Assert.Null(run.LastError);
        Assert.Null(run.RosterFingerprint);

        run.State = ZaloScheduledDraftRunState.Drafted;
        run.DraftedAt = initial.AddHours(1).AddMinutes(30);
        Assert.False(ZaloScheduledDraftService.ResetForPolicy(
            run, 4, initial.AddHours(2), initial.AddHours(2).AddMinutes(30), initial.AddHours(1)));
        Assert.Equal(3, run.PolicyVersion);
        Assert.Equal(ZaloScheduledDraftRunState.Drafted, run.State);
    }

    [Fact]
    public void Failed_late_reminder_keeps_the_same_30_minute_deadline_and_transport_identity_on_retry()
    {
        var reminderTime = new DateTimeOffset(2026, 9, 24, 17, 0, 0, TimeSpan.FromHours(7));
        var due = reminderTime.AddMinutes(30);
        var run = new ZaloScheduledDraftRun
        {
            SessionId = "session",
            PolicyVersion = 2,
            ReminderDueAt = reminderTime,
            DraftDueAt = due
        };

        Assert.True(ZaloScheduledDraftService.ShouldShiftLateReminder(
            run, due, reminderTime.AddMinutes(5)));
        run.ReminderDueAt = reminderTime.AddMinutes(5);
        run.DraftDueAt = reminderTime.AddMinutes(35);
        run.LastError = "late_reminder_safe_window";
        var firstSendIdentity = ZaloScheduledDraftService.ReminderIdempotencyKey(run);

        Assert.False(ZaloScheduledDraftService.ShouldShiftLateReminder(
            run, due, reminderTime.AddMinutes(12)));
        Assert.Equal(firstSendIdentity, ZaloScheduledDraftService.ReminderIdempotencyKey(run));

        run.DraftDueAt = reminderTime.AddMinutes(55); // Authorized defer is a new notice payload.
        Assert.NotEqual(firstSendIdentity, ZaloScheduledDraftService.ReminderIdempotencyKey(run));

        run.DraftDueAt = due;
        run.ReminderDueAt = reminderTime;
        run.LastError = "reminder_delivery_attempted";
        Assert.False(ZaloScheduledDraftService.ShouldShiftLateReminder(
            run, due, reminderTime.AddMinutes(16))); // A potentially delivered reminder must not move.
    }

    [Fact]
    public void Warning_acknowledged_at_1701_must_move_nominal_1730_due_to_1731()
    {
        var nominalDue = new DateTimeOffset(2026, 9, 24, 17, 30, 0, TimeSpan.FromHours(7));
        var warningAcknowledgedAt = nominalDue.AddMinutes(-29);
        var run = new ZaloScheduledDraftRun
        {
            SessionId = "session",
            PolicyVersion = 2,
            DraftDueAt = nominalDue
        };
        var originalWarningKey = ZaloScheduledDraftService.ReminderIdempotencyKey(run);

        Assert.Equal(nominalDue.AddMinutes(1), ZaloScheduledDraftService.ResolveConfirmedDraftDue(
            nominalDue, warningAcknowledgedAt, 30));
        Assert.Equal(originalWarningKey, ZaloScheduledDraftService.ReminderIdempotencyKey(run));

        run.ReminderSentAt = warningAcknowledgedAt;
        run.DraftDueAt = ZaloScheduledDraftService.ResolveConfirmedDraftDue(
            run.DraftDueAt, warningAcknowledgedAt, 30);
        Assert.Equal(warningAcknowledgedAt.AddMinutes(30), run.DraftDueAt);
    }

    [Fact]
    public async Task Scheduled_execution_claim_requires_confirmed_warning_and_fences_concurrent_claims()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "owner", DisplayName = "Owner" });
        db.MatchSessions.Add(new MatchSession { Id = "session", AdminUserId = "owner" });
        var run = new ZaloScheduledDraftRun
        {
            Id = "scheduled-run",
            SessionId = "session",
            PolicyVersion = 2,
            ReminderDueAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            DraftDueAt = DateTimeOffset.UtcNow
        };
        db.ZaloScheduledDraftRuns.Add(run);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        Assert.False(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            db, run.Id, 2, 30, "before-warning", now));

        run.ReminderSentAt = now.AddMinutes(-30);
        run.State = ZaloScheduledDraftRunState.ReminderSent;
        await db.SaveChangesAsync();
        Assert.False(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            db, run.Id, 1, 30, "old-version", now));
        Assert.True(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            db, run.Id, 2, 30, "first", now));

        await using var competingDb = new VolleyDraftDbContext(options);
        Assert.False(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            competingDb, run.Id, 2, 30, "second", now.AddSeconds(2)));
        var persisted = await competingDb.ZaloScheduledDraftRuns.AsNoTracking().SingleAsync();
        Assert.Equal("first", persisted.LeaseToken);
        Assert.True(persisted.LeaseUntil > now.AddMinutes(14));
    }

    [Fact]
    public async Task Execution_claim_rejects_1730_when_delivery_was_confirmed_at_1701()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "owner", DisplayName = "Owner" });
        db.MatchSessions.Add(new MatchSession { Id = "session", AdminUserId = "owner" });
        var warningAck = new DateTimeOffset(2026, 9, 24, 17, 1, 0, TimeSpan.FromHours(7));
        var nominalDue = warningAck.AddMinutes(29);
        var run = new ZaloScheduledDraftRun
        {
            Id = "scheduled-run",
            SessionId = "session",
            PolicyVersion = 2,
            ReminderDueAt = warningAck.AddMinutes(-1),
            ReminderSentAt = warningAck,
            DraftDueAt = nominalDue,
            State = ZaloScheduledDraftRunState.ReminderSent
        };
        db.ZaloScheduledDraftRuns.Add(run);
        await db.SaveChangesAsync();

        Assert.False(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            db, run.Id, 2, 30, "premature", nominalDue));

        var actualDue = ZaloScheduledDraftService.ResolveConfirmedDraftDue(nominalDue, warningAck, 30);
        await db.ZaloScheduledDraftRuns.Where(item => item.Id == run.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DraftDueAt, actualDue));
        Assert.Equal(warningAck.AddMinutes(30), actualDue);
        Assert.True(await ZaloScheduledDraftService.TryClaimExecutionAsync(
            db, run.Id, 2, 30, "safe", actualDue));
    }
}
