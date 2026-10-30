using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloVenueGuidanceResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReusesGuidanceFromEquivalentVenueName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "connection-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-1",
            DisplayName = "Bot",
            EncryptedCredentials = "test"
        });
        db.MatchSessions.AddRange(
            new MatchSession
            {
                Id = "ute-old",
                AdminUserId = "admin-1",
                ZaloConnectionId = "connection-1",
                ZaloGroupId = "group-1",
                Name = "CN cũ",
                Location = "UTE",
                LocationImageUrl = "https://example.test/ute.jpg",
                ParkingInstructions = "gửi xe cổng Võ Văn Ngân",
                UpdatedAt = DateTimeOffset.Parse("2026-10-25T12:00:00Z")
            },
            new MatchSession
            {
                Id = "other-newer",
                AdminUserId = "admin-1",
                ZaloConnectionId = "connection-1",
                ZaloGroupId = "group-1",
                Name = "Sân khác",
                Location = "Sân B",
                LocationImageUrl = "https://example.test/other.jpg",
                UpdatedAt = DateTimeOffset.Parse("2026-10-29T12:00:00Z")
            });
        await db.SaveChangesAsync();

        var result = await new ZaloVenueGuidanceResolver(db)
            .ResolveAsync("connection-1", "group-1", "Sân UTE");

        Assert.Equal("https://example.test/ute.jpg", result.LocationImageUrl);
        Assert.Equal("gửi xe cổng Võ Văn Ngân", result.ParkingInstructions);
    }

    [Theory]
    [InlineData("UTE", "Sân UTE")]
    [InlineData("Sân bóng chuyền UTE", "UTE")]
    [InlineData("Nhà thi đấu Hồ Xuân Hương", "Hồ Xuân Hương")]
    public void NormalizeVenueKey_IgnoresGenericVenueWords(string left, string right)
    {
        Assert.Equal(
            ZaloVenueGuidanceResolver.NormalizeVenueKey(left),
            ZaloVenueGuidanceResolver.NormalizeVenueKey(right));
    }
}
