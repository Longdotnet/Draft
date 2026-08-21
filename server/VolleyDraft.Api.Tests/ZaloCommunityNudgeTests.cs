using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void Rotation_does_not_repeat_last_type_when_an_alternative_exists()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            new ZaloCommunityNudgeCandidate("feature-a", "A"),
            new ZaloCommunityNudgeCandidate("feature-b", "B"),
            new ZaloCommunityNudgeCandidate("feature-c", "C")
        };
        var history = new[]
        {
            History("feature-a", now.AddMinutes(-1)),
            History("feature-b", now.AddMinutes(-20)),
            History("feature-c", now.AddMinutes(-40))
        };

        var selected = ZaloCommunityNudgeService.SelectRotatedCandidate(
            candidates,
            history,
            "conn-1",
            "g1",
            "2026-08-21",
            2);

        Assert.NotNull(selected);
        Assert.NotEqual("feature-a", selected!.Type);
    }

    [Fact]
    public void Rotation_prefers_least_used_eligible_type()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            new ZaloCommunityNudgeCandidate("feature-a", "A"),
            new ZaloCommunityNudgeCandidate("feature-b", "B"),
            new ZaloCommunityNudgeCandidate("feature-c", "C")
        };
        var history = new[]
        {
            History("feature-a", now.AddMinutes(-1)),
            History("feature-a", now.AddMinutes(-10)),
            History("feature-b", now.AddMinutes(-20))
        };

        var selected = ZaloCommunityNudgeService.SelectRotatedCandidate(
            candidates,
            history,
            "conn-1",
            "g1",
            "2026-08-21",
            3);

        Assert.NotNull(selected);
        Assert.Equal("feature-c", selected!.Type);
    }

    [Fact]
    public async Task Vote_activity_builds_top_30_day_praise_and_single_reengagement_candidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.SeedVoteActivityAsync(now);

        var service = CommunityService(fixture.Db);
        var candidates = await service.BuildVoteActivityCandidatesAsync(
            "conn-1",
            "g1",
            [],
            now);

        var leaderboard = Assert.Single(candidates.Where(item => item.Type == "group_top_voters_30d"));
        Assert.Contains("30 ngày", leaderboard.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Long (6/6 kèo)", leaderboard.Text, StringComparison.Ordinal);
        Assert.Contains("Minh (5/6 kèo)", leaderboard.Text, StringComparison.Ordinal);

        var praise = Assert.Single(candidates.Where(item => item.Type == "member_vote_spotlight"));
        Assert.Equal("Long", praise.SubjectName);
        Assert.Equal("long-user", praise.SubjectUserId);
        Assert.Contains("@Long", praise.Text, StringComparison.Ordinal);

        var reengage = Assert.Single(candidates.Where(item => item.Type == "member_vote_reengagement"));
        Assert.Equal("Nam", reengage.SubjectName);
        Assert.Equal("nam-user", reengage.SubjectUserId);
        Assert.Contains("@Nam", reengage.Text, StringComparison.Ordinal);

        var mentions = ZaloCommunityNudgeService.BuildMentions(reengage);
        var mention = Assert.Single(mentions);
        Assert.Equal("nam-user", mention.Uid);
        Assert.Equal(reengage.Text.IndexOf("@Nam", StringComparison.Ordinal), mention.Pos);
        Assert.Equal("@Nam".Length, mention.Len);
    }

    [Fact]
    public async Task Recent_subject_is_not_selected_for_another_personal_nudge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.SeedVoteActivityAsync(now);

        var history = new[]
        {
            new ZaloCommunityNudgeHistoryData(
                Guid.NewGuid().ToString("n"),
                "conn-1",
                "g1",
                now.ToString("yyyy-MM-dd"),
                1,
                "member_vote_spotlight",
                "Long",
                "old",
                now.AddDays(-2),
                null),
            new ZaloCommunityNudgeHistoryData(
                Guid.NewGuid().ToString("n"),
                "conn-1",
                "g1",
                now.ToString("yyyy-MM-dd"),
                2,
                "member_vote_reengagement",
                "Nam",
                "old",
                now.AddDays(-2),
                null)
        };

        var service = CommunityService(fixture.Db);
        var candidates = await service.BuildVoteActivityCandidatesAsync(
            "conn-1",
            "g1",
            history,
            now);

        Assert.DoesNotContain(
            candidates,
            item => item.SubjectName is "Long" or "Nam");
    }

    private static ZaloCommunityNudgeService CommunityService(VolleyDraftDbContext db) =>
        new(
            db,
            new ZaloBridgeClient(new HttpClient()),
            NullLogger<ZaloCommunityNudgeService>.Instance);

    private static ZaloCommunityNudgeHistoryData History(string type, DateTimeOffset sentAt) =>
        new(
            Guid.NewGuid().ToString("n"),
            "conn-1",
            "g1",
            sentAt.ToString("yyyy-MM-dd"),
            1,
            type,
            null,
            type,
            sentAt,
            null);

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

        public async Task SeedVoteActivityAsync(DateTimeOffset now)
        {
            Db.ZaloGroupMembers.AddRange(
                Member("long-user", "Long"),
                Member("minh-user", "Minh"),
                Member("an-user", "An"),
                Member("nam-user", "Nam"));

            for (var index = 0; index < 6; index += 1)
            {
                var poll = new ZaloPollSnapshot
                {
                    Id = $"poll-snapshot-{index}",
                    ZaloConnectionId = "conn-1",
                    GroupId = "g1",
                    PollId = $"poll-{index}",
                    Question = $"Kèo {index + 1}",
                    CreatorZaloUserId = "owner-user",
                    CreatedAtFromZalo = now.AddDays(-(index * 4 + 1)),
                    UpdatedAtFromZalo = now.AddDays(-(index * 4 + 1)),
                    FirstObservedAt = now.AddDays(-(index * 4 + 1)),
                    LastObservedAt = now,
                    HasVoterIdentities = true,
                    IsAnalyticsEligible = true
                };
                var option = new ZaloPollOptionSnapshot
                {
                    Id = $"option-snapshot-{index}",
                    PollSnapshotId = poll.Id,
                    ZaloOptionId = $"option-{index}",
                    Content = "Tham gia",
                    PollSnapshot = poll
                };
                poll.Options.Add(option);
                Db.ZaloPollSnapshots.Add(poll);

                AddVote(poll, option, "long-user", index);
                if (index < 5) AddVote(poll, option, "minh-user", index);
                if (index < 4) AddVote(poll, option, "an-user", index);
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private ZaloGroupMember Member(string userId, string displayName) =>
            new()
            {
                Id = $"member-{userId}",
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                ZaloUserId = userId,
                DisplayName = displayName,
                IsCurrentMember = true,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-120),
                LastSeenAt = DateTimeOffset.UtcNow,
                LastSyncedAt = DateTimeOffset.UtcNow
            };

        private void AddVote(
            ZaloPollSnapshot poll,
            ZaloPollOptionSnapshot option,
            string userId,
            int index)
        {
            Db.ZaloPollVoteActivities.Add(new ZaloPollVoteActivity
            {
                Id = $"vote-{index}-{userId}",
                PollSnapshotId = poll.Id,
                PollOptionSnapshotId = option.Id,
                ZaloUserId = userId,
                IsCurrentlySelected = true,
                PollSnapshot = poll,
                PollOptionSnapshot = option
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
