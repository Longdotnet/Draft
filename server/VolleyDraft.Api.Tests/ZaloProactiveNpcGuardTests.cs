using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloProactiveNpcGuardTests
{
    [Fact]
    public async Task Shared_send_lease_serializes_proactive_lanes_and_holds_committed_cooldown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloProactiveMessageStore(db);
        var now = DateTimeOffset.Parse("2026-08-27T06:00:00+00:00");

        Assert.True(await store.TryAcquireLeaseAsync(
            "conn-1", "g1", "social-a", now, TimeSpan.FromMinutes(2)));
        Assert.False(await store.TryAcquireLeaseAsync(
            "conn-1", "g1", "community-b", now.AddSeconds(1), TimeSpan.FromMinutes(2)));

        await store.CommitCooldownAsync(
            "conn-1",
            "g1",
            "social-a",
            now.AddMinutes(60));

        Assert.False(await store.TryAcquireLeaseAsync(
            "conn-1", "g1", "community-b", now.AddMinutes(30), TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryAcquireLeaseAsync(
            "conn-1", "g1", "community-b", now.AddMinutes(61), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Durable_history_from_another_lane_suppresses_presence_inside_global_cooldown()
    {
        var now = DateTimeOffset.Parse("2026-08-27T07:00:00+00:00"); // 14:00 VN
        var history = new[]
        {
            Proactive(
                lane: ZaloProactiveLane.Community,
                kind: "member_vote_reengagement",
                subjectUserId: "member-1",
                sentAt: now.AddMinutes(-27))
        };

        var move = ZaloGroupEngagementDirector.Plan(
            Snapshot(now, history),
            EnabledPresence());

        Assert.Null(move);
    }

    [Fact]
    public void Ambient_trash_is_never_sent_twice_on_the_same_local_day()
    {
        var now = DateTimeOffset.Parse("2026-08-27T07:00:00+00:00"); // 14:00 VN
        var history = new[]
        {
            Proactive(
                lane: ZaloProactiveLane.SocialPresence,
                kind: nameof(ZaloEngagementMoveKind.HotTake),
                contentKey: "ambient:hot:street:2",
                sentAt: now.AddHours(-3))
        };

        var move = ZaloGroupEngagementDirector.Plan(
            Snapshot(now, history),
            EnabledPresence());

        Assert.Null(move);
    }

    [Fact]
    public void Ambient_phrase_rotates_before_reusing_the_same_content_key()
    {
        var firstNow = DateTimeOffset.Parse("2026-08-27T07:00:00+00:00");
        var first = ZaloGroupEngagementDirector.Plan(
            Snapshot(firstNow, []),
            EnabledPresence());

        Assert.NotNull(first);
        Assert.Contains(
            first!.Kind,
            new[] { ZaloEngagementMoveKind.QuietWake, ZaloEngagementMoveKind.HotTake });

        var firstHistory = new[]
        {
            Proactive(
                lane: ZaloProactiveLane.SocialPresence,
                kind: first.Kind.ToString(),
                contentKey: first.ContentKey,
                message: first.Message,
                sentAt: firstNow)
        };

        ZaloEngagementMove? sameKindLater = null;
        for (var day = 1; day <= 30; day++)
        {
            var candidateNow = firstNow.AddDays(day);
            var candidate = ZaloGroupEngagementDirector.Plan(
                Snapshot(candidateNow, firstHistory),
                EnabledPresence());
            if (candidate?.Kind == first.Kind)
            {
                sameKindLater = candidate;
                break;
            }
        }

        Assert.NotNull(sameKindLater);
        Assert.NotEqual(first.ContentKey, sameKindLater!.ContentKey);
        Assert.NotEqual(first.Message, sameKindLater.Message);
    }

    [Fact]
    public void Member_rotation_uses_uid_not_display_name_and_survives_renames()
    {
        var now = DateTimeOffset.Parse("2026-08-27T07:00:00+00:00");
        var members = new[]
        {
            new ZaloCommunityVoteMember("uid-a", "Cùng Tên", 0),
            new ZaloCommunityVoteMember("uid-b", "Cùng Tên", 0)
        };
        var history = new[]
        {
            Proactive(
                lane: ZaloProactiveLane.Community,
                kind: "member_vote_reengagement",
                subjectUserId: "uid-a",
                subjectName: "Tên Cũ Trước Khi Đổi",
                sentAt: now.AddDays(-1))
        };

        var selected = ZaloCommunityNudgeService.SelectRotatedMember(
            members,
            "member_vote_reengagement",
            history,
            "g1",
            "2026-08-27",
            preferLowerVoteCount: true);

        Assert.NotNull(selected);
        Assert.Equal("uid-b", selected!.UserId);
    }

    [Fact]
    public async Task Proactive_history_is_idempotent_for_the_same_outbound_occurrence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloProactiveMessageStore(db);
        var now = DateTimeOffset.Parse("2026-08-27T07:00:00+00:00");
        var item = Proactive(
            lane: ZaloProactiveLane.SocialPresence,
            kind: nameof(ZaloEngagementMoveKind.HotTake),
            contentKey: "ambient:hot:street:2",
            sentAt: now,
            idempotencyKey: "social-presence:conn-1:g1:20260827:ambient-trash");

        Assert.True(await store.RecordAsync(item));
        Assert.False(await store.RecordAsync(item with { Id = Guid.NewGuid().ToString("n") }));

        var history = await store.GetHistoryAsync("conn-1", "g1");
        Assert.Single(history);
    }

    private static ZaloSocialPresenceSnapshot Snapshot(
        DateTimeOffset now,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history) => new(
        GroupId: "g1",
        Now: now,
        LastUserMessageAt: now.AddHours(-4),
        LastBotMessageAt: now.AddHours(-4),
        BotMessagesToday: 0,
        RecentTwoMinuteMessageCount: 0,
        UpcomingSessionName: null,
        UpcomingSessionAt: null,
        RecentFinishedSessionName: null,
        RecentFinishedSessionAt: null,
        ProactiveHistory: history,
        LegacyAmbientTrashSentToday: false);

    private static ZaloSocialPresenceSettings EnabledPresence() => new(
        Enabled: true,
        SendEnabled: true,
        QuietMinutes: 90,
        MinBotIntervalMinutes: 60,
        MaxProactivePerDay: 4,
        StartHour: 8,
        EndHour: 23,
        TrashTalkLevel: 3);

    private static ZaloProactiveMessageHistoryData Proactive(
        string lane,
        string kind,
        DateTimeOffset sentAt,
        string? contentKey = null,
        string? subjectUserId = null,
        string? subjectName = null,
        string message = "test",
        string idempotencyKey = "test-key") => new(
        Guid.NewGuid().ToString("n"),
        "conn-1",
        "g1",
        sentAt.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd"),
        lane,
        kind,
        contentKey,
        subjectUserId,
        subjectName,
        message,
        sentAt,
        null,
        idempotencyKey);
}
