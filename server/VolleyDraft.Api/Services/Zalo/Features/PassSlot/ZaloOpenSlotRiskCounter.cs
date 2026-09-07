using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Counts unresolved pass-slot offers from the durable offer ledger itself.
/// The owner may legitimately disappear from the current roster after unvoting,
/// so current SessionPlayer presence must never be used to discover these risks.
/// </summary>
public sealed class ZaloOpenSlotRiskCounter(VolleyDraftDbContext db)
{
    public async Task<int> CountActiveForSessionAsync(
        string connectionId,
        string groupId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId);
        groupId = Clean(groupId);
        sessionId = Clean(sessionId);
        if (connectionId.Length == 0 || groupId.Length == 0 || sessionId.Length == 0)
            return 0;

        // The low-level offer store owns provider-specific schema bootstrap/migration.
        // Trigger that boundary before issuing the small aggregate query below.
        _ = await new ZaloOpenSlotOfferStore(db).ListClaimableAsync(
            "__schema__",
            "__schema__",
            "__schema__",
            cancellationToken);

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            // The final draft mutation gate runs inside SessionDraftService's EF
            // transaction. Raw provider commands must join that transaction explicitly;
            // otherwise SQLite (and some relational providers) reject the command and
            // turn a safety check into a runtime failure.
            if (db.Database.CurrentTransaction is { } currentTransaction)
                command.Transaction = currentTransaction.GetDbTransaction();
            command.CommandText = """
                SELECT COUNT(*)
                FROM "ZaloOpenSlotOffers"
                WHERE "ConnectionId" = @connectionId
                  AND "GroupId" = @groupId
                  AND "SessionId" = @sessionId
                  AND "Status" IN ('Open', 'ClaimPending', 'Applying')
                  AND "ExpiresAt" > @now;
                """;
            Add(command, "@connectionId", connectionId);
            Add(command, "@groupId", groupId);
            Add(command, "@sessionId", sessionId);
            Add(command, "@now", DateTimeOffset.UtcNow);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
