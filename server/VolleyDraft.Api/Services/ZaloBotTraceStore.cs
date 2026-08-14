using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloBotTraceEntry(
    string MessageId,
    string GroupId,
    string SenderZaloUserId,
    string AddressReason,
    string? IntentSource = null,
    string? Intent = null,
    double? Confidence = null,
    string ContextMessageIdsJson = "[]",
    string? QuotedMessageId = null,
    string ConceptIdsJson = "[]",
    string ResolvedPersonIdsJson = "[]",
    string? ResolvedSessionId = null,
    string? PendingStateBefore = null,
    string? PendingStateAfter = null,
    bool AiCalled = false,
    long? AiLatencyMs = null,
    long? TotalLatencyMs = null,
    string? FallbackReason = null,
    string? ReplyMessageId = null);

/// <summary>
/// Structured observability for one bot turn. Trace rows intentionally store IDs
/// and routing decisions rather than full raw prompts/messages to reduce privacy risk.
/// </summary>
public sealed class ZaloBotTraceStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<string> WriteAsync(ZaloBotTraceEntry trace, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("n");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloBotTraces" (
                "Id", "MessageId", "GroupId", "SenderZaloUserId", "AddressReason", "IntentSource", "Intent",
                "Confidence", "ContextMessageIdsJson", "QuotedMessageId", "ConceptIdsJson", "ResolvedPersonIdsJson",
                "ResolvedSessionId", "PendingStateBefore", "PendingStateAfter", "AiCalled", "AiLatencyMs",
                "TotalLatencyMs", "FallbackReason", "ReplyMessageId", "CreatedAt")
            VALUES (@id, @messageId, @groupId, @senderId, @addressReason, @intentSource, @intent,
                    @confidence, @contextIds, @quotedMessageId, @conceptIds, @personIds,
                    @sessionId, @pendingBefore, @pendingAfter, @aiCalled, @aiLatency,
                    @totalLatency, @fallbackReason, @replyMessageId, @createdAt);
            """;
        Add(command, "@id", id);
        Add(command, "@messageId", Clean(trace.MessageId, 160));
        Add(command, "@groupId", Clean(trace.GroupId, 100));
        Add(command, "@senderId", Clean(trace.SenderZaloUserId, 100));
        Add(command, "@addressReason", Clean(trace.AddressReason, 80));
        Add(command, "@intentSource", CleanOptional(trace.IntentSource, 80));
        Add(command, "@intent", CleanOptional(trace.Intent, 120));
        Add(command, "@confidence", trace.Confidence);
        Add(command, "@contextIds", Json(trace.ContextMessageIdsJson, 4000));
        Add(command, "@quotedMessageId", CleanOptional(trace.QuotedMessageId, 160));
        Add(command, "@conceptIds", Json(trace.ConceptIdsJson, 4000));
        Add(command, "@personIds", Json(trace.ResolvedPersonIdsJson, 4000));
        Add(command, "@sessionId", CleanOptional(trace.ResolvedSessionId, 100));
        Add(command, "@pendingBefore", CleanOptional(trace.PendingStateBefore, 2000));
        Add(command, "@pendingAfter", CleanOptional(trace.PendingStateAfter, 2000));
        Add(command, "@aiCalled", trace.AiCalled);
        Add(command, "@aiLatency", trace.AiLatencyMs);
        Add(command, "@totalLatency", trace.TotalLatencyMs);
        Add(command, "@fallbackReason", CleanOptional(trace.FallbackReason, 500));
        Add(command, "@replyMessageId", CleanOptional(trace.ReplyMessageId, 160));
        Add(command, "@createdAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"ZaloBotTraces\" WHERE \"CreatedAt\" < @cutoff;";
        Add(command, "@cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
            var boolean = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                ? "boolean"
                : "INTEGER";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE IF NOT EXISTS "ZaloBotTraces" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloBotTraces" PRIMARY KEY,
                    "MessageId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "SenderZaloUserId" TEXT NOT NULL,
                    "AddressReason" TEXT NOT NULL,
                    "IntentSource" TEXT NULL,
                    "Intent" TEXT NULL,
                    "Confidence" REAL NULL,
                    "ContextMessageIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "QuotedMessageId" TEXT NULL,
                    "ConceptIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "ResolvedPersonIdsJson" TEXT NOT NULL DEFAULT '[]',
                    "ResolvedSessionId" TEXT NULL,
                    "PendingStateBefore" TEXT NULL,
                    "PendingStateAfter" TEXT NULL,
                    "AiCalled" {boolean} NOT NULL DEFAULT {(provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ? "FALSE" : "0")},
                    "AiLatencyMs" INTEGER NULL,
                    "TotalLatencyMs" INTEGER NULL,
                    "FallbackReason" TEXT NULL,
                    "ReplyMessageId" TEXT NULL,
                    "CreatedAt" {timestamp} NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_ZaloBotTraces_Message" ON "ZaloBotTraces" ("GroupId", "MessageId");
                CREATE INDEX IF NOT EXISTS "IX_ZaloBotTraces_CreatedAt" ON "ZaloBotTraces" ("CreatedAt");
                """, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
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

    private static string Json(string? value, int maxLength)
    {
        var text = (value ?? "[]").Trim();
        if (text.Length == 0) text = "[]";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
