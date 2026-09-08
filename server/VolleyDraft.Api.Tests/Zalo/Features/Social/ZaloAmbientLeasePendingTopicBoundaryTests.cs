using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeasePendingTopicBoundaryTests
{
    [Theory]
    [InlineData("hủy reminder")]
    [InlineData("HUỶ SHARE SLOT")]
    [InlineData("hủy pass")]
    [InlineData("hủy nhận")]
    [InlineData("hủy đặt sân")]
    [InlineData("cancel reminder")]
    public async Task Domain_qualified_cancel_text_does_not_cancel_an_unmentioned_draft_pending(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraftConfirm, withCandidates: false);

        var promotion = await fixture.Policy.TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.Null(promotion);
    }

    [Theory]
    [InlineData("huỷ")]
    [InlineData("cancel")]
    [InlineData("thôi")]
    [InlineData("bỏ qua")]
    [InlineData("không cần nữa")]
    public async Task Bare_cancel_controls_still_cancel_the_current_pending_workflow(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraftConfirm, withCandidates: false);

        var promotion = await fixture.Policy.TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.NotNull(promotion);
        Assert.True(promotion!.IsCancellation);
        Assert.Equal(ZaloBotIntent.AutoDraftConfirm, promotion.PendingIntent);
    }

    [Theory]
    [InlineData("cn đi nhậu không")]
    [InlineData("13/9 có ai rảnh không")]
    [InlineData("t6 chắc vui")]
    [InlineData("cn 13/9 ai đánh")]
    [InlineData("Mai")]
    public async Task Ordinary_chat_that_only_contains_a_session_token_does_not_select_for_npc9(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraft, withCandidates: true);

        var promotion = await fixture.Policy.TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.Null(promotion);
    }

    [Theory]
    [InlineData("cn")]
    [InlineData("chủ nhật")]
    [InlineData("13/9")]
    [InlineData("cn 13/9")]
    [InlineData("chọn cn")]
    public async Task Standalone_grounded_selector_still_continues_npc9_without_a_new_mention(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraft, withCandidates: true);

        var promotion = await fixture.Policy.TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.NotNull(promotion);
        Assert.False(promotion!.IsCancellation);
        Assert.Equal(ZaloBotIntent.AutoDraft, promotion.PendingIntent);
    }

    [Theory]
    [InlineData("cn")]
    [InlineData("13/9")]
    [InlineData("chọn cn")]
    public async Task Same_standalone_selector_boundary_applies_to_npc10(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.TeamImage, withCandidates: true);

        var promotion = await fixture.Policy.TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.NotNull(promotion);
        Assert.False(promotion!.IsCancellation);
        Assert.Equal(ZaloBotIntent.TeamImage, promotion.PendingIntent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
            Policy = new ZaloAmbientLeasePendingContinuationPolicy(db);
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public ZaloAmbientLeasePendingContinuationPolicy Policy { get; }

        public static async Task<Fixture> CreateAsync(ZaloBotIntent pendingIntent, bool withCandidates)
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
                Email = $"pending-boundary-{Guid.NewGuid():n}@example.test",
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

            var candidateIds = new List<string>();
            if (withCandidates)
            {
                var sunday = new MatchSession
                {
                    Id = "session-cn-1309",
                    Name = "CN 13/9",
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    ZaloGroupId = "g1",
                    BotEnabled = true,
                    StartTime = new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7))
                };
                var friday = new MatchSession
                {
                    Id = "session-t6-1109",
                    Name = "T6 11/9",
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    ZaloGroupId = "g1",
                    BotEnabled = true,
                    StartTime = new DateTimeOffset(2026, 9, 11, 17, 45, 0, TimeSpan.FromHours(7))
                };
                db.MatchSessions.AddRange(sunday, friday);
                candidateIds.AddRange([sunday.Id, friday.Id]);
            }

            var updatedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
            db.ZaloBotConversationStates.Add(new ZaloBotConversationState
            {
                Id = "pending-1",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                SenderZaloUserId = "user-long",
                PendingIntent = pendingIntent.ToString(),
                PendingPayloadJson = System.Text.Json.JsonSerializer.Serialize(candidateIds),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            });

            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                Id = "prompt-row",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                MessageId = "prompt-message",
                SenderId = "user-long",
                SenderName = "Long",
                Content = "@Npc 9",
                IsFromBot = false,
                SentAt = updatedAt.AddSeconds(-1),
                ReceivedAt = updatedAt.AddSeconds(-1),
                BotReplySentAt = updatedAt.AddSeconds(1),
                SelectedIntent = PromptIntentForPending(pendingIntent).ToString(),
                ReplyOutcome = "sent"
            });

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        private static ZaloBotIntent PromptIntentForPending(ZaloBotIntent pendingIntent) => pendingIntent switch
        {
            ZaloBotIntent.AutoDraftConfirm => ZaloBotIntent.AutoDraft,
            ZaloBotIntent.RedraftConfirm => ZaloBotIntent.Redraft,
            ZaloBotIntent.RebalanceTeamsConfirm => ZaloBotIntent.RebalanceTeams,
            _ => pendingIntent
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
