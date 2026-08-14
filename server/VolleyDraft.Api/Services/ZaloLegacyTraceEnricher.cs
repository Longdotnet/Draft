using System.Data;
using System.Data.Common;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyTraceEnrichmentResult(int Scanned, int Enriched);

/// <summary>
/// Adds IDs that become available outside the legacy router to already projected
/// terminal traces. It never infers business facts: reply IDs come from the message
/// graph and person/session IDs come from existing V2 pre-routing traces.
/// </summary>
public sealed class ZaloLegacyTraceEnricher(VolleyDraftDbContext db)
{
    public async Task<ZaloLegacyTraceEnrichmentResult> EnrichAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var traceStore = new ZaloBotTraceStore(db);
        await traceStore.DeleteOlderThanAsync(DateTimeOffset.UnixEpoch, cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var rows = await LoadProjectionRowsAsync(connection, limit, cancellationToken);
        var enriched = 0;
        var graph = new ZaloMessageGraphQuery(db);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectionId = await LoadConnectionIdAsync(connection, row.GroupId, row.MessageId, cancellationToken);
            var replyId = row.ReplyMessageId;
            if (replyId is null && connectionId is not null)
                replyId = await graph.LoadBotReplyMessageIdAsync(connectionId, row.GroupId, row.MessageId, cancellationToken);

            var supplement = await LoadSupplementAsync(connection, row.GroupId, row.MessageId, row.Id, cancellationToken);
            var personIds = IsEmptyJsonArray(row.ResolvedPersonIdsJson)
                ? supplement.ResolvedPersonIdsJson
                : row.ResolvedPersonIdsJson;
            var sessionId = string.IsNullOrWhiteSpace(row.ResolvedSessionId)
                ? supplement.ResolvedSessionId
                : row.ResolvedSessionId;

            if (replyId == row.ReplyMessageId &&
                personIds == row.ResolvedPersonIdsJson &&
                sessionId == row.ResolvedSessionId)
                continue;

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE "ZaloBotTraces"
                SET "ReplyMessageId" = @replyMessageId,
                    "ResolvedPersonIdsJson" = @personIds,
                    "ResolvedSessionId" = @sessionId
                WHERE "Id" = @id;
                """;
            Add(update, "@replyMessageId", replyId);
            Add(update, "@personIds", personIds);
            Add(update, "@sessionId", sessionId);
            Add(update, "@id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            enriched += 1;
        }

        return new ZaloLegacyTraceEnrichmentResult(rows.Count, enriched);
    }

    private static async Task<List<ProjectionRow>> LoadProjectionRowsAsync(
        DbConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "GroupId", "MessageId", "ReplyMessageId", "ResolvedPersonIdsJson", "ResolvedSessionId"
            FROM "ZaloBotTraces"
            WHERE "IntentSource" = 'LegacyOutcomeProjection'
            ORDER BY "CreatedAt" DESC
            LIMIT @limit;
            """;
        Add(command, "@limit", limit);
        var result = new List<ProjectionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                reader.GetString(4),
                NullableString(reader, 5)));
        }
        return result;
    }

    private static async Task<string?> LoadConnectionIdAsync(
        DbConnection connection,
        string groupId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ZaloConnectionId"
            FROM "ZaloGroupMessages"
            WHERE "GroupId" = @groupId AND "MessageId" = @messageId AND "IsFromBot" = FALSE
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@messageId", messageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static async Task<TraceSupplement> LoadSupplementAsync(
        DbConnection connection,
        string groupId,
        string messageId,
        string excludedTraceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ResolvedPersonIdsJson", "ResolvedSessionId"
            FROM "ZaloBotTraces"
            WHERE "GroupId" = @groupId
              AND "MessageId" = @messageId
              AND "Id" <> @excludedId
              AND (("ResolvedPersonIdsJson" IS NOT NULL AND "ResolvedPersonIdsJson" <> '[]')
                   OR "ResolvedSessionId" IS NOT NULL)
            ORDER BY "CreatedAt" DESC
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@messageId", messageId);
        Add(command, "@excludedId", excludedTraceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new("[]", null);
        return new(
            reader.IsDBNull(0) ? "[]" : Convert.ToString(reader.GetValue(0)) ?? "[]",
            NullableString(reader, 1));
    }

    private static bool IsEmptyJsonArray(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "[]";

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ProjectionRow(
        string Id,
        string GroupId,
        string MessageId,
        string? ReplyMessageId,
        string ResolvedPersonIdsJson,
        string? ResolvedSessionId);

    private sealed record TraceSupplement(string ResolvedPersonIdsJson, string? ResolvedSessionId);
}
