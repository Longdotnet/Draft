using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamPreferenceConfirmationGuardConnectionIsolationTests
{
    [Fact]
    public async Task Abort_removes_only_the_promoted_confirmation_from_the_incoming_account()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = $"guard-isolation-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        };
        var connectionA = new ZaloConnection
        {
            Id = "conn-a",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account-a",
            DisplayName = "Npc A",
            EncryptedCredentials = "test"
        };
        var connectionB = new ZaloConnection
        {
            Id = "conn-b",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account-b",
            DisplayName = "Npc B",
            EncryptedCredentials = "test"
        };
        db.Users.Add(admin);
        db.ZaloConnections.AddRange(connectionA, connectionB);

        var now = DateTimeOffset.UtcNow;
        db.ZaloBotConversationStates.AddRange(
            Promoted("pending-a", connectionA, now),
            Promoted("pending-b", connectionB, now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account-a",
            botId: "bot-account-a",
            groupId: "shared-group",
            messageId: "same-provider-message-id",
            senderId: "user-long",
            senderName: "Long",
            content: "xác nhận",
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: now.ToUnixTimeMilliseconds(),
            quote: null);

        await new ZaloAmbientTeamPreferenceConfirmationGuard(db)
            .AbortPromotedConfirmationAsync(incoming);

        var remaining = await db.ZaloBotConversationStates
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .ToListAsync();
        var survivor = Assert.Single(remaining);
        Assert.Equal("pending-b", survivor.Id);
        Assert.Equal("bot-account-b", survivor.ZaloConnection.AccountZaloId);
    }

    private static ZaloBotConversationState Promoted(
        string id,
        ZaloConnection connection,
        DateTimeOffset now) => new()
    {
        Id = id,
        ZaloConnectionId = connection.Id,
        ZaloConnection = connection,
        GroupId = "shared-group",
        SenderZaloUserId = "user-long",
        PendingIntent = ZaloBotIntent.TeamPreferenceConfirm.ToString(),
        PendingPayloadJson = "{}",
        PreviousCommand = "TeamPreference:Addressed:same-provider-message-id",
        ExpiresAt = now.AddMinutes(5),
        CreatedAt = now,
        UpdatedAt = now
    };
}
