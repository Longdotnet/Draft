using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyOutcomeProjectionResult(int Scanned, int Projected);

/// <summary>
/// Incrementally projects terminal outcomes already persisted by the legacy bot into
/// the V2 trace schema. This gives one observability model without editing every
/// legacy return/failure branch during migration.
/// </summary>
public sealed class ZaloLegacyOutcomeTraceProjector(VolleyDraftDbContext db)
{
    private const string ProjectionSource = "LegacyOutcomeProjection";

    public async Task<ZaloLegacyOutcomeProjectionResult> ProjectAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var traceStore = new ZaloBotTraceStore(db);
        // Ensures additive trace schema exists; UnixEpoch removes no normal rows.
        await traceStore.DeleteOlderThanAsync(DateTimeOffset.UnixEpoch, cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var candidates = await ReadCandidatesAsync(connection, limit, cancellationToken);
        var projected = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProjectionExistsAsync(connection, candidate.GroupId, candidate.MessageId, cancellationToken))
                continue;

            long? totalLatencyMs = null;
            if (candidate.ProcessingStartedAt is not null && candidate.BotReplySentAt is not null)
            {
                totalLatencyMs = Math.Max(
                    0,
                    (long)(candidate.BotReplySentAt.Value - candidate.ProcessingStartedAt.Value).TotalMilliseconds);
            }

            await traceStore.WriteAsync(
                new ZaloBotTraceEntry(
                    candidate.MessageId,
                    candidate.GroupId,
                    candidate.SenderId,
                    "LegacyAddressedMessage",
                    IntentSource: ProjectionSource,
                    Intent: candidate.SelectedIntent,
                    Confidence: null,
                    AiCalled: candidate.AiCalled,
                    TotalLatencyMs: totalLatencyMs,
                    FallbackReason: candidate.ReplyOutcome),
                cancellationToken);
            projected += 1;
        }

        return new ZaloLegacyOutcomeProjectionResult(candidates.Count, projected);
    }

    private static async Task<List<Candidate>> ReadCandidatesAsync(
        DbConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // FALSE is accepted by PostgreSQL and SQLite. Raw ordering avoids EF Core's
        // SQLite DateTimeOffset ORDER BY limitation while preserving recent-first scan.
        command.CommandText = """
            SELECT "GroupId", "MessageId", "SenderId", "SelectedIntent", "AiCalled", "ReplyOutcome",
                   "ProcessingStartedAt", "BotReplySentAt", "ReceivedAt"
            FROM "ZaloGroupMessages"
            WHERE "IsFromBot" = FALSE
              AND "ReplyOutcome" IS NOT NULL
              AND "ReplyOutcome" <> 'processing'
            ORDER BY "ReceivedAt" DESC
            LIMIT @limit;
            """;
        Add(command, "@limit", limit);
        var result = new List<Candidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Candidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                Convert.ToBoolean(reader.GetValue(4)),
                NullableString(reader, 5),
                NullableTimestamp(reader, 6),
                NullableTimestamp(reader, 7)));
        }
        return result;
    }

    private static async Task<bool> ProjectionExistsAsync(
        DbConnection connection,
        string groupId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM "ZaloBotTraces"
            WHERE "GroupId" = @groupId
              AND "MessageId" = @messageId
              AND "IntentSource" = @source
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@messageId", messageId);
        Add(command, "@source", ProjectionSource);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static DateTimeOffset? NullableTimestamp(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record Candidate(
        string GroupId,
        string MessageId,
        string SenderId,
        string? SelectedIntent,
        bool AiCalled,
        string? ReplyOutcome,
        DateTimeOffset? ProcessingStartedAt,
        DateTimeOffset? BotReplySentAt);
}
