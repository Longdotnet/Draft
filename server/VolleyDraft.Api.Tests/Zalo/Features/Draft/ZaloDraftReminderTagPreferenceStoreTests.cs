using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftReminderTagPreferenceStoreTests
{
    [Fact]
    public async Task Preferences_AreExplicitAndPersistDisabledState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloDraftReminderTagPreferenceStore(db);

        Assert.Empty(await store.GetForTrackedGroupAsync("tracked-a"));

        await store.SetAsync(
            "tracked-a",
            "connection-a",
            "group-a",
            "admin-b_0",
            "Phó B",
            true,
            "web-admin");

        var enabled = Assert.Single(await store.GetForGroupAsync("connection-a", "group-a"));
        Assert.Equal("admin-b", enabled.ZaloUserId);
        Assert.True(enabled.Enabled);

        await store.SetAsync(
            "tracked-a",
            "connection-a",
            "group-a",
            "admin-b",
            "Phó B",
            false,
            "web-admin");

        var disabled = Assert.Single(await store.GetForTrackedGroupAsync("tracked-a"));
        Assert.False(disabled.Enabled);
        Assert.Equal("Phó B", disabled.DisplayName);
    }

    [Fact]
    public async Task Preferences_AreScopedByConnectionAndGroup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloDraftReminderTagPreferenceStore(db);

        await store.SetAsync("tracked-a", "connection-a", "group-a", "leader-a", "Leader A", true, "web-admin");
        await store.SetAsync("tracked-b", "connection-b", "group-a", "leader-b", "Leader B", true, "web-admin");

        var first = Assert.Single(await store.GetForGroupAsync("connection-a", "group-a"));
        var second = Assert.Single(await store.GetForGroupAsync("connection-b", "group-a"));
        Assert.Equal("leader-a", first.ZaloUserId);
        Assert.Equal("leader-b", second.ZaloUserId);
    }
}
