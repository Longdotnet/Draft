using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionSettingsStoreTests
{
    [Fact]
    public async Task InsertAndUpdate_PreservesAdminOwnershipAndMutableSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionSettingsStore(db);

        var tracked = await store.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng chuyền UTE"
        });

        Assert.NotNull(await store.GetForAdminAsync("admin-a", tracked.Id));
        Assert.Null(await store.GetForAdminAsync("admin-b", tracked.Id));

        tracked.AutoSessionEnabled = false;
        tracked.RequireOrganizerApproval = false;
        tracked.DefaultTeamSize = 7;
        tracked.DefaultTotalSets = 5;
        tracked.DefaultStartMinutes = 18 * 60;
        tracked.AssumePmForHourUnder12 = false;
        tracked.DefaultLocation = "Sân A";
        tracked.BotEnabledForCreatedSessions = false;

        var updated = await store.UpdateAsync(tracked);

        Assert.NotNull(updated);
        Assert.False(updated!.AutoSessionEnabled);
        Assert.False(updated.RequireOrganizerApproval);
        Assert.Equal(3, updated.DefaultTeamCount);
        Assert.Equal(7, updated.DefaultTeamSize);
        Assert.Equal(5, updated.DefaultTotalSets);
        Assert.Equal(18 * 60, updated.DefaultStartMinutes);
        Assert.False(updated.AssumePmForHourUnder12);
        Assert.Equal("Sân A", updated.DefaultLocation);
        Assert.False(updated.BotEnabledForCreatedSessions);
    }

    [Fact]
    public async Task InsertIfMissing_DoesNotResetExistingUserConfiguration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionSettingsStore(db);

        var first = await store.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Tên đầu",
            DefaultTeamSize = 8,
            DefaultLocation = "Sân cũ"
        });
        first.DefaultTeamSize = 9;
        first.DefaultLocation = "Sân đã chỉnh";
        await store.UpdateAsync(first);

        var second = await store.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Tên từ Zalo",
            DefaultTeamSize = 6,
            DefaultLocation = null
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(9, second.DefaultTeamSize);
        Assert.Equal("Sân đã chỉnh", second.DefaultLocation);
    }

    [Theory]
    [InlineData("17:30", 1050)]
    [InlineData("5:05", 305)]
    [InlineData("23:59", 1439)]
    public void TryParseStartTime_AcceptsExpectedValues(string value, int expectedMinutes)
    {
        Assert.True(ZaloAutoSessionSettingsService.TryParseStartTime(value, out var minutes));
        Assert.Equal(expectedMinutes, minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("17h30")]
    [InlineData("25:00")]
    public void TryParseStartTime_RejectsInvalidValues(string value)
    {
        Assert.False(ZaloAutoSessionSettingsService.TryParseStartTime(value, out _));
    }
}
