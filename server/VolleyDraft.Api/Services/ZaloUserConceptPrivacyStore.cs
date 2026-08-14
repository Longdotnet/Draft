using System.Data;
using System.Data.Common;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Privacy-oriented deletion path for user-owned concept memory. This deliberately
/// hard-deletes matching active and historical/superseded rows, unlike DisableAsync
/// which is suitable for operational deactivation but still retains history.
/// </summary>
public sealed class ZaloUserConceptPrivacyStore(VolleyDraftDbContext db)
{
    public Task<int> DeleteKeyHistoryAsync(
        string groupId,
        string subjectZaloUserId,
        string conceptKey,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(groupId, subjectZaloUserId, conceptKey, cancellationToken);

    public Task<int> DeleteAllAsync(
        string groupId,
        string subjectZaloUserId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(groupId, subjectZaloUserId, null, cancellationToken);

    private async Task<int> DeleteAsync(
        string groupId,
        string subjectZaloUserId,
        string? conceptKey,
        CancellationToken cancellationToken)
    {
        // Ensure the additive memory table exists before issuing the privacy delete.
        // LoadActiveAsync is a cheap no-op for an empty scope and centralizes schema setup.
        await new ZaloUserConceptStore(db).LoadActiveAsync(groupId, subjectZaloUserId, 1, cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = conceptKey is null
            ? """
                DELETE FROM "ZaloUserConcepts"
                WHERE "GroupId" = @groupId AND "SubjectZaloUserId" = @subjectId;
                """
            : """
                DELETE FROM "ZaloUserConcepts"
                WHERE "GroupId" = @groupId AND "SubjectZaloUserId" = @subjectId AND "ConceptKey" = @key;
                """;
        Add(command, "@groupId", Clean(groupId, 100));
        Add(command, "@subjectId", Clean(subjectZaloUserId, 100));
        if (conceptKey is not null) Add(command, "@key", Clean(conceptKey, 120));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
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
