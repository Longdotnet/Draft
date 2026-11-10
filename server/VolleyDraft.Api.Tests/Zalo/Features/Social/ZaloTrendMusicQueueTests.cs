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

        Assert.True(await store.EnqueueAsync(target, "2026-11-10", key, candidates, CancellationToken.None));
        Assert.False(await store.EnqueueAsync(target, "2026-11-10", key, "[{\"id\":\"newer-override\"}]", CancellationToken.None));

        var first = await store.TryClaimQueueAsync(now, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("account-1", first.AccountId);
        Assert.Equal("group-1", first.GroupId);
        Assert.Equal("connection-1", first.ConnectionId);
        Assert.Equal("2026-11-10", first.LocalDate);
        Assert.Equal(key, first.IdempotencyKey);
        Assert.Equal(candidates, first.CandidatesJson);
        Assert.False(string.IsNullOrWhiteSpace(first.ClaimToken));
        Assert.True(await store.IsCurrentQueueClaimAsync(key, first.ClaimToken, CancellationToken.None));
        Assert.False(await store.IsCurrentQueueClaimAsync(key, "wrong-token", CancellationToken.None));

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
        Assert.NotEqual(first.ClaimToken, staleRetry.ClaimToken);
        Assert.False(await store.IsCurrentQueueClaimAsync(key, first.ClaimToken, CancellationToken.None));
        Assert.True(await store.IsCurrentQueueClaimAsync(key, staleRetry.ClaimToken, CancellationToken.None));

        await store.MarkQueueSentAsync(key, first.ClaimToken, CancellationToken.None);
        Assert.NotNull(await store.TryClaimQueueAsync(
            now.AddMinutes(22),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));

        var current = await store.TryClaimQueueAsync(
            now.AddMinutes(33),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.NotNull(current);
        await store.MarkQueueSentAsync(key, current.ClaimToken, CancellationToken.None);
        Assert.Null(await store.TryClaimQueueAsync(
            now.AddDays(1),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));
    }

    [Fact]
    public async Task Failed_requested_song_is_retryable_once_then_terminal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicJobStore(db);
        var target = new ZaloProactiveTarget("connection-1", "group-1", "account-1");
        var now = DateTimeOffset.UtcNow;
        const string key = "trend-music:request:connection-1:group-1:message-1";

        Assert.True(await store.EnqueueAsync(
            target,
            now.ToString("yyyy-MM-dd"),
            key,
            "{\"candidates\":[],\"songQuery\":\"Gói con tim làm quà\"}",
            CancellationToken.None));

        var first = await store.TryClaimQueueAsync(now, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.NotNull(first);
        await store.MarkQueueFailedAsync(key, first.ClaimToken, CancellationToken.None);

        var retry = await store.TryClaimQueueAsync(now.AddSeconds(1), TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(key, retry.IdempotencyKey);
        Assert.NotEqual(first.ClaimToken, retry.ClaimToken);
        await store.MarkQueueFailedAsync(key, retry.ClaimToken, CancellationToken.None);

        Assert.Null(await store.TryClaimQueueAsync(
            now.AddSeconds(2),
            TimeSpan.FromMinutes(10),
            CancellationToken.None));
    }

    [Fact]
    public async Task Daily_job_store_allows_morning_and_night_on_same_service_date()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicJobStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryBeginAsync(
            "connection-1", "group-1", "2026-11-10|morning", now,
            TimeSpan.FromMinutes(35), CancellationToken.None));
        await store.MarkSentAsync(
            "connection-1", "group-1", "2026-11-10|morning", "morning-msg", CancellationToken.None);

        Assert.True(await store.TryBeginAsync(
            "connection-1", "group-1", "2026-11-10|night", now.AddHours(12),
            TimeSpan.FromMinutes(35), CancellationToken.None));
        Assert.False(await store.TryBeginAsync(
            "connection-1", "group-1", "2026-11-10|morning", now.AddHours(12),
            TimeSpan.FromMinutes(35), CancellationToken.None));
    }

    [Fact]
    public async Task One_shot_override_is_period_scoped_and_consumed_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicOverrideStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var value = new ZaloTrendMusicOneShotOverride(
            "night", false, "Ngủ ngon cả nhà", "Kèo tối", "Zyro VC",
            "https://example.test/image.jpg", false);

        await store.UpsertAsync(
            "connection-1", "group-1", value, now.AddHours(24), CancellationToken.None);

        var loaded = await store.GetAsync(
            "connection-1", "group-1", "night", now, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(value.Period, loaded.Period);
        Assert.Equal(value.GreetingDisabled, loaded.GreetingDisabled);
        Assert.Equal(value.GreetingText, loaded.GreetingText);
        Assert.Equal(value.DisplayTitle, loaded.DisplayTitle);
        Assert.Equal(value.DisplayArtist, loaded.DisplayArtist);
        Assert.Equal(value.ArtworkUrl, loaded.ArtworkUrl);
        Assert.Equal(value.ShowLogo, loaded.ShowLogo);
        Assert.False(string.IsNullOrWhiteSpace(loaded.Revision));
        Assert.Null(await store.GetAsync(
            "connection-1", "group-1", "morning", now, CancellationToken.None));

        await store.ConsumeAsync("connection-1", "group-1", "night", CancellationToken.None);
        Assert.Null(await store.GetAsync(
            "connection-1", "group-1", "night", now, CancellationToken.None));
    }

    [Fact]
    public async Task One_shot_revision_consume_does_not_delete_newer_override()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicOverrideStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(
            "connection-1", "group-1",
            new ZaloTrendMusicOneShotOverride("night", false, "Bản cũ", null, null, null, null),
            now.AddDays(8), CancellationToken.None);
        var first = await store.GetAsync("connection-1", "group-1", "night", now, CancellationToken.None);
        Assert.NotNull(first);

        await Task.Delay(2);
        await store.UpsertAsync(
            "connection-1", "group-1",
            new ZaloTrendMusicOneShotOverride("night", false, "Bản mới", null, null, null, null),
            now.AddDays(8), CancellationToken.None);
        await store.ConsumeIfUnchangedAsync(
            "connection-1", "group-1", "night", first.Revision, CancellationToken.None);

        var remaining = await store.GetAsync("connection-1", "group-1", "night", now, CancellationToken.None);
        Assert.NotNull(remaining);
        Assert.Equal("Bản mới", remaining.GreetingText);
    }

    [Fact]
    public async Task One_shot_atomic_patches_preserve_fields_from_separate_commands()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicOverrideStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        await store.PatchAsync(
            "connection-1", "group-1", "night",
            null, null, "Kèo tối", null, false, null, null,
            now.AddDays(8), CancellationToken.None);
        var first = await store.GetAsync("connection-1", "group-1", "night", now, CancellationToken.None);
        Assert.NotNull(first);

        await Task.Delay(2);
        await store.PatchAsync(
            "connection-1", "group-1", "night",
            null, null, null, "Zyro VC", false, null, null,
            now.AddDays(8), CancellationToken.None);

        await store.ConsumeIfUnchangedAsync(
            "connection-1", "group-1", "night", first.Revision, CancellationToken.None);
        var final = await store.GetAsync("connection-1", "group-1", "night", now, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal("Kèo tối", final.DisplayTitle);
        Assert.Equal("Zyro VC", final.DisplayArtist);
    }
}
