using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloTrendMusicQueueTests
{
    [Fact]
    public async Task Queue_claim_is_durable_stale_retryable_and_sent_fenced()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicJobStore(db);
        var target = new ZaloProactiveTarget("connection-1", "group-1", "account-1");
        var now = new DateTimeOffset(2026, 11, 10, 4, 30, 0, TimeSpan.Zero);
        const string key = "trend-music:test:connection-1:group-1:message-1";
        const string candidates = "[{\"id\":\"candidate-1\"}]";

        await store.EnqueueAsync(target, "2026-11-10", key, candidates, CancellationToken.None);
        await store.EnqueueAsync(target, "2026-11-10", key, candidates, CancellationToken.None);

        var first = await store.TryClaimQueueAsync(now, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("account-1", first.AccountId);
        Assert.Equal("group-1", first.GroupId);
        Assert.Equal("connection-1", first.ConnectionId);
        Assert.Equal("2026-11-10", first.LocalDate);
        Assert.Equal(key, first.IdempotencyKey);
        Assert.Equal(candidates, first.CandidatesJson);

        Assert.Null(await store.TryClaimQueueAsync(
            now.AddMinutes(9),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));

        var staleRetry = await store.TryClaimQueueAsync(
            now.AddMinutes(11),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.NotNull(staleRetry);
        Assert.Equal(key, staleRetry.IdempotencyKey);

        await store.MarkQueueSentAsync(key, CancellationToken.None);
        Assert.Null(await store.TryClaimQueueAsync(
            now.AddDays(1),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));
    }
}
