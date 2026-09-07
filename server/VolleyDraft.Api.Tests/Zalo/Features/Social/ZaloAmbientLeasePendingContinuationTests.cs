using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeasePendingContinuationTests
{
    [Theory]
    [InlineData("AutoDraftConfirm")]
    [InlineData("RedraftConfirm")]
    [InlineData("RebalanceTeamsConfirm")]
    public async Task Strong_confirmation_is_allowed_only_for_preview_safe_draft_pending(string pendingIntent)
    {
        await using var fixture = await Fixture.CreateAsync(pendingIntent);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "xác nhận");

        Assert.NotNull(promotion);
        Assert.False(promotion!.IsCancellation);
        Assert.Equal(pendingIntent, promotion.PendingIntent.ToString());
    }

    [Theory]
    [InlineData("AutoDraft", "cn")]
    [InlineData("AutoDraft", "chủ nhật")]
    [InlineData("AutoDraft", "13/9")]
    [InlineData("AutoDraft", "cn 13/9")]
    [InlineData("Redraft", "chủ nhật")]
    public async Task Draft_session_selection_accepts_grounded_short_follow_up_without_mention(
        string pendingIntent,
        string content)
    {
        await using var fixture = await Fixture.CreateAsync(pendingIntent, withDraftCandidates: true);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.NotNull(promotion);
        Assert.False(promotion!.IsCancellation);
        Assert.Equal(pendingIntent, promotion.PendingIntent.ToString());
    }

    [Theory]
    [InlineData("AutoDraft")]
    [InlineData("Redraft")]
    public async Task Draft_session_selection_can_be_cancelled_without_rementioning_bot(string pendingIntent)
    {
        await using var fixture = await Fixture.CreateAsync(pendingIntent, withDraftCandidates: true);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "huỷ");

        Assert.NotNull(promotion);
        Assert.True(promotion!.IsCancellation);
        Assert.Equal(pendingIntent, promotion.PendingIntent.ToString());
    }

    [Fact]
    public async Task Strong_confirmation_does_not_skip_missing_session_selection()
    {
        await using var fixture = await Fixture.CreateAsync(
            ZaloBotIntent.AutoDraft.ToString(),
            withDraftCandidates: true);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "xác nhận");

        Assert.Null(promotion);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("được")]
    [InlineData("chốt")]
    [InlineData("làm đi")]
    [InlineData("nay vui ghê")]
    public async Task Generic_ack_or_chatter_is_not_session_selection_authority(string content)
    {
        await using var fixture = await Fixture.CreateAsync(
            ZaloBotIntent.AutoDraft.ToString(),
            withDraftCandidates: true);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.Null(promotion);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("được")]
    [InlineData("chốt")]
    [InlineData("làm đi")]
    public async Task Generic_ack_is_not_strong_enough_for_no_mention_confirmation(string content)
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraftConfirm.ToString());

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.Null(promotion);
    }

    [Theory]
    [InlineData("ShareSlotConfirm")]
    [InlineData("SlotTransferConfirm")]
    [InlineData("UndoActionConfirm")]
    [InlineData("TeamPreferenceConfirm")]
    public async Task Other_pending_mutations_are_not_promoted_by_conversation_lease(string pendingIntent)
    {
        await using var fixture = await Fixture.CreateAsync(pendingIntent);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "xác nhận");

        Assert.Null(promotion);
    }

    [Fact]
    public async Task Cancel_is_allowed_for_preview_safe_pending_but_does_not_mutate_here()
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.RebalanceTeamsConfirm.ToString());

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "huỷ");

        Assert.NotNull(promotion);
        Assert.True(promotion!.IsCancellation);
        Assert.Single(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Wrong_sender_cannot_continue_someone_elses_draft_selection()
    {
        await using var fixture = await Fixture.CreateAsync(
            ZaloBotIntent.AutoDraft.ToString(),
            withDraftCandidates: true);

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-nam", "cn");

        Assert.Null(promotion);
    }

    [Fact]
    public async Task Pending_candidate_from_another_group_does_not_authorize_selector_promotion()
    {
        await using var fixture = await Fixture.CreateAsync(
            ZaloBotIntent.AutoDraft.ToString(),
            withDraftCandidates: true,
            candidateGroupId: "g2");

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "cn");

        Assert.Null(promotion);
    }

    [Fact]
    public async Task Expired_pending_is_not_promoted()
    {
        await using var fixture = await Fixture.CreateAsync(
            ZaloBotIntent.AutoDraftConfirm.ToString(),
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "xác nhận");

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
            string pendingIntent,
            DateTimeOffset? expiresAt = null,
            bool withDraftCandidates = false,
            string candidateGroupId = "g1")
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
                Email = $"ambient-pending-{Guid.NewGuid():n}@example.test",
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
            if (withDraftCandidates)
            {
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
                candidateIds.AddRange([sunday.Id, wednesday.Id]);
            }

            db.ZaloBotConversationStates.Add(new ZaloBotConversationState
            {
                Id = "pending-1",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                SenderZaloUserId = "user-long",
                PendingIntent = pendingIntent,
                PendingPayloadJson = System.Text.Json.JsonSerializer.Serialize(candidateIds),
                ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
            });
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
