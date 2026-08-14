using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloOutboundReceipt(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string ProviderMessageId,
    string? ParentMessageId,
    string ContentSha256,
    DateTimeOffset CreatedAt);

/// <summary>
/// Short-lived migration receipts for provider outbound IDs. The store never keeps
/// raw outbound text: only a SHA-256 fingerprint used to match the legacy synthetic
/// core-history row after the bridge call has returned.
/// </summary>
public sealed class ZaloOutboundReceiptStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloOutboundReceipt> RememberAsync(
        string zaloConnectionId,
        string groupId,
        string providerMessageId,
        string? parentMessageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        providerMessageId = Clean(providerMessageId, 160);
        parentMessageId = CleanOptional(parentMessageId, 160);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || providerMessageId.Length == 0)
            throw new ArgumentException("Connection, group and provider message ID are required.");

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var existing = await LoadByProviderIdAsync(connection, zaloConnectionId, groupId, providerMessageId, cancellationToken);
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var createdAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;
        var fingerprint = Fingerprint(content);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloOutboundReceipts" (
                "Id", "ZaloConnectionId", "GroupId", "ProviderMessageId", "ParentMessageId", "ContentSha256", "CreatedAt")
            VALUES (@id, @connectionId, @groupId, @providerMessageId, @parentMessageId, @contentSha256, @createdAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "ProviderMessageId") DO UPDATE SET
                "ParentMessageId" = excluded."ParentMessageId",
                "ContentSha256" = excluded."ContentSha256";
            """;
        Add(command, "@id", id);
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@providerMessageId", providerMessageId);
        Add(command, "@parentMessageId", parentMessageId);
        Add(command, "@contentSha256", fingerprint);
        Add(command, "@createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new ZaloOutboundReceipt(id, zaloConnectionId, groupId, providerMessageId, parentMessageId, fingerprint, createdAt);
    }

    public async Task<IReadOnlyList<ZaloOutboundReceipt>> LoadRecentAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ZaloConnectionId", "GroupId", "ProviderMessageId", "ParentMessageId", "ContentSha256", "CreatedAt"
            FROM "ZaloOutboundReceipts"
            ORDER BY "CreatedAt" DESC
            LIMIT @limit;
            """;
        Add(command, "@limit", limit);
        var rows = new List<ZaloOutboundReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<int> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"ZaloOutboundReceipts\" WHERE \"Id\" = @id;";
        Add(command, "@id", Clean(id, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"ZaloOutboundReceipts\" WHERE \"CreatedAt\" < @cutoff;";
        Add(command, "@cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string Fingerprint(string? content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<ZaloOutboundReceipt?> LoadByProviderIdAsync(
        DbConnection connection,
        string zaloConnectionId,
        string groupId,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ZaloConnectionId", "GroupId", "ProviderMessageId", "ParentMessageId", "ContentSha256", "CreatedAt"
            FROM "ZaloOutboundReceipts"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId AND "ProviderMessageId" = @providerMessageId
            LIMIT 1;
            """;
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@providerMessageId", providerMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
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
                    CREATE TABLE IF NOT EXISTS "ZaloOutboundReceipts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOutboundReceipts" PRIMARY KEY,
                        "ZaloConnectionId" TEXT NOT NULL,
                        "GroupId" TEXT NOT NULL,
                        "ProviderMessageId" TEXT NOT NULL,
                        "ParentMessageId" TEXT NULL,
                        "ContentSha256" TEXT NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "UX_ZaloOutboundReceipts_Provider" UNIQUE ("ZaloConnectionId", "GroupId", "ProviderMessageId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOutboundReceipts_Parent"
                    ON "ZaloOutboundReceipts" ("ZaloConnectionId", "GroupId", "ParentMessageId");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOutboundReceipts_CreatedAt"
                    ON "ZaloOutboundReceipts" ("CreatedAt");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloOutboundReceipts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOutboundReceipts" PRIMARY KEY,
                        "ZaloConnectionId" TEXT NOT NULL,
                        "GroupId" TEXT NOT NULL,
                        "ProviderMessageId" TEXT NOT NULL,
                        "ParentMessageId" TEXT NULL,
                        "ContentSha256" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        CONSTRAINT "UX_ZaloOutboundReceipts_Provider" UNIQUE ("ZaloConnectionId", "GroupId", "ProviderMessageId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOutboundReceipts_Parent"
                    ON "ZaloOutboundReceipts" ("ZaloConnectionId", "GroupId", "ParentMessageId");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOutboundReceipts_CreatedAt"
                    ON "ZaloOutboundReceipts" ("CreatedAt");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static ZaloOutboundReceipt Read(DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        NullableString(reader, 4),
        reader.GetString(5),
        Timestamp(reader.GetValue(6)));

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var valueClean = Clean(value, maxLength);
        return valueClean.Length == 0 ? null : valueClean;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

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
