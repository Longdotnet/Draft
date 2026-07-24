using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberActivityServiceTests
{
    [Fact]
    public void Four_month_period_uses_calendar_subtraction_in_vietnam_timezone()
    {
        var now = new DateTimeOffset(2026, 7, 24, 16, 30, 0, TimeSpan.FromHours(7));

        var period = ZaloMemberActivityService.ParsePeriod("4 tháng", now);

        Assert.Equal(new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.FromHours(7)), period.Start.ToOffset(TimeSpan.FromHours(7)));
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.FromHours(7)), period.End.ToOffset(TimeSpan.FromHours(7)));
    }

    [Theory]
    [InlineData("90 ngày", 2026, 4, 25)]
    [InlineData("tháng này", 2026, 7, 1)]
    [InlineData("từ tháng 3", 2026, 3, 1)]
    public void Natural_periods_are_parsed_deterministically(string text, int year, int month, int day)
    {
        var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(7));

        var period = ZaloMemberActivityService.ParsePeriod(text, now);

        Assert.Equal(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(7)), period.Start.ToOffset(TimeSpan.FromHours(7)));
    }

    [Fact]
    public async Task Multi_choice_vote_counts_as_one_poll_but_preserves_selected_option_count()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        fixture.AddMember("u1", "Nguyễn A");
        fixture.AddMember("u2", "Trần B");
        var poll = fixture.AddPoll("poll-1", "Đăng ký sân Thứ 6", fixture.Now.AddDays(-5));
        fixture.AddVote(poll, "option-a", "T4", "u1");
        fixture.AddVote(poll, "option-b", "T6", "u1");
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.QueryGroupAsync(
            ActivityFixture.ConnectionId,
            ActivityFixture.GroupId,
            fixture.Period,
            ZaloMemberActivityFilter.All,
            1,
            10,
            CancellationToken.None);

        var member = Assert.Single(result.Items, item => item.ZaloUserId == "u1");
        Assert.Equal(1, member.VotedPollCount);
        Assert.Equal(2, member.TotalSelectedOptions);
        Assert.Equal(1d, member.VoteParticipationRate);
        Assert.Null(member.ExactUserVoteAt);
        Assert.Equal("Đăng ký sân Thứ 6", member.LastVotedPollQuestion);
    }

    [Fact]
    public async Task No_vote_query_excludes_former_members_and_reports_last_poll_before_range()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        fixture.AddMember("current", "Current");
        fixture.AddMember("former", "Former", isCurrent: false);
        var oldPoll = fixture.AddPoll("old", "Poll cũ", fixture.Now.AddDays(-130));
        fixture.AddVote(oldPoll, "old-option", "Có", "current");
        fixture.AddPoll("recent", "Poll mới", fixture.Now.AddDays(-10));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.QueryGroupAsync(
            ActivityFixture.ConnectionId,
            ActivityFixture.GroupId,
            fixture.Period,
            ZaloMemberActivityFilter.NoVote,
            1,
            10,
            CancellationToken.None);

        var member = Assert.Single(result.Items);
        Assert.Equal("current", member.ZaloUserId);
        Assert.Equal("Poll cũ", member.LastVotedPollQuestion);
        Assert.Equal(0, member.VotedPollCount);
        Assert.DoesNotContain(result.Items, item => item.ZaloUserId == "former");
    }

    [Fact]
    public async Task Bot_messages_do_not_count_as_member_activity()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        fixture.AddMember("u1", "Nguyễn A");
        fixture.Db.ZaloGroupMessages.AddRange(
            fixture.Message("member-message", "u1", fixture.Now.AddDays(-3), false),
            fixture.Message("bot-message", "u1", fixture.Now.AddDays(-1), true));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.QueryGroupAsync(
            ActivityFixture.ConnectionId,
            ActivityFixture.GroupId,
            fixture.Period,
            ZaloMemberActivityFilter.All,
            1,
            10,
            CancellationToken.None);

        var member = Assert.Single(result.Items);
        Assert.Equal(1, member.MessageCount);
        Assert.Equal(fixture.Now.AddDays(-3), member.LastMessageAt);
    }

    [Fact]
    public async Task Pagination_is_stable_and_uses_uid_as_identity()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        for (var index = 1; index <= 12; index++)
            fixture.AddMember($"uid-{index:00}", $"Member {index:00}");
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.QueryGroupAsync(
            ActivityFixture.ConnectionId,
            ActivityFixture.GroupId,
            fixture.Period,
            ZaloMemberActivityFilter.All,
            1,
            10,
            CancellationToken.None);
        var second = await fixture.Service.QueryGroupAsync(
            ActivityFixture.ConnectionId,
            ActivityFixture.GroupId,
            fixture.Period,
            ZaloMemberActivityFilter.All,
            2,
            10,
            CancellationToken.None);

        Assert.Equal(12, first.TotalItems);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(item => item.ZaloUserId).Intersect(second.Items.Select(item => item.ZaloUserId)));
    }

    [Fact]
    public async Task Duplicate_member_uid_is_rejected_even_when_display_name_changes()
    {
        await using var fixture = await ActivityFixture.CreateAsync();
        fixture.AddMember("same-uid", "Tên cũ");
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            ZaloConnectionId = ActivityFixture.ConnectionId,
            GroupId = ActivityFixture.GroupId,
            ZaloUserId = "same-uid",
            DisplayName = "Tên mới"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    private sealed class ActivityFixture : IAsyncDisposable
    {
        public const string ConnectionId = "connection";
        public const string GroupId = "group";
        private readonly SqliteConnection connection;
        public VolleyDraftDbContext Db { get; }
        public ZaloMemberActivityService Service { get; }
        public DateTimeOffset Now { get; } = new(2026, 7, 24, 5, 0, 0, TimeSpan.Zero);
        public ZaloActivityPeriod Period => new(Now.AddDays(-90), Now.AddDays(1), "90 ngày");

        private ActivityFixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            this.connection = connection;
            Db = db;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ZaloActivityRules:NewMemberDays"] = "14",
                    ["ZaloActivityRules:ActiveDays"] = "14",
                    ["ZaloActivityRules:RegularDays"] = "30",
                    ["ZaloActivityRules:InactiveDays"] = "90",
                    ["ZaloActivityRules:AtRiskMissedPolls"] = "3"
                })
                .Build();
            Service = new ZaloMemberActivityService(
                db,
                configuration,
                NullLogger<ZaloMemberActivityService>.Instance);
        }

        public static async Task<ActivityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = "admin@activity.test",
                PasswordHash = "hash"
            });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = ConnectionId,
                AdminUserId = "admin",
                AccountZaloId = "bot",
                DisplayName = "Bot",
                EncryptedCredentials = "encrypted",
                Status = ZaloConnectionStatus.Connected
            });
            db.ZaloActivityBackfillJobs.Add(new ZaloActivityBackfillJob
            {
                Id = "job",
                ZaloConnectionId = ConnectionId,
                GroupId = GroupId,
                Stage = ZaloActivityBackfillStage.Completed,
                Status = ZaloActivityBackfillStatus.CompletedWithLimitations,
                MessageHistoryCapability = ZaloMessageHistoryCapability.PartialHistoricalBackfill,
                OldestRetrievablePollAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                NewestRetrievablePollAt = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
            return new ActivityFixture(connection, db);
        }

        public void AddMember(string uid, string name, bool isCurrent = true)
        {
            Db.ZaloGroupMembers.Add(new ZaloGroupMember
            {
                Id = $"member-{uid}",
                ZaloConnectionId = ConnectionId,
                GroupId = GroupId,
                ZaloUserId = uid,
                DisplayName = name,
                FirstSeenAt = Now.AddDays(-180),
                LastSeenAt = Now,
                LastSyncedAt = Now,
                IsCurrentMember = isCurrent,
                LeftAt = isCurrent ? null : Now.AddDays(-1),
                CreatedAt = Now.AddDays(-180),
                UpdatedAt = Now
            });
        }

        public ZaloPollSnapshot AddPoll(string id, string question, DateTimeOffset createdAt)
        {
            var poll = new ZaloPollSnapshot
            {
                Id = id,
                ZaloConnectionId = ConnectionId,
                GroupId = GroupId,
                PollId = id,
                Question = question,
                CreatedAtFromZalo = createdAt,
                UpdatedAtFromZalo = createdAt,
                FirstObservedAt = createdAt.AddHours(1),
                LastObservedAt = Now,
                HasVoterIdentities = true,
                IsAnalyticsEligible = true,
                CreatedAt = createdAt.AddHours(1),
                UpdatedAt = Now
            };
            Db.ZaloPollSnapshots.Add(poll);
            return poll;
        }

        public void AddVote(
            ZaloPollSnapshot poll,
            string optionId,
            string content,
            string uid)
        {
            var option = new ZaloPollOptionSnapshot
            {
                Id = $"{poll.Id}-{optionId}",
                PollSnapshot = poll,
                PollSnapshotId = poll.Id,
                ZaloOptionId = optionId,
                Content = content,
                FirstObservedAt = poll.FirstObservedAt,
                LastObservedAt = Now,
                CreatedAt = poll.FirstObservedAt,
                UpdatedAt = Now
            };
            var vote = new ZaloPollVoteActivity
            {
                Id = $"{option.Id}-{uid}",
                PollSnapshot = poll,
                PollSnapshotId = poll.Id,
                PollOptionSnapshot = option,
                PollOptionSnapshotId = option.Id,
                ZaloUserId = uid,
                FirstObservedAt = poll.FirstObservedAt,
                LastObservedAt = Now,
                IsCurrentlySelected = true,
                CreatedAt = poll.FirstObservedAt,
                UpdatedAt = Now
            };
            option.Votes.Add(vote);
            poll.Options.Add(option);
        }

        public ZaloGroupMessage Message(
            string id,
            string senderId,
            DateTimeOffset sentAt,
            bool isFromBot) =>
            new()
            {
                Id = id,
                ZaloConnectionId = ConnectionId,
                GroupId = GroupId,
                MessageId = id,
                SenderId = senderId,
                SenderName = senderId,
                Content = "not asserted",
                IsFromBot = isFromBot,
                SentAt = sentAt,
                ReceivedAt = sentAt,
                FirstObservedAt = sentAt,
                LastObservedAt = sentAt
            };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
