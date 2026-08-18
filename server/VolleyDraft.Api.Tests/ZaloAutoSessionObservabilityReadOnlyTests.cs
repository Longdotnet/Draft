using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionObservabilityReadOnlyTests
{
    [Fact]
    public async Task GetOverbookStates_WhenTableIsMissing_ReturnsEmptyWithoutCreatingSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var settingsStore = new ZaloAutoSessionSettingsStore(db);
        var baseStore = new ZaloAutoSessionStore(db);
        var tracked = await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng UTE"
        });
        await baseStore.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            tracked.Id,
            "poll-1",
            "option-t6",
            "session-1",
            DateTimeOffset.UtcNow));

        var auditStore = new ZaloAutoSessionObservabilityStore(db);
        var states = await auditStore.GetOverbookStatesAsync("admin-a", tracked.Id);

        Assert.Empty(states);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ZaloOverbookStates';";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }
}
