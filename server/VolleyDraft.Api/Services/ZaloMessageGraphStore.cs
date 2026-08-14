using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloMessageGraphRelation(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string FromMessageId,
    string? ToMessageId,
    string RelationType,
    string? QuotedSenderId,
    string? QuotedSenderName,
    string? QuotedContentSnapshot,
    string? ProviderOutboundMessageId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Additive message-relation store. It keeps provider message IDs and quote edges
/// separate from mutable chat content, allowing V2 to traverse reply chains without
/// relying on synthetic local IDs. Snapshots are untrusted conversational data.
/// </summary>
public sealed class ZaloMessageGraphStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloMessageGraphRelation?> RememberIncomingQuoteAsync(
        string zaloConnectionId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (incoming.Quote is null) return null;
        var fromMessageId = Clean(incoming.MessageId, 160);
        var groupId = Clean(incoming.GroupId, 100);
        zaloConnectionId = Clean(zaloConnectionId, 100);
        if (fromMessageId.Length == 0 || groupId.Length == 0 || zaloConnectionId.Length == 0) return null;

        return await UpsertAsync(
            zaloConnectionId,
            groupId,
            fromMessageId,
            CleanOptional(incoming.Quote.MessageId, 160),
            "ReplyTo",
            CleanOptional(incoming.Quote.SenderId, 100),
            CleanOptional(incoming.Quote.SenderName, 160),
            CleanOptional(incoming.Quote.Content, 4000),
            null,
            cancellationToken);
    }

    public Task<ZaloMessageGraphRelation> RememberOutboundAsync(
        string zaloConnectionId,
        string groupId,
        string providerOutboundMessageId,
        string? inReplyToMessageId,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            Clean(zaloConnectionId, 100),
            Clean(groupId, 100),
            Clean(providerOutboundMessageId, 160),
            CleanOptional(inReplyToMessageId, 160),
            "BotReply",
            null,
            null,
            null,
            Clean(providerOutboundMessageId, 160),
            cancellationToken);

    public async Task<ZaloMessageGraphRelation?> LoadRelationAsync(
        string zaloConnectionId,
        string groupId,
        string fromMessageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ZaloConnectionId", "GroupId", "FromMessageId", "ToMessageId",
                   "RelationType", "QuotedSenderId", "QuotedSenderName", "QuotedContentSnapshot",
                   "ProviderOutboundMessageId", "CreatedAt"
            FROM "ZaloMessageRelations"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId AND "FromMessageId" = @fromMessageId
            LIMIT 1;
            """;
        Add(command, "@connectionId", Clean(zaloConnectionId, 100));
        Add(command, "@groupId", Clean(groupId, 100));
        Add(command, "@fromMessageId", Clean(fromMessageId, 160));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task<ZaloMessageGraphRelation> UpsertAsync(
        string zaloConnectionId,
        string groupId,
        string fromMessageId,
        string? toMessageId,
        string relationType,
        string? quotedSenderId,
        string? quotedSenderName,
        string? quotedContentSnapshot,
        string? providerOutboundMessageId,
        CancellationToken cancellationToken)
    {
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || fromMessageId.Length == 0)
            throw new ArgumentException("Connection, group and message ID are required for a message relation.");
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var existing = await LoadRelationAsync(zaloConnectionId, groupId, fromMessageId, cancellationToken);
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var createdAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloMessageRelations" (
                "Id", "ZaloConnectionId", "GroupId", "FromMessageId", "ToMessageId", "RelationType",
                "QuotedSenderId", "QuotedSenderName", "QuotedContentSnapshot", "ProviderOutboundMessageId", "CreatedAt")
            VALUES (@id, @connectionId, @groupId, @fromMessageId, @toMessageId, @relationType,
                    @quotedSenderId, @quotedSenderName, @quotedContent, @providerOutboundMessageId, @createdAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "FromMessageId") DO UPDATE SET
                "ToMessageId" = excluded."ToMessageId",
                "RelationType" = excluded."RelationType",
                "QuotedSenderId" = excluded."QuotedSenderId",
                "QuotedSenderName" = excluded."QuotedSenderName",
                "QuotedContentSnapshot" = excluded."QuotedContentSnapshot",
                "ProviderOutboundMessageId" = excluded."ProviderOutboundMessageId";
            """;
        Add(command, "@id", id);
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@fromMessageId", fromMessageId);
        Add(command, "@toMessageId", toMessageId);
        Add(command, "@relationType", Clean(relationType, 40));
        Add(command, "@quotedSenderId", quotedSenderId);
        Add(command, "@quotedSenderName", quotedSenderName);
        Add(command, "@quotedContent", quotedContentSnapshot);
        Add(command, "@providerOutboundMessageId", providerOutboundMessageId);
        Add(command, "@createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new ZaloMessageGraphRelation(
            id, zaloConnectionId, groupId, fromMessageId, toMessageId, relationType,
            quotedSenderId, quotedSenderName, quotedContentSnapshot, providerOutboundMessageId, createdAt);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) &&
            !provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                ? "timestamp with time zone"
                : "TEXT";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE IF NOT EXISTS "ZaloMessageRelations" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloMessageRelations" PRIMARY KEY,
                    "ZaloConnectionId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "FromMessageId" TEXT NOT NULL,
                    "ToMessageId" TEXT NULL,
                    "RelationType" TEXT NOT NULL,
                    "QuotedSenderId" TEXT NULL,
                    "QuotedSenderName" TEXT NULL,
                    "QuotedContentSnapshot" TEXT NULL,
                    "ProviderOutboundMessageId" TEXT NULL,
                    "CreatedAt" {timestamp} NOT NULL,
                    CONSTRAINT "UX_ZaloMessageRelations_From" UNIQUE ("ZaloConnectionId", "GroupId", "FromMessageId")
                );
                CREATE INDEX IF NOT EXISTS "IX_ZaloMessageRelations_To"
                ON "ZaloMessageRelations" ("ZaloConnectionId", "GroupId", "ToMessageId");
                CREATE INDEX IF NOT EXISTS "IX_ZaloMessageRelations_ProviderOutbound"
                ON "ZaloMessageRelations" ("ProviderOutboundMessageId");
                """, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static ZaloMessageGraphRelation Read(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        NullableString(reader, 4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7),
        NullableString(reader, 8), NullableString(reader, 9), Timestamp(reader.GetValue(10)));

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
