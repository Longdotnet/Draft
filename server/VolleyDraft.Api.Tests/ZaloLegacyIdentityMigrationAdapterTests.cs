using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloLegacyIdentityMigrationAdapterTests
{
    [Fact]
    public async Task Exact_display_name_and_approved_alias_become_metadata_uid_mentions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedAsync(db,
            ("u-long", "Long Nguyễn"),
            ("u-tung", "Tùng Phạm"),
            ("u-sender", "Người Gửi"));
        await RememberAliasAsync(db, "u-long", "Long Nguyễn", "Tồ");

        var incoming = Addressed("đổi Tồ với Tùng Phạm", "u-sender");
        var result = await new ZaloLegacyIdentityMigrationAdapter(db).EnrichAsync("g1", incoming);

        Assert.Contains("u-long", result.AddedZaloUserIds);
        Assert.Contains("u-tung", result.AddedZaloUserIds);
        Assert.Contains(incoming.Mentions, item => item.Uid == "u-long" && item.Len == 0);
        Assert.Contains(incoming.Mentions, item => item.Uid == "u-tung" && item.Len == 0);
    }

    [Fact]
    public async Task Ambiguous_alias_is_not_promoted_to_any_uid()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await SeedAsync(db,
            ("u1", "Nguyễn Long"),
            ("u2", "Trần Long"),
            ("u-sender", "Người Gửi"));
        await RememberAliasAsync(db, "u1", "Nguyễn Long", "Đại Ca");
        await RememberAliasAsync(db, "u2", "Trần Long", "Đại Ca");

        var incoming = Addressed("xem hoạt động Đại Ca", "u-sender");
        var result = await new ZaloLegacyIdentityMigrationAdapter(db).EnrichAsync("g1", incoming);

        Assert.Empty(result.AddedZaloUserIds);
        Assert.Contains(result.Resolutions, item => item.Status == ZaloIdentityResolutionStatus.Ambiguous);
        Assert.DoesNotContain(incoming.Mentions, item => item.Uid is "u1" or "u2");
    }

    private static ZaloIncomingMessageEvent Addressed(string content, string senderId) => new(
        "bot-uid",
        "bot-uid",
        "g1",
        Guid.NewGuid().ToString("n"),
        senderId,
        "Người Gửi",
        content,
        [new ZaloBridgeMention("bot-uid", 0, 4)],
        true,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static async Task RememberAliasAsync(
        VolleyDraftDbContext db,
        string uid,
        string displayName,
        string alias)
    {
        await new ZaloUserConceptStore(db).RememberAsync(
            "g1",
            new ZaloAiSender(uid, displayName),
            new ZaloUserConceptDraft(
                "Alias",
                "preferred_name",
                JsonSerializer.Serialize(new { name = alias })));
    }

    private static async Task SeedAsync(
        VolleyDraftDbContext db,
        params (string Uid, string Name)[] members)
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
        foreach (var member in members)
        {
            db.ZaloGroupMembers.Add(new ZaloGroupMember
            {
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                ZaloUserId = member.Uid,
                DisplayName = member.Name,
                IsCurrentMember = true
            });
        }
        await db.SaveChangesAsync();
    }
}
