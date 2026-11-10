using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloTrendMusicRenderCacheStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task<ZaloTrendMusicRenderCacheEntry?> TryGetAsync(
        string cacheKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "CacheKey", "SongQuery", "CandidateId", "Title", "Artist", "Rank",
                   "VideoData", "ThumbnailData", "DurationMs", "Width", "Height",
                   "SourceUrl", "LicenseUrl", "LicenseLabel",
                   "CreatedAt", "LastUsedAt", "ExpiresAt", "HitCount"
            FROM "ZaloRequestedSongRenderCache"
            WHERE "CacheKey" = @CacheKey
              AND "ExpiresAt" > @Now
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@CacheKey", cacheKey.Trim());
        Add(command, "@Now", DbTime(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloTrendMusicRenderCacheEntry(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetFieldValue<byte[]>(6),
            reader.GetFieldValue<byte[]>(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            ParseTime(reader.GetString(14)),
            ParseTime(reader.GetString(15)),
            ParseTime(reader.GetString(16)),
            reader.GetInt32(17));
    }

    public async Task UpsertAsync(
        ZaloTrendMusicRenderCacheEntry entry,
        DateTimeOffset now,
        int maxEntries,
        long maxTotalBytes,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloRequestedSongRenderCache" (
                "CacheKey", "SongQuery", "CandidateId", "Title", "Artist", "Rank",
                "VideoData", "ThumbnailData", "VideoSize", "ThumbnailSize",
                "DurationMs", "Width", "Height",
                "SourceUrl", "LicenseUrl", "LicenseLabel",
                "CreatedAt", "LastUsedAt", "ExpiresAt", "HitCount")
            VALUES (
                @CacheKey, @SongQuery, @CandidateId, @Title, @Artist, @Rank,
                @VideoData, @ThumbnailData, @VideoSize, @ThumbnailSize,
                @DurationMs, @Width, @Height,
                @SourceUrl, @LicenseUrl, @LicenseLabel,
                @CreatedAt, @LastUsedAt, @ExpiresAt, @HitCount)
            ON CONFLICT ("CacheKey") DO UPDATE SET
                "SongQuery" = excluded."SongQuery",
                "CandidateId" = excluded."CandidateId",
                "Title" = excluded."Title",
                "Artist" = excluded."Artist",
                "Rank" = excluded."Rank",
                "VideoData" = excluded."VideoData",
                "ThumbnailData" = excluded."ThumbnailData",
                "VideoSize" = excluded."VideoSize",
                "ThumbnailSize" = excluded."ThumbnailSize",
                "DurationMs" = excluded."DurationMs",
                "Width" = excluded."Width",
                "Height" = excluded."Height",
                "SourceUrl" = excluded."SourceUrl",
                "LicenseUrl" = excluded."LicenseUrl",
                "LicenseLabel" = excluded."LicenseLabel",
                "CreatedAt" = excluded."CreatedAt",
                "LastUsedAt" = excluded."LastUsedAt",
                "ExpiresAt" = excluded."ExpiresAt",
                "HitCount" = excluded."HitCount";
            """;
        await using (var command = await CreateCommandAsync(sql, cancellationToken))
        {
            Add(command, "@CacheKey", entry.CacheKey.Trim());
            Add(command, "@SongQuery", entry.SongQuery.Trim());
            Add(command, "@CandidateId", entry.CandidateId.Trim());
            Add(command, "@Title", entry.Title.Trim());
            Add(command, "@Artist", entry.Artist.Trim());
            Add(command, "@Rank", entry.Rank);
            Add(command, "@VideoData", entry.VideoData);
            Add(command, "@ThumbnailData", entry.ThumbnailData);
            Add(command, "@VideoSize", entry.VideoData.LongLength);
            Add(command, "@ThumbnailSize", entry.ThumbnailData.LongLength);
            Add(command, "@DurationMs", entry.DurationMs);
            Add(command, "@Width", entry.Width);
            Add(command, "@Height", entry.Height);
            Add(command, "@SourceUrl", entry.SourceUrl);
            Add(command, "@LicenseUrl", entry.LicenseUrl);
            Add(command, "@LicenseLabel", entry.LicenseLabel);
            Add(command, "@CreatedAt", DbTime(entry.CreatedAt));
            Add(command, "@LastUsedAt", DbTime(entry.LastUsedAt));
            Add(command, "@ExpiresAt", DbTime(entry.ExpiresAt));
            Add(command, "@HitCount", entry.UseCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await CleanupAsync(now, maxEntries, maxTotalBytes, cancellationToken);
    }

    public async Task TouchAsync(
        string cacheKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloRequestedSongRenderCache"
            SET "LastUsedAt" = @Now,
                "HitCount" = "HitCount" + 1
            WHERE "CacheKey" = @CacheKey
              AND "ExpiresAt" > @Now;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@CacheKey", cacheKey.Trim());
        Add(command, "@Now", DbTime(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task CleanupAsync(
        DateTimeOffset now,
        int maxEntries,
        long maxTotalBytes,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using (var expire = await CreateCommandAsync(
                         """
                         DELETE FROM "ZaloRequestedSongRenderCache"
                         WHERE "ExpiresAt" <= @Now;
                         """,
                         cancellationToken))
        {
            Add(expire, "@Now", DbTime(now));
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        var rows = new List<(string CacheKey, long Size)>();
        await using (var command = await CreateCommandAsync(
                         """
                         SELECT "CacheKey", "VideoSize", "ThumbnailSize"
                         FROM "ZaloRequestedSongRenderCache"
                         ORDER BY "LastUsedAt" DESC, "CreatedAt" DESC, "CacheKey";
                         """,
                         cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((
                    reader.GetString(0),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) +
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }

        var keepCount = 0;
        long keptBytes = 0;
        var remove = new List<string>();
        foreach (var row in rows)
        {
            var fitsCount = keepCount < Math.Max(1, maxEntries);
            var fitsBytes = keptBytes + row.Size <= Math.Max(1L, maxTotalBytes);
            if (fitsCount && fitsBytes)
            {
                keepCount++;
                keptBytes += row.Size;
            }
            else
            {
                remove.Add(row.CacheKey);
            }
        }

        foreach (var key in remove)
        {
            await using var command = await CreateCommandAsync(
                """
                DELETE FROM "ZaloRequestedSongRenderCache"
                WHERE "CacheKey" = @CacheKey;
                """,
                cancellationToken);
            Add(command, "@CacheKey", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        var binaryType = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? "bytea"
            : "BLOB";
        var sql = $"""
            CREATE TABLE IF NOT EXISTS "ZaloRequestedSongRenderCache" (
                "CacheKey" TEXT NOT NULL PRIMARY KEY,
                "SongQuery" TEXT NOT NULL,
                "CandidateId" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Artist" TEXT NOT NULL,
                "Rank" INTEGER NOT NULL,
                "VideoData" {binaryType} NOT NULL,
                "ThumbnailData" {binaryType} NOT NULL,
                "VideoSize" BIGINT NOT NULL,
                "ThumbnailSize" BIGINT NOT NULL,
                "DurationMs" INTEGER NOT NULL,
                "Width" INTEGER NOT NULL,
                "Height" INTEGER NOT NULL,
                "SourceUrl" TEXT NULL,
                "LicenseUrl" TEXT NULL,
                "LicenseLabel" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "LastUsedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "HitCount" INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloRequestedSongRenderCache_ExpiresAt"
                ON "ZaloRequestedSongRenderCache" ("ExpiresAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloRequestedSongRenderCache_LastUsedAt"
                ON "ZaloRequestedSongRenderCache" ("LastUsedAt");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    private async Task<DbCommand> CreateCommandAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string DbTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
