using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamPreferenceHandoffConnectionScopeTests
{
    [Fact]
    public async Task Legacy_proposal_bound_to_another_connection_cannot_be_promoted_or_migrated()
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
            Email = $"scope-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        };
        var connectionA = new ZaloConnection
        {
            Id = "conn-a",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "account-a",
            DisplayName = "Bot A",
            EncryptedCredentials = "test"
        };
        var connectionB = new ZaloConnection
        {
            Id = "conn-b",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "account-b",
            DisplayName = "Bot B",
            EncryptedCredentials = "test"
        };
        db.Users.Add(admin);
        db.ZaloConnections.AddRange(connectionA, connectionB);
        db.MatchSessions.AddRange(
            new MatchSession
            {
                Id = "session-a",
                AdminUserId = admin.Id,
                ZaloConnectionId = connectionA.Id,
                ZaloConnection = connectionA,
                ZaloGroupId = "same-group",
                Name = "A",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 2,
                TeamSize = 6
            },
            new MatchSession
            {
                Id = "session-b",
                AdminUserId = admin.Id,
                ZaloConnectionId = connectionB.Id,
                ZaloConnection = connectionB,
                ZaloGroupId = "same-group",
                Name = "B",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 2,
                TeamSize = 6
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var collected = JsonSerializer.Serialize(new
        {
            requesterZaloUserId = "same-user",
            requesterDisplayName = "Long",
            partnerZaloUserId = "partner",
            partnerDisplayName = "Partner",
            sessionId = "session-a",
            sessionName = "A"
        });
        await new ZaloConversationStateV2Store(db).SaveActiveAsync(
            "same-group",
            "same-user",
            ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
            collected,
            "[]",
            "[]",
            "same-source",
            "same-source",
            DateTimeOffset.UtcNow.AddMinutes(5));

        var incoming = new ZaloIncomingMessageEvent(
            accountId: "account-b",
            botId: "account-b",
            groupId: "same-group",
            messageId: "confirm-b",
            senderId: "same-user",
            senderName: "Long",
            content: "xác nhận",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: new ZaloBridgeMessageQuote(
                "same-provider-reply",
                "account-b",
                "Bot B",
                "proposal",
                "text",
                DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
                null));

        Assert.False(await new ZaloAmbientTeamPreferenceHandoff(db)
            .TryPromoteExactReplyConfirmationAsync(incoming));
        Assert.Empty(await db.ZaloBotConversationStates.AsNoTracking().ToListAsync());

        using (ZaloConversationStateScope.Push("conn-b"))
            Assert.Null(await new ZaloConversationStateV2Store(db)
                .LoadActiveAsync("same-group", "same-user"));

        using (ZaloConversationStateScope.Push(null))
        {
            var legacy = await new ZaloConversationStateV2Store(db)
                .LoadActiveAsync("same-group", "same-user");
            Assert.NotNull(legacy);
            Assert.Equal("session-a", JsonDocument.Parse(legacy!.CollectedArgumentsJson)
                .RootElement.GetProperty("sessionId").GetString());
        }
    }
}
