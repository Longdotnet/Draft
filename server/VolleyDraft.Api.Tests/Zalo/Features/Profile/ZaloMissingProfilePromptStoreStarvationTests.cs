using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMissingProfilePromptStoreStarvationTests
{
    [Fact]
    public async Task Global_active_batch_filters_expired_rows_before_applying_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloMissingProfilePromptStore(fixture.Db);
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 110; index++)
        {
            await store.UpsertAsync(
                "connection-1",
                "group-1",
                $"expired-session-{index}",
                $"expired-player-{index}",
                $"expired-uid-{index}",
                $"Expired {index}",
                true,
                true,
                true,
                $"expired-message-{index}",
                now.AddHours(-2),
                now.AddHours(-1));
        }

        var live = await store.UpsertAsync(
            "connection-1",
            "group-1",
            "live-session",
            "live-player",
            "live-uid",
            "Live",
            true,
            true,
            true,
            "live-message",
            now.AddMinutes(-1),
            now.AddMinutes(30));

        var active = await store.GetActiveAsync(now, 1);

        var prompt = Assert.Single(active);
        Assert.Equal(live.Id, prompt.Id);
    }

    [Fact]
    public async Task Sender_lookup_is_not_starved_by_unrelated_active_prompts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloMissingProfilePromptStore(fixture.Db);
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 120; index++)
        {
            await store.UpsertAsync(
                "connection-1",
                "group-1",
                $"other-session-{index}",
                $"other-player-{index}",
                $"other-uid-{index}",
                $"Other {index}",
                true,
                true,
                true,
                $"other-message-{index}",
                now.AddMinutes(-5),
                now.AddMinutes(30));
        }

        var target = await store.UpsertAsync(
            "connection-1",
            "group-1",
            "target-session",
            "target-player",
            "target-uid",
            "Target",
            true,
            false,
            true,
            "target-message",
            now.AddMinutes(-1),
            now.AddMinutes(30));

        var active = await store.GetActiveForSenderAsync(
            now,
            "connection-1",
            "group-1",
            "target-uid");

        var prompt = Assert.Single(active);
        Assert.Equal(target.Id, prompt.Id);
        Assert.Equal("target-session", prompt.SessionId);
    }

    [Fact]
    public async Task Sender_lookup_excludes_expired_and_wrong_scope_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloMissingProfilePromptStore(fixture.Db);
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            "connection-1", "group-1", "expired", "player-expired", "target-uid", "Target",
            true, true, true, "expired-message", now.AddHours(-2), now.AddHours(-1));
        await store.UpsertAsync(
            "connection-1", "group-2", "wrong-group", "player-group", "target-uid", "Target",
            true, true, true, "wrong-group-message", now.AddMinutes(-1), now.AddMinutes(30));
        await store.UpsertAsync(
            "connection-2", "group-1", "wrong-connection", "player-connection", "target-uid", "Target",
            true, true, true, "wrong-connection-message", now.AddMinutes(-1), now.AddMinutes(30));
        var expected = await store.UpsertAsync(
            "connection-1", "group-1", "expected", "player-expected", "target-uid", "Target",
            true, false, true, "expected-message", now.AddMinutes(-1), now.AddMinutes(30));

        var active = await store.GetActiveForSenderAsync(
            now,
            "connection-1",
            "group-1",
            "target-uid");

        var prompt = Assert.Single(active);
        Assert.Equal(expected.Id, prompt.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
