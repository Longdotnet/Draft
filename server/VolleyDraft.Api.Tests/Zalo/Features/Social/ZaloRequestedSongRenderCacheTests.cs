using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloRequestedSongRenderCacheTests
{
    [Fact]
    public void Cache_key_is_accent_case_punctuation_insensitive_and_render_variant_scoped()
    {
        var canonical = ZaloTrendMusicService.RequestedSongCacheKey(
            "Gói con tim làm quà",
            showLogo: true);
        var variant = ZaloTrendMusicService.RequestedSongCacheKey(
            "  GOI-con TIM, lam QUA!!! ",
            showLogo: true);

        Assert.Equal(canonical, variant);
        Assert.NotEqual(
            canonical,
            ZaloTrendMusicService.RequestedSongCacheKey(
                "Gói con tim làm quà",
                showLogo: false));
        Assert.StartsWith("v4:logo1:", canonical);
    }

    [Fact]
    public async Task Cache_persists_across_store_instances_and_touch_updates_lru_metadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        var now = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero);
        var entry = Entry(
            "v4:logo1:key",
            "Gói con tim làm quà",
            "itunes-6796615117",
            now,
            now.AddDays(30),
            [1, 2, 3, 4],
            [5, 6]);

        await using (var db = new VolleyDraftDbContext(options))
        {
            var store = new ZaloTrendMusicRenderCacheStore(db);
            await store.UpsertAsync(entry, now, 24, 96L * 1024 * 1024, CancellationToken.None);
        }

        await using (var db = new VolleyDraftDbContext(options))
        {
            var store = new ZaloTrendMusicRenderCacheStore(db);
            var cached = await store.TryGetAsync(entry.CacheKey, now.AddMinutes(1), CancellationToken.None);
            Assert.NotNull(cached);
            Assert.Equal(entry.CandidateId, cached.CandidateId);
            Assert.Equal(entry.VideoData, cached.VideoData);
            Assert.Equal(entry.ThumbnailData, cached.ThumbnailData);
            Assert.Equal(0, cached.UseCount);

            var touchedAt = now.AddMinutes(2);
            await store.TouchAsync(entry.CacheKey, touchedAt, CancellationToken.None);
            cached = await store.TryGetAsync(entry.CacheKey, touchedAt, CancellationToken.None);
            Assert.NotNull(cached);
            Assert.Equal(1, cached.UseCount);
            Assert.Equal(touchedAt, cached.LastUsedAt);
        }
    }

    [Fact]
    public async Task Cache_cleanup_removes_expired_and_oldest_entries_by_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicRenderCacheStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(
            Entry("expired", "Expired", "itunes-expired", now.AddDays(-31), now.AddSeconds(-1), [1], [2]),
            now,
            2,
            1024,
            CancellationToken.None);
        Assert.Null(await store.TryGetAsync("expired", now, CancellationToken.None));

        var oldest = Entry("oldest", "Oldest", "itunes-old", now, now.AddDays(30), [1, 2], [3]);
        var middle = Entry("middle", "Middle", "itunes-mid", now.AddMinutes(1), now.AddDays(30), [4, 5], [6]);
        var newest = Entry("newest", "Newest", "itunes-new", now.AddMinutes(2), now.AddDays(30), [7, 8], [9]);
        await store.UpsertAsync(oldest, now, 2, 1024, CancellationToken.None);
        await store.UpsertAsync(middle, now.AddMinutes(1), 2, 1024, CancellationToken.None);
        await store.UpsertAsync(newest, now.AddMinutes(2), 2, 1024, CancellationToken.None);

        Assert.Null(await store.TryGetAsync("oldest", now.AddMinutes(3), CancellationToken.None));
        Assert.NotNull(await store.TryGetAsync("middle", now.AddMinutes(3), CancellationToken.None));
        Assert.NotNull(await store.TryGetAsync("newest", now.AddMinutes(3), CancellationToken.None));
    }

    [Fact]
    public async Task Cache_cleanup_respects_total_byte_budget()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloTrendMusicRenderCacheStore(db);
        var now = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(
            Entry("older", "Older", "itunes-1", now, now.AddDays(30), [1, 2, 3], [4]),
            now,
            10,
            5,
            CancellationToken.None);
        await store.UpsertAsync(
            Entry("newer", "Newer", "itunes-2", now.AddMinutes(1), now.AddDays(30), [5, 6, 7], [8]),
            now.AddMinutes(1),
            10,
            5,
            CancellationToken.None);

        Assert.Null(await store.TryGetAsync("older", now.AddMinutes(2), CancellationToken.None));
        Assert.NotNull(await store.TryGetAsync("newer", now.AddMinutes(2), CancellationToken.None));
    }

    private static ZaloTrendMusicRenderCacheEntry Entry(
        string key,
        string query,
        string candidateId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        byte[] video,
        byte[] thumbnail) =>
        new(
            key,
            query,
            candidateId,
            query,
            "Nguyễn Hùng",
            1,
            video,
            thumbnail,
            75_000,
            1080,
            1920,
            null,
            null,
            null,
            createdAt,
            createdAt,
            expiresAt,
            0);
}
