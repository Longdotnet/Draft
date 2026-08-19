using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionTrustedOrganizerStoreTests
{
    [Fact]
    public async Task TrustedBackup_MustBeExplicitlyEnabledAndCanBeDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionTrustedOrganizerStore(db);

        Assert.Empty(await store.GetEnabledIdsAsync("group-a"));

        await store.SetAsync("group-a", "admin-b_0", "Phó B", true, "web-admin");
        var enabled = await store.GetEnabledIdsAsync("group-a");
        Assert.Contains("admin-b", enabled);
        Assert.DoesNotContain("admin-b_0", enabled);

        await store.SetAsync("group-a", "admin-b", "Phó B", false, "web-admin");
        Assert.Empty(await store.GetEnabledIdsAsync("group-a"));

        var rows = await store.GetAsync("group-a");
        var row = Assert.Single(rows);
        Assert.False(row.Enabled);
        Assert.Equal("Phó B", row.DisplayName);
    }

    [Fact]
    public async Task TrustedBackups_AreScopedPerTrackedGroup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionTrustedOrganizerStore(db);

        await store.SetAsync("group-a", "admin-a", "Admin A", true, "web-admin");
        await store.SetAsync("group-b", "admin-b", "Admin B", true, "web-admin");

        Assert.Equal(new[] { "admin-a" }, (await store.GetEnabledIdsAsync("group-a")).OrderBy(item => item));
        Assert.Equal(new[] { "admin-b" }, (await store.GetEnabledIdsAsync("group-b")).OrderBy(item => item));
    }
}
