using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeaseTeamImageContinuationTests
{
    [Theory]
    [InlineData("cn")]
    [InlineData("chủ nhật")]
    [InlineData("13/9")]
    [InlineData("cn 13/9")]
    public async Task Team_image_session_selection_accepts_grounded_short_follow_up_without_mention(string content)
    {
        await using var fixture = await Fixture.CreateAsync();

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.NotNull(promotion);
        Assert.Equal(ZaloBotIntent.TeamImage, promotion!.PendingIntent);
        Assert.False(promotion.IsCancellation);
    }

    [Fact]
    public async Task Team_image_session_selection_can_be_cancelled_without_rementioning_bot()
    {
        await using var fixture = await Fixture.CreateAsync();

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "huỷ");

        Assert.NotNull(promotion);
        Assert.Equal(ZaloBotIntent.TeamImage, promotion!.PendingIntent);
        Assert.True(promotion.IsCancellation);
    }

    [Fact]
    public async Task Team_image_selector_requires_its_latest_successful_prompt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddReplyAsync(ZaloBotIntent.GeneralChat, DateTimeOffset.UtcNow, "later-chat");

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "cn");

        Assert.Null(promotion);
    }

    [Fact]
    public async Task Team_image_selector_is_not_promoted_when_clarification_never_reached_user()
    {
        await using var fixture = await Fixture.CreateAsync(withPromptReply: false);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "cn");

        Assert.Null(promotion);
    }

    [Fact]
    public async Task Team_image_selector_cannot_escape_candidate_group_scope()
    {
        await using var fixture = await Fixture.CreateAsync(candidateGroupId: "g2");

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "cn");

        Assert.Null(promotion);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(
            bool withPromptReply = true,
            string candidateGroupId = "g1")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"npc10-followup-{Guid.NewGuid():n}@example.test",
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

            var sunday = new MatchSession
            {
                Id = "session-cn-1309",
                Name = "CN 13/9",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = candidateGroupId,
                BotEnabled = true,
                StartTime = new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7))
            };
            var wednesday = new MatchSession
            {
                Id = "session-t4-0909",
                Name = "T4 9/9",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = candidateGroupId,
                BotEnabled = true,
                StartTime = new DateTimeOffset(2026, 9, 9, 17, 45, 0, TimeSpan.FromHours(7))
            };
            db.MatchSessions.AddRange(sunday, wednesday);

            var pendingUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
            db.ZaloBotConversationStates.Add(new ZaloBotConversationState
            {
                Id = "pending-team-image",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                SenderZaloUserId = "user-long",
                PendingIntent = ZaloBotIntent.TeamImage.ToString(),
                PendingPayloadJson = System.Text.Json.JsonSerializer.Serialize(new[] { sunday.Id, wednesday.Id }),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = pendingUpdatedAt,
                UpdatedAt = pendingUpdatedAt
            });

            if (withPromptReply)
            {
                db.ZaloGroupMessages.Add(new ZaloGroupMessage
                {
                    Id = "team-image-prompt-row",
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    GroupId = "g1",
                    MessageId = "team-image-prompt",
                    SenderId = "user-long",
                    SenderName = "Long",
                    Content = "@Npc 10",
                    IsFromBot = false,
                    SentAt = pendingUpdatedAt.AddSeconds(-1),
                    ReceivedAt = pendingUpdatedAt.AddSeconds(-1),
                    BotReplySentAt = pendingUpdatedAt.AddSeconds(1),
                    SelectedIntent = ZaloBotIntent.TeamImage.ToString(),
                    ReplyOutcome = "sent"
                });
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async Task AddReplyAsync(ZaloBotIntent intent, DateTimeOffset sentAt, string messageId)
        {
            Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                Id = $"{messageId}-row",
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = messageId,
                SenderId = "user-long",
                SenderName = "Long",
                Content = "unrelated",
                IsFromBot = false,
                SentAt = sentAt.AddMilliseconds(-100),
                ReceivedAt = sentAt.AddMilliseconds(-100),
                BotReplySentAt = sentAt,
                SelectedIntent = intent.ToString(),
                ReplyOutcome = "sent"
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
