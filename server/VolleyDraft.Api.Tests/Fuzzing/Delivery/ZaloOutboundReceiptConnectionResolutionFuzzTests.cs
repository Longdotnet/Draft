using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOutboundReceiptConnectionResolutionFuzzTests
{
    [Fact]
    public async Task Tracked_group_without_match_session_still_resolves_provider_receipt_connection()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=receipt-resolution-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedOnlyGroupAsync(options, seed);

            await using var db = new VolleyDraftDbContext(options);
            var resolved = await ZaloBridgeClient.ResolveOutboundConnectionIdAsync(
                db,
                "bot-account",
                seed % 2 == 0 ? " g1 " : "g1");

            Assert.Equal(
                "conn-target",
                resolved);
        }
    }

    [Fact]
    public async Task Tracked_group_resolution_is_scoped_by_group_when_same_account_has_newer_connection()
    {
        var connectionString = $"Data Source=receipt-resolution-scope-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await SeedTrackedOnlyGroupAsync(options, 9001);

        await using var db = new VolleyDraftDbContext(options);
        var resolved = await ZaloBridgeClient.ResolveOutboundConnectionIdAsync(db, "bot-account", "g1");

        Assert.Equal("conn-target", resolved);
    }

    private static async Task SeedTrackedOnlyGroupAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = $"admin-{seed}",
            DisplayName = "Admin",
            Email = $"receipt-resolution-{seed}@example.test",
            PasswordHash = "x"
        };
        var target = new ZaloConnection
        {
            Id = "conn-target",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc target",
            EncryptedCredentials = "x",
            Status = ZaloConnectionStatus.Connected,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var newerWrongGroup = new ZaloConnection
        {
            Id = "conn-newer-wrong-group",
            AdminUserId = admin.Id,
            AccountZaloId = "bot-account",
            DisplayName = "Npc newer",
            EncryptedCredentials = "x",
            Status = ZaloConnectionStatus.Connected,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(admin, target, newerWrongGroup);
        await db.SaveChangesAsync();

        await new ZaloAutoSessionStore(db).EnsureAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloTrackedGroups" (
                "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "AutoSessionEnabled", "CreatedAt", "UpdatedAt")
            VALUES (
                {{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{target.Id}}, {{"g1"}}, {{"g1"}}, {{1}}, {{now}}, {{now}}),
                ({{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{newerWrongGroup.Id}}, {{"g-other"}}, {{"g-other"}}, {{1}}, {{now}}, {{now}});
            """);
        db.ChangeTracker.Clear();
    }
}
