using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloUserConceptSnapshot(
    string Id,
    string GroupId,
    string SubjectZaloUserId,
    string ConceptType,
    string Key,
    string ValueJson,
    string Scope,
    double Confidence,
    string? SourceMessageId,
    string CreatedBySenderId,
    string CreatedBySenderName,
    string Status,
    string? SupersedesConceptId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persists explicit user concepts independently from a Zalo login connection.
/// GroupId + Zalo user identity is the stable scope so re-authentication does not
/// erase memory. The store uses provider-neutral ADO commands and creates its
/// additive table lazily for safe rollout on existing SQLite/Postgres databases.
/// </summary>
public sealed class ZaloUserConceptStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly HashSet<string> ReadySchemas = new(StringComparer.Ordinal);

    public async Task<ZaloUserConceptSnapshot> RememberAsync(
        string groupId,
        ZaloAiSender sender,
        ZaloUserConceptDraft draft,
        string? sourceMessageId = null,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        var subjectId = Clean(sender.Id, 100);
        var senderName = Clean(sender.Name, 160);
        var type = Clean(draft.ConceptType, 40);
        var key = Clean(draft.Key, 120);
        var valueJson = Clean(draft.ValueJson, 4000);
        var scope = Clean(draft.Scope, 40);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        if (groupId.Length == 0 || subjectId.Length == 0 || type.Length == 0 || key.Length == 0 || valueJson.Length == 0)
            throw new ArgumentException("User concept scope, subject, type, key and value are required.");

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existingId = await FindActiveIdAsync(connection, transaction, groupId, subjectId, type, key, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (existingId is not null)
        {
            await using var supersede = connection.CreateCommand();
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE "ZaloUserConcepts"
                SET "Status" = 'Superseded', "UpdatedAt" = @updatedAt
                WHERE "Id" = @id AND "Status" = 'Active';
                """;
            Add(supersede, "@updatedAt", now);
            Add(supersede, "@id", existingId);
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        var id = Guid.NewGuid().ToString("n");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO "ZaloUserConcepts" (
                    "Id", "GroupId", "SubjectZaloUserId", "ConceptType", "ConceptKey", "ValueJson",
                    "Scope", "Confidence", "SourceMessageId", "CreatedBySenderId", "CreatedBySenderName",
                    "Status", "SupersedesConceptId", "ExpiresAt", "LastConfirmedAt", "CreatedAt", "UpdatedAt")
                VALUES (
                    @id, @groupId, @subjectId, @type, @key, @valueJson,
                    @scope, @confidence, @sourceMessageId, @createdBy, @createdByName,
                    'Active', @supersedesId, @expiresAt, @confirmedAt, @createdAt, @updatedAt);
                """;
            Add(insert, "@id", id);
            Add(insert, "@groupId", groupId);
            Add(insert, "@subjectId", subjectId);
            Add(insert, "@type", type);
            Add(insert, "@key", key);
            Add(insert, "@valueJson", valueJson);
            Add(insert, "@scope", scope);
            Add(insert, "@confidence", Math.Clamp(draft.Confidence, 0, 1));
            Add(insert, "@sourceMessageId", sourceMessageId);
            Add(insert, "@createdBy", subjectId);
            Add(insert, "@createdByName", senderName);
            Add(insert, "@supersedesId", existingId);
            Add(insert, "@expiresAt", draft.ExpiresAt);
            Add(insert, "@confirmedAt", now);
            Add(insert, "@createdAt", now);
            Add(insert, "@updatedAt", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ZaloUserConceptSnapshot(
            id,
            groupId,
            subjectId,
            type,
            key,
            valueJson,
            scope,
            Math.Clamp(draft.Confidence, 0, 1),
            sourceMessageId,
            subjectId,
            senderName,
            "Active",
            existingId,
            draft.ExpiresAt,
            now,
            now,
            now);
    }

    public async Task<IReadOnlyList<ZaloUserConceptSnapshot>> LoadActiveAsync(
        string groupId,
        string subjectZaloUserId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        subjectZaloUserId = Clean(subjectZaloUserId, 100);
        if (groupId.Length == 0 || subjectZaloUserId.Length == 0) return [];
        limit = Math.Clamp(limit, 1, 50);
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "GroupId", "SubjectZaloUserId", "ConceptType", "ConceptKey", "ValueJson",
                   "Scope", "Confidence", "SourceMessageId", "CreatedBySenderId", "CreatedBySenderName",
                   "Status", "SupersedesConceptId", "ExpiresAt", "LastConfirmedAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloUserConcepts"
            WHERE "GroupId" = @groupId
              AND "SubjectZaloUserId" = @subjectId
              AND "Status" = 'Active'
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > @now)
            ORDER BY "UpdatedAt" DESC
            LIMIT @limit;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@subjectId", subjectZaloUserId);
        Add(command, "@now", DateTimeOffset.UtcNow);
        Add(command, "@limit", limit);

        var concepts = new List<ZaloUserConceptSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) concepts.Add(Read(reader));
        return concepts;
    }

    public async Task<int> DisableAsync(
        string groupId,
        string subjectZaloUserId,
        string conceptKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloUserConcepts"
            SET "Status" = 'Disabled', "UpdatedAt" = @now
            WHERE "GroupId" = @groupId
              AND "SubjectZaloUserId" = @subjectId
              AND "ConceptKey" = @key
              AND "Status" = 'Active';
            """;
        Add(command, "@now", DateTimeOffset.UtcNow);
        Add(command, "@groupId", Clean(groupId, 100));
        Add(command, "@subjectId", Clean(subjectZaloUserId, 100));
        Add(command, "@key", Clean(conceptKey, 120));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var connection = db.Database.GetDbConnection();
        var connectionIdentity = connection.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            ? RuntimeHelpers.GetHashCode(connection).ToString()
            : $"{connection.DataSource}|{connection.Database}";
        var schemaKey = provider + "|" + connectionIdentity;
        lock (ReadySchemas)
        {
            if (ReadySchemas.Contains(schemaKey)) return;
        }

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            lock (ReadySchemas)
            {
                if (ReadySchemas.Contains(schemaKey)) return;
            }

            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ZaloUserConcepts" (
                        "Id" text NOT NULL CONSTRAINT "PK_ZaloUserConcepts" PRIMARY KEY,
                        "GroupId" text NOT NULL,
                        "SubjectZaloUserId" text NOT NULL,
                        "ConceptType" text NOT NULL,
                        "ConceptKey" text NOT NULL,
                        "ValueJson" text NOT NULL,
                        "Scope" text NOT NULL DEFAULT 'User',
                        "Confidence" double precision NOT NULL DEFAULT 1,
                        "SourceMessageId" text NULL,
                        "CreatedBySenderId" text NOT NULL,
                        "CreatedBySenderName" text NOT NULL,
                        "Status" text NOT NULL DEFAULT 'Active',
                        "SupersedesConceptId" text NULL,
                        "ExpiresAt" timestamp with time zone NULL,
                        "LastConfirmedAt" timestamp with time zone NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloUserConcepts_Subject_Active"
                    ON "ZaloUserConcepts" ("GroupId", "SubjectZaloUserId", "Status", "UpdatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloUserConcepts_Type_Key"
                    ON "ZaloUserConcepts" ("GroupId", "ConceptType", "ConceptKey", "Status");
                    """, cancellationToken);
            }
            else if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ZaloUserConcepts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloUserConcepts" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "SubjectZaloUserId" TEXT NOT NULL,
                        "ConceptType" TEXT NOT NULL,
                        "ConceptKey" TEXT NOT NULL,
                        "ValueJson" TEXT NOT NULL,
                        "Scope" TEXT NOT NULL DEFAULT 'User',
                        "Confidence" REAL NOT NULL DEFAULT 1,
                        "SourceMessageId" TEXT NULL,
                        "CreatedBySenderId" TEXT NOT NULL,
                        "CreatedBySenderName" TEXT NOT NULL,
                        "Status" TEXT NOT NULL DEFAULT 'Active',
                        "SupersedesConceptId" TEXT NULL,
                        "ExpiresAt" TEXT NULL,
                        "LastConfirmedAt" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloUserConcepts_Subject_Active"
                    ON "ZaloUserConcepts" ("GroupId", "SubjectZaloUserId", "Status", "UpdatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloUserConcepts_Type_Key"
                    ON "ZaloUserConcepts" ("GroupId", "ConceptType", "ConceptKey", "Status");
                    """, cancellationToken);
            }
            else
            {
                return;
            }

            lock (ReadySchemas) ReadySchemas.Add(schemaKey);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static async Task<string?> FindActiveIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        string groupId,
        string subjectId,
        string type,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Id" FROM "ZaloUserConcepts"
            WHERE "GroupId" = @groupId AND "SubjectZaloUserId" = @subjectId
              AND "ConceptType" = @type AND "ConceptKey" = @key AND "Status" = 'Active'
            ORDER BY "UpdatedAt" DESC LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@subjectId", subjectId);
        Add(command, "@type", type);
        Add(command, "@key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static ZaloUserConceptSnapshot Read(DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        Convert.ToDouble(reader.GetValue(7)),
        NullableString(reader, 8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11),
        NullableString(reader, 12),
        NullableTimestamp(reader, 13),
        NullableTimestamp(reader, 14),
        Timestamp(reader, 15),
        Timestamp(reader, 16));

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static DateTimeOffset Timestamp(DbDataReader reader, int ordinal) =>
        ToTimestamp(reader.GetValue(ordinal)) ?? DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? NullableTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ToTimestamp(reader.GetValue(ordinal));

    private static DateTimeOffset? ToTimestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
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

    private static string Clean(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var cleaned = Clean(value, maxLength);
        return cleaned.Length == 0 ? null : cleaned;
    }
}
