using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal static class ZaloAutoSessionRolloutGuard
{
    public static async Task<int> SupersedePendingAsync(
        VolleyDraftDbContext db,
        string? trackedGroupId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(trackedGroupId)
            ? """
              UPDATE "ZaloPollSessionProposals"
              SET "Status" = 'Superseded', "LastError" = @Reason, "UpdatedAt" = @UpdatedAt
              WHERE "Status" = 'AwaitingApproval';
              """
            : """
              UPDATE "ZaloPollSessionProposals"
              SET "Status" = 'Superseded', "LastError" = @Reason, "UpdatedAt" = @UpdatedAt
              WHERE "TrackedGroupId" = @TrackedGroupId AND "Status" = 'AwaitingApproval';
              """;
        AddParameter(command, "@Reason", Truncate(reason, 1000));
        AddParameter(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(trackedGroupId))
            AddParameter(command, "@TrackedGroupId", trackedGroupId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Truncate(string value, int maxLength)
    {
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
