using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record TeamPosterAssignment(
    string SessionId,
    int TemplateId,
    int CycleNumber,
    DateTimeOffset AssignedAt);

public static class TeamPosterDeckLogic
{
    public static IReadOnlyList<int> BuildShuffledDeck(int? lastAssignedTemplateId, RandomNumberGenerator? rng = null)
    {
        var values = TeamPosterTemplateCatalog.AllIds.ToArray();
        var ownsRng = rng is null;
        rng ??= RandomNumberGenerator.Create();
        try
        {
            for (var index = values.Length - 1; index > 0; index -= 1)
            {
                var target = RandomNumberGenerator.GetInt32(index + 1);
                (values[index], values[target]) = (values[target], values[index]);
            }
        }
        finally
        {
            if (ownsRng) rng.Dispose();
        }

        if (lastAssignedTemplateId is >= 1 and <= TeamPosterTemplateCatalog.Count &&
            values.Length > 1 && values[0] == lastAssignedTemplateId.Value)
        {
            var swapIndex = Array.FindIndex(values, 1, value => value != lastAssignedTemplateId.Value);
            if (swapIndex > 0)
                (values[0], values[swapIndex]) = (values[swapIndex], values[0]);
        }
        return values;
    }

    internal static List<int> NormalizeRemainingDeck(string? json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<int>>(json ?? "[]") ?? [];
            return values
                .Where(TeamPosterTemplateCatalog.IsValid)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Persists one immutable poster assignment per session and one no-repeat deck per Zalo group.
/// The deck is consumed once per newly assigned session. When all ten templates have been used,
/// a fresh shuffled deck is created and its first item is forced to differ from the previous cycle's last poster.
/// </summary>
public static class TeamPosterRotationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates = new(StringComparer.Ordinal);
    private static int _schemaReady;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public static async Task<TeamPosterAssignment> EnsureAssignedAsync(
        VolleyDraftDbContext db,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(db, cancellationToken);

        var scope = await ReadSessionScopeAsync(db, sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy buổi đấu để chọn poster.");
        var rotationKey = BuildRotationKey(scope.ZaloConnectionId, scope.GroupId, sessionId);
        var gate = LocalGates.GetOrAdd(rotationKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadAssignmentAsync(db, sessionId, cancellationToken);
            if (existing is not null) return existing;

            if (string.IsNullOrWhiteSpace(scope.ZaloConnectionId) || string.IsNullOrWhiteSpace(scope.GroupId))
            {
                // Sessions outside a linked Zalo group are not part of a group deck. Keep them deterministic.
                return await InsertStandaloneAssignmentAsync(db, sessionId, cancellationToken);
            }

            return await AssignFromGroupDeckAsync(
                db,
                sessionId,
                scope.ZaloConnectionId,
                scope.GroupId,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<TeamPosterAssignment?> GetAssignmentAsync(
        VolleyDraftDbContext db,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(db, cancellationToken);
        return await ReadAssignmentAsync(db, sessionId, cancellationToken);
    }

    private static async Task<TeamPosterAssignment> AssignFromGroupDeckAsync(
        VolleyDraftDbContext db,
        string sessionId,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var rotationKey = BuildRotationKey(connectionId, groupId, sessionId);
            await EnsureRotationRowAsync(connection, transaction, rotationKey, connectionId, groupId, cancellationToken);
            var state = await ReadRotationStateForUpdateAsync(connection, transaction, rotationKey, cancellationToken);

            var existing = await ReadAssignmentAsync(connection, transaction, sessionId, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var remaining = TeamPosterDeckLogic.NormalizeRemainingDeck(state.RemainingTemplateIdsJson);
            var cycleNumber = state.CycleNumber;
            if (remaining.Count == 0)
            {
                remaining = TeamPosterDeckLogic.BuildShuffledDeck(state.LastAssignedTemplateId).ToList();
                cycleNumber += 1;
            }

            var templateId = remaining[0];
            remaining.RemoveAt(0);
            var now = DateTimeOffset.UtcNow;
            await InsertAssignmentAsync(
                connection,
                transaction,
                sessionId,
                connectionId,
                groupId,
                templateId,
                cycleNumber,
                now,
                cancellationToken);
            await UpdateRotationStateAsync(
                connection,
                transaction,
                rotationKey,
                JsonSerializer.Serialize(remaining),
                templateId,
                cycleNumber,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new TeamPosterAssignment(sessionId, templateId, cycleNumber, now);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<TeamPosterAssignment> InsertStandaloneAssignmentAsync(
        VolleyDraftDbContext db,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existing = await ReadAssignmentAsync(connection, transaction, sessionId, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
            var now = DateTimeOffset.UtcNow;
            await InsertAssignmentAsync(connection, transaction, sessionId, null, null, 1, 1, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TeamPosterAssignment(sessionId, 1, 1, now);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureSchemaAsync(VolleyDraftDbContext db, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaReady) == 1) return;
        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady == 1) return;
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);
            var postgres = (db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var timestampType = postgres ? "timestamp with time zone" : "TEXT";

            await ExecuteNonQueryAsync(connection, null, $"""
                CREATE TABLE IF NOT EXISTS "TeamPosterAssignments" (
                    "SessionId" TEXT PRIMARY KEY,
                    "ZaloConnectionId" TEXT NULL,
                    "GroupId" TEXT NULL,
                    "TemplateId" INTEGER NOT NULL,
                    "CycleNumber" INTEGER NOT NULL,
                    "AssignedAt" {timestampType} NOT NULL
                );
                """, cancellationToken);
            await ExecuteNonQueryAsync(connection, null, $"""
                CREATE TABLE IF NOT EXISTS "TeamPosterRotationStates" (
                    "RotationKey" TEXT PRIMARY KEY,
                    "ZaloConnectionId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "RemainingTemplateIdsJson" TEXT NOT NULL,
                    "LastAssignedTemplateId" INTEGER NULL,
                    "CycleNumber" INTEGER NOT NULL,
                    "UpdatedAt" {timestampType} NOT NULL
                );
                """, cancellationToken);
            await ExecuteNonQueryAsync(connection, null,
                "CREATE INDEX IF NOT EXISTS \"IX_TeamPosterAssignments_Group_Assigned\" ON \"TeamPosterAssignments\" (\"ZaloConnectionId\", \"GroupId\", \"AssignedAt\");",
                cancellationToken);
            Volatile.Write(ref _schemaReady, 1);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static async Task<SessionScope?> ReadSessionScopeAsync(
        VolleyDraftDbContext db,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"ZaloConnectionId\", \"ZaloGroupId\" FROM \"MatchSessions\" WHERE \"Id\" = @sessionId LIMIT 1";
        AddParameter(command, "@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SessionScope(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<TeamPosterAssignment?> ReadAssignmentAsync(
        VolleyDraftDbContext db,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        return await ReadAssignmentAsync(connection, null, sessionId, cancellationToken);
    }

    private static async Task<TeamPosterAssignment?> ReadAssignmentAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"TemplateId\", \"CycleNumber\", \"AssignedAt\" FROM \"TeamPosterAssignments\" WHERE \"SessionId\" = @sessionId LIMIT 1";
        AddParameter(command, "@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var templateId = reader.GetInt32(0);
        var cycleNumber = reader.GetInt32(1);
        var assignedAt = ReadDateTimeOffset(reader, 2);
        return new TeamPosterAssignment(sessionId, templateId, cycleNumber, assignedAt);
    }

    private static async Task EnsureRotationRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var postgres = connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = postgres
            ? "INSERT INTO \"TeamPosterRotationStates\" (\"RotationKey\", \"ZaloConnectionId\", \"GroupId\", \"RemainingTemplateIdsJson\", \"LastAssignedTemplateId\", \"CycleNumber\", \"UpdatedAt\") VALUES (@key, @connectionId, @groupId, '[]', NULL, 0, @now) ON CONFLICT (\"RotationKey\") DO NOTHING"
            : "INSERT OR IGNORE INTO \"TeamPosterRotationStates\" (\"RotationKey\", \"ZaloConnectionId\", \"GroupId\", \"RemainingTemplateIdsJson\", \"LastAssignedTemplateId\", \"CycleNumber\", \"UpdatedAt\") VALUES (@key, @connectionId, @groupId, '[]', NULL, 0, @now)";
        AddParameter(command, "@key", rotationKey);
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@groupId", groupId);
        AddParameter(command, "@now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RotationState> ReadRotationStateForUpdateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        CancellationToken cancellationToken)
    {
        var postgres = connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"RemainingTemplateIdsJson\", \"LastAssignedTemplateId\", \"CycleNumber\" FROM \"TeamPosterRotationStates\" WHERE \"RotationKey\" = @key" + (postgres ? " FOR UPDATE" : string.Empty);
        AddParameter(command, "@key", rotationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Không khởi tạo được poster deck của group.");
        return new RotationState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task InsertAssignmentAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sessionId,
        string? connectionId,
        string? groupId,
        int templateId,
        int cycleNumber,
        DateTimeOffset assignedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO \"TeamPosterAssignments\" (\"SessionId\", \"ZaloConnectionId\", \"GroupId\", \"TemplateId\", \"CycleNumber\", \"AssignedAt\") VALUES (@sessionId, @connectionId, @groupId, @templateId, @cycleNumber, @assignedAt)";
        AddParameter(command, "@sessionId", sessionId);
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@groupId", groupId);
        AddParameter(command, "@templateId", templateId);
        AddParameter(command, "@cycleNumber", cycleNumber);
        AddParameter(command, "@assignedAt", assignedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRotationStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        string remainingJson,
        int lastAssignedTemplateId,
        int cycleNumber,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE \"TeamPosterRotationStates\" SET \"RemainingTemplateIdsJson\" = @remaining, \"LastAssignedTemplateId\" = @last, \"CycleNumber\" = @cycle, \"UpdatedAt\" = @updatedAt WHERE \"RotationKey\" = @key";
        AddParameter(command, "@remaining", remainingJson);
        AddParameter(command, "@last", lastAssignedTemplateId);
        AddParameter(command, "@cycle", cycleNumber);
        AddParameter(command, "@updatedAt", updatedAt);
        AddParameter(command, "@key", rotationKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => DateTimeOffset.UtcNow
        };
    }

    private static string BuildRotationKey(string? connectionId, string? groupId, string sessionId) =>
        !string.IsNullOrWhiteSpace(connectionId) && !string.IsNullOrWhiteSpace(groupId)
            ? $"{connectionId}:{groupId}"
            : $"standalone:{sessionId}";

    private sealed record SessionScope(string? ZaloConnectionId, string? GroupId);
    private sealed record RotationState(string RemainingTemplateIdsJson, int? LastAssignedTemplateId, int CycleNumber);
}
