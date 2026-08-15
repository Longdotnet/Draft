using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOutboundReceiptCanonicalizationTests
{
    [Fact]
    public async Task Unique_receipt_replaces_legacy_bot_guid_without_storing_raw_content()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);

        const string content = "T6 còn 2 slot - nội dung riêng tư";
        var now = DateTimeOffset.UtcNow;
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "bot:legacy-guid",
            SenderId = "bot-uid",
            SenderName = "Bot",
            Content = content,
            IsFromBot = true,
            SentAt = now,
            ReceivedAt = now
        });
        await db.SaveChangesAsync();

        var receipts = new ZaloOutboundReceiptStore(db);
        var receipt = await receipts.RememberAsync("conn-1", "g1", "provider-123", "incoming-1", content);
        Assert.DoesNotContain(content, receipt.ContentSha256, StringComparison.Ordinal);
        Assert.Equal(64, receipt.ContentSha256.Length);

        var result = await new ZaloLegacyOutboundCanonicalizer(db).CanonicalizeAsync();
        var stored = await db.ZaloGroupMessages.AsNoTracking().SingleAsync(item => item.IsFromBot);

        Assert.Equal(1, result.Canonicalized);
        Assert.Equal("provider-123", stored.MessageId);
        Assert.Equal("ProviderIdCanonicalized", stored.ObservationSource);
        Assert.Empty(await receipts.LoadRecentAsync());
    }

    [Fact]
    public async Task Same_content_twice_in_window_is_left_ambiguous()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 2; i++)
        {
            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = $"bot:legacy-{i}",
                SenderId = "bot-uid",
                SenderName = "Bot",
                Content = "cùng một câu",
                IsFromBot = true,
                SentAt = now.AddSeconds(i),
                ReceivedAt = now.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
        await new ZaloOutboundReceiptStore(db)
            .RememberAsync("conn-1", "g1", "provider-ambiguous", null, "cùng một câu");

        var result = await new ZaloLegacyOutboundCanonicalizer(db).CanonicalizeAsync();

        Assert.Equal(0, result.Canonicalized);
        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(2, await db.ZaloGroupMessages.CountAsync(item => item.MessageId.StartsWith("bot:")));
    }

    [Fact]
    public async Task SaveChanges_uses_unique_parent_receipt_before_persisting_legacy_bot_guid()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await AddTrackedIncomingAsync(db, "incoming-live");

        const string reply = "Long + To An đều ở roster T6.";
        await new ZaloOutboundReceiptStore(db)
            .RememberAsync("conn-1", "g1", "provider-live-123", "incoming-live", reply);

        db.ZaloGroupMessages.Add(BotMessage("bot:temporary-guid", reply));
        await db.SaveChangesAsync();

        var stored = await db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.IsFromBot);
        Assert.Equal("provider-live-123", stored.MessageId);
        Assert.Equal("ProviderIdCanonicalized", stored.ObservationSource);
    }

    [Fact]
    public async Task SaveChanges_does_not_guess_when_parent_message_is_not_tracked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await AddTrackedIncomingAsync(db, "incoming-untracked");
        db.ChangeTracker.Clear();

        const string reply = "Bot trả lời giống nhau";
        await new ZaloOutboundReceiptStore(db)
            .RememberAsync("conn-1", "g1", "provider-untracked", "incoming-untracked", reply);
        db.ZaloGroupMessages.Add(BotMessage("bot:must-remain", reply));

        await db.SaveChangesAsync();

        Assert.True(await db.ZaloGroupMessages.AsNoTracking()
            .AnyAsync(item => item.MessageId == "bot:must-remain"));
        Assert.False(await db.ZaloGroupMessages.AsNoTracking()
            .AnyAsync(item => item.MessageId == "provider-untracked"));
    }

    [Fact]
    public async Task SaveChanges_does_not_guess_when_two_provider_receipts_match_same_parent_and_content()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await AddTrackedIncomingAsync(db, "incoming-ambiguous");

        const string reply = "cùng nội dung cùng parent";
        var receipts = new ZaloOutboundReceiptStore(db);
        await receipts.RememberAsync("conn-1", "g1", "provider-a", "incoming-ambiguous", reply);
        await receipts.RememberAsync("conn-1", "g1", "provider-b", "incoming-ambiguous", reply);
        db.ZaloGroupMessages.Add(BotMessage("bot:ambiguous", reply));

        await db.SaveChangesAsync();

        Assert.True(await db.ZaloGroupMessages.AsNoTracking()
            .AnyAsync(item => item.MessageId == "bot:ambiguous"));
    }

    [Fact]
    public async Task SaveChanges_drops_synthetic_duplicate_when_canonical_provider_row_already_exists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await AddTrackedIncomingAsync(db, "incoming-existing");

        const string reply = "reply already persisted";
        await new ZaloOutboundReceiptStore(db)
            .RememberAsync("conn-1", "g1", "provider-existing", "incoming-existing", reply);
        db.ZaloGroupMessages.Add(BotMessage("provider-existing", reply));
        await db.SaveChangesAsync();

        db.ZaloGroupMessages.Add(BotMessage("bot:duplicate", reply));
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ZaloGroupMessages.AsNoTracking().CountAsync(item => item.IsFromBot));
        Assert.True(await db.ZaloGroupMessages.AsNoTracking()
            .AnyAsync(item => item.MessageId == "provider-existing"));
        Assert.False(await db.ZaloGroupMessages.AsNoTracking()
            .AnyAsync(item => item.MessageId == "bot:duplicate"));
    }

    [Theory]
    [InlineData("bot-uid", "bot-uid:incoming-44", "incoming-44")]
    [InlineData("bot-uid", "memory-v2:bot-uid:incoming-44", null)]
    [InlineData("bot-uid", null, null)]
    public void Parent_message_id_is_parsed_only_from_legacy_send_idempotency_key(
        string accountId,
        string? key,
        string? expected)
    {
        Assert.Equal(expected, ZaloBridgeClient.ParseParentMessageId(accountId, key));
    }

    private static async Task AddTrackedIncomingAsync(VolleyDraftDbContext db, string messageId)
    {
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = messageId,
            SenderId = "user-long",
            SenderName = "Long",
            Content = "incoming",
            IsFromBot = false,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.NotNull(db.ChangeTracker.Entries<ZaloGroupMessage>()
            .SingleOrDefault(entry => entry.Entity.MessageId == messageId));
    }

    private static ZaloGroupMessage BotMessage(string messageId, string content) => new()
    {
        ZaloConnectionId = "conn-1",
        GroupId = "g1",
        MessageId = messageId,
        SenderId = "bot-uid",
        SenderName = "Bot",
        Content = content,
        IsFromBot = true,
        SentAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    private static async Task SeedConnectionAsync(VolleyDraftDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"admin-{Guid.NewGuid():n}@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-uid",
            DisplayName = "Bot",
            EncryptedCredentials = "test"
        });
        await db.SaveChangesAsync();
    }
}