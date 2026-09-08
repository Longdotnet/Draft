using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeasePendingReplyAddressingTests
{
    [Fact]
    public async Task Verified_reply_to_bot_keeps_natural_session_sentence_while_ambient_copy_stays_silent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"reply-addressing-{Guid.NewGuid():n}@example.test",
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
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(sunday);

        var updatedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            Id = "pending-1",
            ZaloConnectionId = zalo.Id,
            ZaloConnection = zalo,
            GroupId = "g1",
            SenderZaloUserId = "user-long",
            PendingIntent = ZaloBotIntent.AutoDraft.ToString(),
            PendingPayloadJson = System.Text.Json.JsonSerializer.Serialize(new[] { sunday.Id }),
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
            SelectedIntent = ZaloBotIntent.AutoDraft.ToString(),
            ReplyOutcome = "sent"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var policy = new ZaloAmbientLeasePendingContinuationPolicy(db);
        const string naturalReply = "chọn trận chủ nhật nha";

        var ambient = await policy.TryResolveAsync("conn-1", "g1", "user-long", naturalReply);
        var replied = await policy.TryResolveAsync(
            "conn-1",
            "g1",
            "user-long",
            naturalReply,
            explicitlyAddressedByReply: true);

        Assert.Null(ambient);
        Assert.NotNull(replied);
        Assert.False(replied!.IsCancellation);
        Assert.Equal(ZaloBotIntent.AutoDraft, replied.PendingIntent);
    }
}
