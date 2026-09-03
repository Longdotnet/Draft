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
    public async Task Wrong_sender_cannot_confirm_someone_elses_pending_preview()
    {
        await using var fixture = await Fixture.CreateAsync(ZaloBotIntent.AutoDraftConfirm.ToString());

        var promotion = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-nam", "xác nhận");

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

        public static async Task<Fixture> CreateAsync(string pendingIntent, DateTimeOffset? expiresAt = null)
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
            db.ZaloBotConversationStates.Add(new ZaloBotConversationState
            {
                Id = "pending-1",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                SenderZaloUserId = "user-long",
                PendingIntent = pendingIntent,
                PendingPayloadJson = "[]",
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
