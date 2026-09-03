using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMessageGraphReconciliationTests
{
    [Fact]
    public async Task Direct_quote_replaces_unique_legacy_bot_guid_with_provider_message_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin-graph@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-uid",
            DisplayName = "Volley Bot",
            EncryptedCredentials = "test"
        });
        var sentAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "bot:legacy-guid",
            SenderId = "bot-uid",
            SenderName = "Volley Bot",
            Content = "T6 còn 2 slot",
            IsFromBot = true,
            SentAt = sentAt,
            ReceivedAt = sentAt
        });
        await db.SaveChangesAsync();

        var incoming = new ZaloIncomingMessageEvent(
            "bot-uid",
            "bot-uid",
            "g1",
            "user-message-2",
            "u1",
            "Long",
            "cái đó đăng ký tui đi",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "provider-bot-message-1",
                "bot-uid",
                "Volley Bot",
                "T6 còn 2 slot",
                "chat",
                sentAt.ToUnixTimeMilliseconds(),
                null));

        var store = new ZaloMessageGraphStore(db);
        var relation = await store.RememberIncomingQuoteAsync("conn-1", incoming);
        var reconciled = await db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.GroupId == "g1" && item.IsFromBot);

        Assert.NotNull(relation);
        Assert.Equal("provider-bot-message-1", relation!.ToMessageId);
        Assert.Equal("provider-bot-message-1", reconciled.MessageId);
        Assert.Equal("ProviderIdReconciled", reconciled.ObservationSource);
    }

    [Fact]
    public async Task Ambiguous_same_content_without_timestamp_is_not_reconciled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User { Id = "admin-1", DisplayName = "Admin", Email = "admin-amb@example.test", PasswordHash = "test" });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1", AdminUserId = "admin-1", AccountZaloId = "bot-uid", DisplayName = "Bot", EncryptedCredentials = "test"
        });
        for (var index = 0; index < 2; index++)
        {
            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = $"bot:legacy-{index}",
                SenderId = "bot-uid",
                SenderName = "Bot",
                Content = "giống nhau",
                IsFromBot = true,
                SentAt = DateTimeOffset.UtcNow.AddMinutes(-index),
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var reconciled = await new ZaloMessageGraphStore(db).ReconcileQuotedLegacyBotMessageAsync(
            "conn-1", "g1", "provider-id", "giống nhau", null);

        Assert.False(reconciled);
        Assert.Equal(2, await db.ZaloGroupMessages.CountAsync(item => item.MessageId.StartsWith("bot:")));
    }
}
