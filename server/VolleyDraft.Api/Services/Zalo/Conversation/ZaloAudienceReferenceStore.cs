using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAudienceReferenceTarget(
    string? ZaloUserId,
    string DisplayName,
    string? SegmentKey = null,
    string? SegmentLabel = null);

public sealed record ZaloAudienceReferenceDraft(
    string SourceKind,
    string ContextLabel,
    IReadOnlyList<ZaloAudienceReferenceTarget> Targets);

internal sealed record ZaloAudienceReferenceSnapshot(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string RequesterZaloUserId,
    string SourceKind,
    string ContextLabel,
    string? SourceMessageId,
    IReadOnlyList<ZaloAudienceReferenceTarget> Targets,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Short-lived grounded audience memory for referential follow-ups such as
/// "tag họ" or "nhắn mấy người đó". It stores only identities already resolved by
/// an authoritative feature response; it never infers recipients from free text.
/// </summary>
internal sealed class ZaloAudienceReferenceStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public async Task RememberAsync(
        string connectionId,
        string groupId,
        string requesterZaloUserId,
        string? sourceMessageId,
        ZaloAudienceReferenceDraft audience,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId, 100);
        groupId = Clean(groupId, 100);
        requesterZaloUserId = NormalizeZaloId(requesterZaloUserId);
        if (connectionId.Length == 0 || groupId.Length == 0 || requesterZaloUserId.Length == 0)
            return;

        await EnsureSchemaAsync(cancellationToken);
        var targets = NormalizeTargets(audience.Targets);
        if (targets.Count == 0)
        {
            await ClearAsync(connectionId, groupId, requesterZaloUserId, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + DefaultLifetime;
        var sourceKind = Clean(audience.SourceKind, 80);
        var contextLabel = Clean(audience.ContextLabel, 300);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        var targetsJson = JsonSerializer.Serialize(targets);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloAudienceReferenceSets" (
                "Id", "ZaloConnectionId", "GroupId", "RequesterZaloUserId", "SourceKind",
                "ContextLabel", "SourceMessageId", "TargetsJson", "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES (
                @id, @connectionId, @groupId, @requesterId, @sourceKind,
                @contextLabel, @sourceMessageId, @targetsJson, @expiresAt, @createdAt, @updatedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "RequesterZaloUserId") DO UPDATE SET
                "SourceKind" = excluded."SourceKind",
                "ContextLabel" = excluded."ContextLabel",
                "SourceMessageId" = excluded."SourceMessageId",
                "TargetsJson" = excluded."TargetsJson",
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        Add(command, "@id", Guid.NewGuid().ToString("n"));
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@requesterId", requesterZaloUserId);
        Add(command, "@sourceKind", sourceKind);
        Add(command, "@contextLabel", contextLabel);
        Add(command, "@sourceMessageId", sourceMessageId);
        Add(command, "@targetsJson", targetsJson);
        Add(command, "@expiresAt", expiresAt);
        Add(command, "@createdAt", now);
        Add(command, "@updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ZaloAudienceReferenceSnapshot?> LoadActiveAsync(
        string connectionId,
        string groupId,
        string requesterZaloUserId,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId, 100);
        groupId = Clean(groupId, 100);
        requesterZaloUserId = NormalizeZaloId(requesterZaloUserId);
        if (connectionId.Length == 0 || groupId.Length == 0 || requesterZaloUserId.Length == 0)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ZaloConnectionId", "GroupId", "RequesterZaloUserId", "SourceKind",
                   "ContextLabel", "SourceMessageId", "TargetsJson", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloAudienceReferenceSets"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId
              AND "RequesterZaloUserId" = @requesterId
            LIMIT 1;
            """;
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@requesterId", requesterZaloUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var snapshot = Read(reader);
        await reader.DisposeAsync();
        if (snapshot.ExpiresAt > DateTimeOffset.UtcNow && snapshot.Targets.Count > 0)
            return snapshot;

        await ClearAsync(connectionId, groupId, requesterZaloUserId, cancellationToken);
        return null;
    }

    private async Task ClearAsync(
        string connectionId,
        string groupId,
        string requesterZaloUserId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM "ZaloAudienceReferenceSets"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId
              AND "RequesterZaloUserId" = @requesterId;
            """;
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@requesterId", requesterZaloUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!isPostgres && !isSqlite) return;

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            var sql = isPostgres
                ? """
                    CREATE TABLE IF NOT EXISTS "ZaloAudienceReferenceSets" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloAudienceReferenceSets" PRIMARY KEY,
                        "ZaloConnectionId" TEXT NOT NULL,
                        "GroupId" TEXT NOT NULL,
                        "RequesterZaloUserId" TEXT NOT NULL,
                        "SourceKind" TEXT NOT NULL,
                        "ContextLabel" TEXT NOT NULL,
                        "SourceMessageId" TEXT NULL,
                        "TargetsJson" TEXT NOT NULL,
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "UX_ZaloAudienceReferenceSets_Scope"
                            UNIQUE ("ZaloConnectionId", "GroupId", "RequesterZaloUserId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloAudienceReferenceSets_ExpiresAt"
                    ON "ZaloAudienceReferenceSets" ("ExpiresAt");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloAudienceReferenceSets" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloAudienceReferenceSets" PRIMARY KEY,
                        "ZaloConnectionId" TEXT NOT NULL,
                        "GroupId" TEXT NOT NULL,
                        "RequesterZaloUserId" TEXT NOT NULL,
                        "SourceKind" TEXT NOT NULL,
                        "ContextLabel" TEXT NOT NULL,
                        "SourceMessageId" TEXT NULL,
                        "TargetsJson" TEXT NOT NULL,
                        "ExpiresAt" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "UX_ZaloAudienceReferenceSets_Scope"
                            UNIQUE ("ZaloConnectionId", "GroupId", "RequesterZaloUserId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloAudienceReferenceSets_ExpiresAt"
                    ON "ZaloAudienceReferenceSets" ("ExpiresAt");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static ZaloAudienceReferenceSnapshot Read(DbDataReader reader)
    {
        var targetsJson = reader.GetString(7);
        IReadOnlyList<ZaloAudienceReferenceTarget> targets;
        try
        {
            targets = NormalizeTargets(
                JsonSerializer.Deserialize<List<ZaloAudienceReferenceTarget>>(targetsJson) ?? []);
        }
        catch (JsonException)
        {
            targets = [];
        }

        return new ZaloAudienceReferenceSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            targets,
            Timestamp(reader.GetValue(8)),
            Timestamp(reader.GetValue(9)),
            Timestamp(reader.GetValue(10)));
    }

    private static IReadOnlyList<ZaloAudienceReferenceTarget> NormalizeTargets(
        IEnumerable<ZaloAudienceReferenceTarget> targets)
    {
        var result = new List<ZaloAudienceReferenceTarget>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets.Take(50))
        {
            var userId = CleanOptional(target.ZaloUserId, 100);
            var displayName = Clean(target.DisplayName, 120).TrimStart('@');
            if (displayName.Length == 0) continue;
            if (userId is not null && !usedIds.Add(userId)) continue;
            result.Add(new ZaloAudienceReferenceTarget(
                userId,
                displayName,
                CleanOptional(target.SegmentKey, 100),
                CleanOptional(target.SegmentLabel, 120)));
        }
        return result.Take(30).ToArray();
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }

    private static string NormalizeZaloId(string? value)
    {
        var text = Clean(value, 100);
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static DateTimeOffset Timestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static async Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
