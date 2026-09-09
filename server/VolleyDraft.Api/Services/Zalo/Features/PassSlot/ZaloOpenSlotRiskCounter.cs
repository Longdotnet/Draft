using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Counts unresolved pass-slot offers from the durable offer ledger itself.
/// The owner may legitimately disappear from the current roster after unvoting,
/// so current SessionPlayer presence must never be used to discover these risks.
///
/// When this oracle runs inside the final draft transaction, it also takes a
/// short-lived write lock on the authoritative MatchSession row. That closes the
/// TOCTOU window between "no active pass risk" and committing Drafting: an ambient
/// pass opener must either win before this gate (and be counted) or wait until the
/// draft transaction commits, at which point its lifecycle CAS rejects Drafting.
/// The temporary gate values are cleared before returning, but the relational row/
/// write lock remains owned by the surrounding transaction until it completes.
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

        string? draftGateLeaseToken = null;
        if (db.Database.CurrentTransaction is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var currentLease = await db.MatchSessions
                .AsNoTracking()
                .Where(session => session.Id == sessionId)
                .Select(session => new
                {
                    session.BotActionLeaseToken,
                    session.BotActionLeaseName,
                    session.BotActionLeaseUntil
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (currentLease is null)
                return 0;

            try
            {
                if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
                {
                    // AutoRunDraftAsync already owns the authoritative session lease and
                    // calls StartDraftAsync/ResetDraftAsync inside it. Do not self-block;
                    // take a no-op write on the same row so the surrounding draft
                    // transaction still serializes against ambient pass opening.
                    if (!string.Equals(currentLease.BotActionLeaseName, "AutoDraft", StringComparison.Ordinal))
                        return 1;

                    var locked = await db.MatchSessions
                        .Where(session => session.Id == sessionId &&
                                          session.BotActionLeaseToken == currentLease.BotActionLeaseToken &&
                                          session.BotActionLeaseName == "AutoDraft")
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(session => session.BotActionLeaseUntil, session => session.BotActionLeaseUntil),
                            cancellationToken);
                    if (locked == 0)
                        return 1;
                }
                else
                {
                    draftGateLeaseToken = Guid.NewGuid().ToString("n");
                    var claimed = await db.MatchSessions
                        .Where(session => session.Id == sessionId &&
                                          session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(session => session.BotActionLeaseToken, draftGateLeaseToken)
                            .SetProperty(session => session.BotActionLeaseName, "DraftPassGate")
                            .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)),
                            cancellationToken);
                    if (claimed == 0)
                        return 1;
                }
            }
            catch (DbException)
            {
                // A concurrent writer can win after this transaction's earlier session
                // snapshot (notably SQLITE_BUSY_SNAPSHOT). Fail closed: the caller treats
                // a positive risk count as 409 and the surrounding transaction rolls back.
                return 1;
            }
        }

        try
        {
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
        finally
        {
            if (draftGateLeaseToken is not null)
            {
                try
                {
                    await db.MatchSessions
                        .Where(session => session.Id == sessionId && session.BotActionLeaseToken == draftGateLeaseToken)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(session => session.BotActionLeaseToken, (string?)null)
                            .SetProperty(session => session.BotActionLeaseName, (string?)null)
                            .SetProperty(session => session.BotActionLeaseUntil, (DateTimeOffset?)null),
                            cancellationToken);
                }
                catch (DbException)
                {
                    // The enclosing transaction is the authority. If cleanup itself loses
                    // the database lock, transaction disposal/rollback prevents a leaked
                    // committed gate lease; do not convert the safety check into mutation.
                }
            }
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
