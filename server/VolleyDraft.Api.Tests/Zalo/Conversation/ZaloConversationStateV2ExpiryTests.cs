using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationStateV2ExpiryTests
{
    [Fact]
    public async Task Cancel_removes_active_state_from_routing_without_deleting_history_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloConversationStateV2Store(db);

        await store.SaveActiveAsync(
            "g1", "u1", "AutoDraftConfirm", "{}", "[]", "[]", "m1", "m1",
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(1, await store.CancelAsync("g1", "u1"));

        Assert.Null(await store.LoadActiveAsync("g1", "u1"));
    }
}
