using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotPersistenceTests
{
    [Fact]
    public async Task Conversation_state_is_isolated_by_connection_group_and_sender()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Db.ZaloBotConversationStates.AddRange(
            State("connection", "group-a", "user-a"),
            State("connection", "group-a", "user-b"),
            State("connection", "group-b", "user-a"));
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(3, await fixture.Db.ZaloBotConversationStates.CountAsync());
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            fixture.Db.ZaloBotConversationStates.Add(State("connection", "group-a", "user-a"));
            await fixture.Db.SaveChangesAsync();
        });
    }

    [Fact]
    public void Conversation_expiry_uses_absolute_utc_deadline()
    {
        var state = State("connection", "group", "user");
        state.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(state.ExpiresAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Incoming_message_id_is_unique_per_connection_for_idempotency()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Db.ZaloGroupMessages.Add(Message("same-message"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ZaloGroupMessages.Add(Message("same-message"));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    private static ZaloBotConversationState State(string connection, string group, string sender) => new()
    {
        ZaloConnectionId = connection,
        GroupId = group,
        SenderZaloUserId = sender,
        PendingIntent = "PaymentQr",
        PendingPayloadJson = "[]",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
    };

    private static ZaloGroupMessage Message(string messageId) => new()
    {
        ZaloConnectionId = "connection",
        GroupId = "group",
        MessageId = messageId,
        SenderId = "sender",
        SenderName = "Sender",
        Content = "@bot help",
        SentAt = DateTimeOffset.UtcNow
    };

    private sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public VolleyDraftDbContext Db { get; }

        private DbFixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User { Id = "admin", DisplayName = "Admin", Email = "admin@test.local", PasswordHash = "hash" });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection",
                AdminUserId = "admin",
                AccountZaloId = "bot",
                DisplayName = "Bot",
                EncryptedCredentials = "encrypted",
                Status = ZaloConnectionStatus.Connected
            });
            await db.SaveChangesAsync();
            return new DbFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
