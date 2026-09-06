using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloLegacyPendingStateProjectorTests
{
    [Fact]
    public async Task Legacy_pending_is_projected_to_typed_state_without_raw_payload_blob()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "u1",
            PendingIntent = "SlotTransferConfirm",
            PendingPayloadJson = "{\"SessionId\":\"s6\",\"SourceZaloUserId\":\"u1\",\"SecretNote\":\"do not migrate\"}",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var result = await new ZaloLegacyPendingStateProjector(db).ProjectScopeAsync("g1", "u1");
        var state = await new ZaloConversationStateV2Store(db).LoadActiveAsync("g1", "u1");

        Assert.Equal(1, result.Projected);
        Assert.NotNull(state);
        Assert.Contains("\"sessionId\":\"s6\"", state!.CollectedArgumentsJson, StringComparison.Ordinal);
        Assert.Contains("confirmation", state.MissingArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretNote", state.CollectedArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("do not migrate", state.CollectedArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_different_v2_intent_is_not_overwritten_by_live_legacy_pending()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await SeedSessionAsync(db, "s1", SessionStatus.Setup);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "u1",
            PendingIntent = "AutoDraftConfirm",
            PendingPayloadJson = "[\"s1\"]",
            ExpiresAt = expires
        });
        await db.SaveChangesAsync();
        await new ZaloConversationStateV2Store(db).SaveActiveAsync(
            "g1", "u1", "SlotTransfer", "{}", "[]", "[]", "m-new", "m-new", expires);

        var result = await new ZaloLegacyPendingStateProjector(db).ProjectScopeAsync("g1", "u1");
        var state = await new ZaloConversationStateV2Store(db).LoadActiveAsync("g1", "u1");

        Assert.Equal(1, result.SkippedDifferentIntent);
        Assert.Equal(0, result.RemovedStale);
        Assert.Equal("SlotTransfer", state!.Intent);
        Assert.Equal("m-new", state.LastMessageId);
    }

    [Fact]
    public async Task Missing_auto_draft_target_is_removed_instead_of_trapping_confirmation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "u1",
            PendingIntent = "AutoDraftConfirm",
            PendingPayloadJson = "[\"deleted-session\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var result = await new ZaloLegacyPendingStateProjector(db).ProjectScopeAsync("g1", "u1");

        Assert.Equal(1, result.RemovedStale);
        Assert.Empty(await db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Null(await new ZaloConversationStateV2Store(db).LoadActiveAsync("g1", "u1"));
    }

    [Fact]
    public async Task Auto_draft_confirmation_is_removed_after_session_is_already_finished()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await SeedSessionAsync(db, "s1", SessionStatus.Finished);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "u1",
            PendingIntent = "AutoDraftConfirm",
            PendingPayloadJson = "[\"s1\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var result = await new ZaloLegacyPendingStateProjector(db).ProjectScopeAsync("g1", "u1");

        Assert.Equal(1, result.RemovedStale);
        Assert.Empty(await db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Finished_session_remains_a_valid_redraft_confirmation_target()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedConnectionAsync(db);
        await SeedSessionAsync(db, "s1", SessionStatus.Finished);
        db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "u1",
            PendingIntent = "RedraftConfirm",
            PendingPayloadJson = "[\"s1\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var result = await new ZaloLegacyPendingStateProjector(db).ProjectScopeAsync("g1", "u1");

        Assert.Equal(0, result.RemovedStale);
        Assert.Equal(1, result.Projected);
        Assert.Single(await db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    private static async Task SeedConnectionAsync(VolleyDraftDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"pending-{Guid.NewGuid():n}@example.test",
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

    private static async Task SeedSessionAsync(
        VolleyDraftDbContext db,
        string sessionId,
        SessionStatus status)
    {
        db.MatchSessions.Add(new MatchSession
        {
            Id = sessionId,
            Name = sessionId,
            AdminUserId = "admin-1",
            ZaloConnectionId = "conn-1",
            ZaloGroupId = "g1",
            BotEnabled = true,
            Status = status,
            StartTime = DateTimeOffset.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
    }
}
