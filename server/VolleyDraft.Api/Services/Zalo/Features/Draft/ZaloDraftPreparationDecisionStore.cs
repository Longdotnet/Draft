using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloDraftPreparationDecisionKind
{
    KeepRecruiting,
    PlayCurrentRoster,
    StopMatch
}

internal sealed record ZaloDraftPreparationDecisionSnapshot(
    string SessionId,
    ZaloDraftPreparationDecisionKind Kind,
    string? RosterFingerprint,
    int? EffectiveSlotCount,
    string ActorZaloUserId,
    string ActorDisplayName,
    string SourceMessageId,
    DateTimeOffset DecidedAt,
    DateTimeOffset UpdatedAt)
{
    public bool MatchesRoster(ZaloDraftReadinessSnapshot readiness) =>
        Kind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster ||
        (!string.IsNullOrWhiteSpace(RosterFingerprint) &&
         string.Equals(RosterFingerprint, readiness.Fingerprint, StringComparison.Ordinal) &&
         EffectiveSlotCount == readiness.EffectiveSlotCount);
}

internal sealed class ZaloDraftPreparationDecisionStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloDraftPreparationDecisions" (
                "SessionId" TEXT PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "RosterFingerprint" TEXT NULL,
                "EffectiveSlotCount" INTEGER NULL,
                "ActorZaloUserId" TEXT NOT NULL,
                "ActorDisplayName" TEXT NOT NULL,
                "SourceMessageId" TEXT NOT NULL,
                "DecidedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<ZaloDraftPreparationDecisionSnapshot?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "SessionId", "Kind", "RosterFingerprint", "EffectiveSlotCount",
                   "ActorZaloUserId", "ActorDisplayName", "SourceMessageId", "DecidedAt", "UpdatedAt"
            FROM "ZaloDraftPreparationDecisions"
            WHERE "SessionId" = @SessionId
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", Clean(sessionId, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!Enum.TryParse<ZaloDraftPreparationDecisionKind>(reader.GetString(1), out var kind)) return null;
        return new ZaloDraftPreparationDecisionSnapshot(
            reader.GetString(0),
            kind,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseDate(reader.GetValue(7)),
            ParseDate(reader.GetValue(8)));
    }

    public async Task<ZaloDraftPreparationDecisionSnapshot> SetAsync(
        string sessionId,
        ZaloDraftPreparationDecisionKind kind,
        string? rosterFingerprint,
        int? effectiveSlotCount,
        string actorZaloUserId,
        string actorDisplayName,
        string sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (kind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster)
        {
            rosterFingerprint = null;
            effectiveSlotCount = null;
        }
        const string sql = """
            INSERT INTO "ZaloDraftPreparationDecisions" (
                "SessionId", "Kind", "RosterFingerprint", "EffectiveSlotCount",
                "ActorZaloUserId", "ActorDisplayName", "SourceMessageId", "DecidedAt", "UpdatedAt")
            VALUES (
                @SessionId, @Kind, @RosterFingerprint, @EffectiveSlotCount,
                @ActorZaloUserId, @ActorDisplayName, @SourceMessageId, @DecidedAt, @UpdatedAt)
            ON CONFLICT ("SessionId") DO UPDATE SET
                "Kind" = excluded."Kind",
                "RosterFingerprint" = excluded."RosterFingerprint",
                "EffectiveSlotCount" = excluded."EffectiveSlotCount",
                "ActorZaloUserId" = excluded."ActorZaloUserId",
                "ActorDisplayName" = excluded."ActorDisplayName",
                "SourceMessageId" = excluded."SourceMessageId",
                "DecidedAt" = excluded."DecidedAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", Clean(sessionId, 100));
        AddParameter(command, "@Kind", kind.ToString());
        AddParameter(command, "@RosterFingerprint", CleanOptional(rosterFingerprint, 160));
        AddParameter(command, "@EffectiveSlotCount", effectiveSlotCount);
        AddParameter(command, "@ActorZaloUserId", Clean(actorZaloUserId, 100));
        AddParameter(command, "@ActorDisplayName", Clean(actorDisplayName, 160));
        AddParameter(command, "@SourceMessageId", Clean(sourceMessageId, 160));
        AddParameter(command, "@DecidedAt", FormatDate(now));
        AddParameter(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAsync(sessionId, cancellationToken))!;
    }

    public async Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = "DELETE FROM \"ZaloDraftPreparationDecisions\" WHERE \"SessionId\" = @SessionId;";
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", Clean(sessionId, 100));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(object value) =>
        value is DateTimeOffset offset
            ? offset
            : DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    private static string Clean(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }
}
