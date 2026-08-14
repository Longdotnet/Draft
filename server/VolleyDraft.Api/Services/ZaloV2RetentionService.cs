using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloV2RetentionResult(
    int DeletedTraces,
    int DeletedMessageRelations,
    int DeletedUserConcepts);

/// <summary>
/// Retention cleanup for V2 observational/context data. Business records and current
/// application truth are deliberately outside this service.
/// </summary>
public sealed class ZaloV2RetentionService(VolleyDraftDbContext db)
{
    public async Task<ZaloV2RetentionResult> CleanupAsync(
        ZaloRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var deletedTraces = await new ZaloBotTraceStore(db)
            .DeleteOlderThanAsync(policy.TraceCutoff(now), cancellationToken);

        // Force additive schemas to exist before raw cleanup. Sentinel reads are
        // non-mutating and keep schema ownership inside each store.
        await new ZaloMessageGraphStore(db)
            .LoadRelationAsync("__retention__", "__retention__", "__retention__", cancellationToken);
        await new ZaloUserConceptStore(db)
            .LoadActiveAsync("__retention__", "__retention__", 1, cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var deletedRelations = await DeleteAsync(
            connection,
            "DELETE FROM \"ZaloMessageRelations\" WHERE \"CreatedAt\" < @cutoff;",
            [("@cutoff", (object?)policy.MessageRelationCutoff(now))],
            cancellationToken);

        var conceptSql = policy.ActiveUserConceptRetention is null
            ? "DELETE FROM \"ZaloUserConcepts\" WHERE \"ExpiresAt\" IS NOT NULL AND \"ExpiresAt\" <= @now;"
            : """
                DELETE FROM "ZaloUserConcepts"
                WHERE ("ExpiresAt" IS NOT NULL AND "ExpiresAt" <= @now)
                   OR "UpdatedAt" < @conceptCutoff;
                """;
        var conceptParameters = new List<(string Name, object? Value)> { ("@now", now) };
        if (policy.ActiveUserConceptRetention is not null)
            conceptParameters.Add(("@conceptCutoff", now - policy.ActiveUserConceptRetention.Value));
        var deletedConcepts = await DeleteAsync(connection, conceptSql, conceptParameters, cancellationToken);

        return new ZaloV2RetentionResult(deletedTraces, deletedRelations, deletedConcepts);
    }

    private static async Task<int> DeleteAsync(
        DbConnection connection,
        string sql,
        IEnumerable<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
