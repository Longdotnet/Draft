using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloDraftEscalationState
{
    AwaitingRequesterConsent,
    ProactiveSoft,
    ApproverTagged,
    Executing,
    Completed,
    Cancelled,
    Expired,
    Superseded
}

public sealed record ZaloDraftEscalationSnapshot(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string SessionId,
    string Origin,
    string? RequestedBySenderId,
    string? RequestedBySenderName,
    string? RequestedByMessageId,
    ZaloDraftEscalationState State,
    string RosterFingerprint,
    string? PrimaryApproverId,
    string? SecondaryApproverId,
    string? PrimaryApproverMessageId,
    string? SecondaryApproverMessageId,
    DateTimeOffset? SoftNudgeSentAt,
    DateTimeOffset? PrimaryNudgeAt,
    DateTimeOffset? SecondaryNudgeAt,
    string? ExecutionToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable, provider-portable state for one draft escalation per linked session.
/// Runtime DDL follows the same additive SQLite/PostgreSQL rollout pattern used by
/// the V2 conversation store, avoiding a migration dependency for the bot pilot.
/// </summary>
public sealed class ZaloDraftEscalationStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly ZaloDraftEscalationState[] ActiveStates =
    [
        ZaloDraftEscalationState.AwaitingRequesterConsent,
        ZaloDraftEscalationState.ProactiveSoft,
        ZaloDraftEscalationState.ApproverTagged,
        ZaloDraftEscalationState.Executing
    ];

    public async Task<ZaloDraftEscalationSnapshot?> LoadForSessionAsync(
        string connectionId,
        string groupId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var row = await LoadOneAsync(
            "\"ZaloConnectionId\" = @connectionId AND \"GroupId\" = @groupId AND \"SessionId\" = @sessionId",
            [("@connectionId", Clean(connectionId, 100)), ("@groupId", Clean(groupId, 100)), ("@sessionId", Clean(sessionId, 100))],
            cancellationToken);
        return await ExpireIfNeededAsync(row, cancellationToken);
    }

    public async Task<ZaloDraftEscalationSnapshot?> LoadActiveForRequesterAsync(
        string connectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var row = await LoadOneAsync(
            "\"ZaloConnectionId\" = @connectionId AND \"GroupId\" = @groupId AND \"RequestedBySenderId\" = @senderId AND \"State\" IN ('AwaitingRequesterConsent','ProactiveSoft','ApproverTagged','Executing')",
            [("@connectionId", Clean(connectionId, 100)), ("@groupId", Clean(groupId, 100)), ("@senderId", Clean(senderId, 100))],
            cancellationToken,
            orderByUpdated: true);
        return await ExpireIfNeededAsync(row, cancellationToken);
    }

    public async Task<ZaloDraftEscalationSnapshot?> LoadActiveForApproverAsync(
        string connectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var row = await LoadOneAsync(
            "\"ZaloConnectionId\" = @connectionId AND \"GroupId\" = @groupId AND \"State\" IN ('ApproverTagged','Executing') AND (\"PrimaryApproverId\" = @senderId OR \"SecondaryApproverId\" = @senderId)",
            [("@connectionId", Clean(connectionId, 100)), ("@groupId", Clean(groupId, 100)), ("@senderId", Clean(senderId, 100))],
            cancellationToken,
            orderByUpdated: true);
        return await ExpireIfNeededAsync(row, cancellationToken);
    }

    public async Task<ZaloDraftEscalationSnapshot> CreateOrReuseAsync(
        string connectionId,
        string groupId,
        string sessionId,
        string origin,
        string? requesterId,
        string? requesterName,
        string? requesterMessageId,
        string fingerprint,
        ZaloDraftEscalationState initialState,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId, 100);
        groupId = Clean(groupId, 100);
        sessionId = Clean(sessionId, 100);
        fingerprint = Clean(fingerprint, 128);
        if (connectionId.Length == 0 || groupId.Length == 0 || sessionId.Length == 0 || fingerprint.Length == 0)
            throw new ArgumentException("Connection, group, session and fingerprint are required.");
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await EnsureSchemaAsync(cancellationToken);
        var existing = await LoadForSessionAsync(connectionId, groupId, sessionId, cancellationToken);
        if (existing is not null &&
            ActiveStates.Contains(existing.State) &&
            string.Equals(existing.RosterFingerprint, fingerprint, StringComparison.Ordinal))
            return existing;

        var now = DateTimeOffset.UtcNow;
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var createdAt = existing is null ? now : now;
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloDraftEscalationRequests" (
                "Id", "ZaloConnectionId", "GroupId", "SessionId", "Origin",
                "RequestedBySenderId", "RequestedBySenderName", "RequestedByMessageId",
                "State", "RosterFingerprint", "PrimaryApproverId", "SecondaryApproverId",
                "PrimaryApproverMessageId", "SecondaryApproverMessageId",
                "SoftNudgeSentAt", "PrimaryNudgeAt", "SecondaryNudgeAt", "ExecutionToken",
                "CreatedAt", "ExpiresAt", "UpdatedAt")
            VALUES (@id, @connectionId, @groupId, @sessionId, @origin,
                    @requesterId, @requesterName, @requesterMessageId,
                    @state, @fingerprint, NULL, NULL, NULL, NULL,
                    NULL, NULL, NULL, NULL, @createdAt, @expiresAt, @updatedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "SessionId") DO UPDATE SET
                "Origin" = excluded."Origin",
                "RequestedBySenderId" = excluded."RequestedBySenderId",
                "RequestedBySenderName" = excluded."RequestedBySenderName",
                "RequestedByMessageId" = excluded."RequestedByMessageId",
                "State" = excluded."State",
                "RosterFingerprint" = excluded."RosterFingerprint",
                "PrimaryApproverId" = NULL,
                "SecondaryApproverId" = NULL,
                "PrimaryApproverMessageId" = NULL,
                "SecondaryApproverMessageId" = NULL,
                "SoftNudgeSentAt" = NULL,
                "PrimaryNudgeAt" = NULL,
                "SecondaryNudgeAt" = NULL,
                "ExecutionToken" = NULL,
                "CreatedAt" = excluded."CreatedAt",
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        Add(command, "@id", id);
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@sessionId", sessionId);
        Add(command, "@origin", Clean(origin, 40));
        Add(command, "@requesterId", CleanOptional(requesterId, 100));
        Add(command, "@requesterName", CleanOptional(requesterName, 160));
        Add(command, "@requesterMessageId", CleanOptional(requesterMessageId, 160));
        Add(command, "@state", initialState.ToString());
        Add(command, "@fingerprint", fingerprint);
        Add(command, "@createdAt", createdAt);
        Add(command, "@expiresAt", expiresAt);
        Add(command, "@updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadForSessionAsync(connectionId, groupId, sessionId, cancellationToken))!;
    }

    public Task<int> MarkSoftNudgeAsync(
        string id,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id,
            "\"State\" = 'ProactiveSoft', \"SoftNudgeSentAt\" = @sentAt, \"UpdatedAt\" = @sentAt",
            [("@sentAt", sentAt)],
            cancellationToken);

    public Task<int> SetPrimaryApproverAsync(
        string id,
        string approverId,
        string? providerMessageId,
        DateTimeOffset sentAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id,
            "\"State\" = 'ApproverTagged', \"PrimaryApproverId\" = @approverId, \"PrimaryApproverMessageId\" = @messageId, \"PrimaryNudgeAt\" = @sentAt, \"ExpiresAt\" = @expiresAt, \"UpdatedAt\" = @sentAt",
            [("@approverId", Clean(approverId, 100)), ("@messageId", CleanOptional(providerMessageId, 160)), ("@sentAt", sentAt), ("@expiresAt", expiresAt)],
            cancellationToken);

    public Task<int> SetSecondaryApproverAsync(
        string id,
        string approverId,
        string? providerMessageId,
        DateTimeOffset sentAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id,
            "\"State\" = 'ApproverTagged', \"SecondaryApproverId\" = @approverId, \"SecondaryApproverMessageId\" = @messageId, \"SecondaryNudgeAt\" = @sentAt, \"ExpiresAt\" = @expiresAt, \"UpdatedAt\" = @sentAt",
            [("@approverId", Clean(approverId, 100)), ("@messageId", CleanOptional(providerMessageId, 160)), ("@sentAt", sentAt), ("@expiresAt", expiresAt)],
            cancellationToken);

    public async Task<string?> TryClaimExecutionAsync(
        ZaloDraftEscalationSnapshot request,
        string approverId,
        string expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (request.State != ZaloDraftEscalationState.ApproverTagged || request.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        approverId = Clean(approverId, 100);
        if (!string.Equals(request.PrimaryApproverId, approverId, StringComparison.Ordinal) &&
            !string.Equals(request.SecondaryApproverId, approverId, StringComparison.Ordinal))
            return null;
        if (!string.Equals(request.RosterFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return null;

        await EnsureSchemaAsync(cancellationToken);
        var token = $"draft-auto:{Guid.NewGuid():n}";
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloDraftEscalationRequests"
            SET "State" = 'Executing', "ExecutionToken" = @token, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "State" = 'ApproverTagged' AND "ExecutionToken" IS NULL
              AND "RosterFingerprint" = @fingerprint
              AND ("PrimaryApproverId" = @approverId OR "SecondaryApproverId" = @approverId);
            """;
        Add(command, "@token", token);
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", request.Id);
        Add(command, "@fingerprint", expectedFingerprint);
        Add(command, "@approverId", approverId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? token : null;
    }

    public Task<int> SetStateAsync(
        string id,
        ZaloDraftEscalationState state,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id,
            "\"State\" = @state, \"ExecutionToken\" = NULL, \"UpdatedAt\" = @updatedAt",
            [("@state", state.ToString()), ("@updatedAt", DateTimeOffset.UtcNow)],
            cancellationToken);

    public async Task<IReadOnlyList<ZaloDraftEscalationSnapshot>> LoadActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE \"State\" IN ('AwaitingRequesterConsent','ProactiveSoft','ApproverTagged','Executing') ORDER BY \"UpdatedAt\" ASC;";
        var rows = new List<ZaloDraftEscalationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    private async Task<ZaloDraftEscalationSnapshot?> ExpireIfNeededAsync(
        ZaloDraftEscalationSnapshot? row,
        CancellationToken cancellationToken)
    {
        if (row is null || !ActiveStates.Contains(row.State) || row.ExpiresAt > DateTimeOffset.UtcNow) return row;
        await SetStateAsync(row.Id, ZaloDraftEscalationState.Expired, cancellationToken);
        return row with { State = ZaloDraftEscalationState.Expired, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private async Task<ZaloDraftEscalationSnapshot?> LoadOneAsync(
        string where,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken,
        bool orderByUpdated = false)
    {
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + $" WHERE {where}" + (orderByUpdated ? " ORDER BY \"UpdatedAt\" DESC" : string.Empty) + " LIMIT 1;";
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task<int> UpdateAsync(
        string id,
        string assignments,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE \"ZaloDraftEscalationRequests\" SET {assignments} WHERE \"Id\" = @id;";
        Add(command, "@id", Clean(id, 100));
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!isPostgres && !isSqlite) return;

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = isPostgres ? "timestamp with time zone" : "TEXT";
            var sql = $"""
                CREATE TABLE IF NOT EXISTS "ZaloDraftEscalationRequests" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloDraftEscalationRequests" PRIMARY KEY,
                    "ZaloConnectionId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "SessionId" TEXT NOT NULL,
                    "Origin" TEXT NOT NULL,
                    "RequestedBySenderId" TEXT NULL,
                    "RequestedBySenderName" TEXT NULL,
                    "RequestedByMessageId" TEXT NULL,
                    "State" TEXT NOT NULL,
                    "RosterFingerprint" TEXT NOT NULL,
                    "PrimaryApproverId" TEXT NULL,
                    "SecondaryApproverId" TEXT NULL,
                    "PrimaryApproverMessageId" TEXT NULL,
                    "SecondaryApproverMessageId" TEXT NULL,
                    "SoftNudgeSentAt" {timestamp} NULL,
                    "PrimaryNudgeAt" {timestamp} NULL,
                    "SecondaryNudgeAt" {timestamp} NULL,
                    "ExecutionToken" TEXT NULL,
                    "CreatedAt" {timestamp} NOT NULL,
                    "ExpiresAt" {timestamp} NOT NULL,
                    "UpdatedAt" {timestamp} NOT NULL,
                    CONSTRAINT "UX_ZaloDraftEscalationRequests_Scope" UNIQUE ("ZaloConnectionId", "GroupId", "SessionId")
                );
                CREATE INDEX IF NOT EXISTS "IX_ZaloDraftEscalationRequests_Active"
                ON "ZaloDraftEscalationRequests" ("GroupId", "State", "ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_ZaloDraftEscalationRequests_Requester"
                ON "ZaloDraftEscalationRequests" ("GroupId", "RequestedBySenderId", "State");
                CREATE INDEX IF NOT EXISTS "IX_ZaloDraftEscalationRequests_Approver"
                ON "ZaloDraftEscalationRequests" ("GroupId", "PrimaryApproverId", "SecondaryApproverId", "State");
                """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private const string SelectColumns = """
        SELECT "Id", "ZaloConnectionId", "GroupId", "SessionId", "Origin",
               "RequestedBySenderId", "RequestedBySenderName", "RequestedByMessageId",
               "State", "RosterFingerprint", "PrimaryApproverId", "SecondaryApproverId",
               "PrimaryApproverMessageId", "SecondaryApproverMessageId",
               "SoftNudgeSentAt", "PrimaryNudgeAt", "SecondaryNudgeAt", "ExecutionToken",
               "CreatedAt", "ExpiresAt", "UpdatedAt"
        FROM "ZaloDraftEscalationRequests"
        """;

    private static ZaloDraftEscalationSnapshot Read(DbDataReader reader)
    {
        _ = Enum.TryParse<ZaloDraftEscalationState>(reader.GetString(8), true, out var state);
        return new ZaloDraftEscalationSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), state,
            reader.GetString(9), NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12),
            NullableString(reader, 13), NullableTimestamp(reader, 14), NullableTimestamp(reader, 15),
            NullableTimestamp(reader, 16), NullableString(reader, 17), Timestamp(reader.GetValue(18)),
            Timestamp(reader.GetValue(19)), Timestamp(reader.GetValue(20)));
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

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static DateTimeOffset? NullableTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Timestamp(reader.GetValue(ordinal));

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
