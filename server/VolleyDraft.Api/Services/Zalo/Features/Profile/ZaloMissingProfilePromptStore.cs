using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloMissingProfilePromptContext(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string SessionId,
    string SessionPlayerId,
    string ZaloUserId,
    string DisplayName,
    bool MissingGender,
    bool MissingRole,
    bool MissingLevel,
    string? PromptMessageId,
    DateTimeOffset PromptedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastProcessedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable context for a short, human conversation such as:
/// bot: "@Long còn thiếu giới tính/vị trí, nói bình thường là được"
/// user: "nam, công"
///
/// This state is intentionally separate from ZaloBotConversationState so a proactive
/// profile question never overwrites another pending guest/share/draft conversation.
/// </summary>
internal sealed class ZaloMissingProfilePromptStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloMissingProfilePrompts" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL,
                "SessionPlayerId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "MissingGender" INTEGER NOT NULL DEFAULT 0,
                "MissingRole" INTEGER NOT NULL DEFAULT 0,
                "MissingLevel" INTEGER NOT NULL DEFAULT 0,
                "PromptMessageId" TEXT NULL,
                "PromptedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "LastProcessedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "UX_ZaloMissingProfilePrompts_Context"
                    UNIQUE ("ZaloConnectionId", "GroupId", "SessionId", "ZaloUserId")
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloMissingProfilePrompts_Active"
            ON "ZaloMissingProfilePrompts" ("ZaloConnectionId", "GroupId", "ZaloUserId", "CompletedAt");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<ZaloMissingProfilePromptContext> UpsertAsync(
        string zaloConnectionId,
        string groupId,
        string sessionId,
        string sessionPlayerId,
        string zaloUserId,
        string displayName,
        bool missingGender,
        bool missingRole,
        bool missingLevel,
        string? promptMessageId,
        DateTimeOffset promptedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var existing = await LoadExactAsync(
            zaloConnectionId,
            groupId,
            sessionId,
            zaloUserId,
            cancellationToken);
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloMissingProfilePrompts" (
                "Id", "ZaloConnectionId", "GroupId", "SessionId", "SessionPlayerId",
                "ZaloUserId", "DisplayName", "MissingGender", "MissingRole", "MissingLevel",
                "PromptMessageId", "PromptedAt", "ExpiresAt", "LastProcessedAt", "CompletedAt", "UpdatedAt")
            VALUES (
                @Id, @ZaloConnectionId, @GroupId, @SessionId, @SessionPlayerId,
                @ZaloUserId, @DisplayName, @MissingGender, @MissingRole, @MissingLevel,
                @PromptMessageId, @PromptedAt, @ExpiresAt, @LastProcessedAt, NULL, @UpdatedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "SessionId", "ZaloUserId") DO UPDATE SET
                "SessionPlayerId" = excluded."SessionPlayerId",
                "DisplayName" = excluded."DisplayName",
                "MissingGender" = excluded."MissingGender",
                "MissingRole" = excluded."MissingRole",
                "MissingLevel" = excluded."MissingLevel",
                "PromptMessageId" = excluded."PromptMessageId",
                "PromptedAt" = excluded."PromptedAt",
                "ExpiresAt" = excluded."ExpiresAt",
                "LastProcessedAt" = excluded."LastProcessedAt",
                "CompletedAt" = NULL,
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Id", id);
        Add(command, "@ZaloConnectionId", Clean(zaloConnectionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@SessionId", Clean(sessionId, 100));
        Add(command, "@SessionPlayerId", Clean(sessionPlayerId, 100));
        Add(command, "@ZaloUserId", Clean(zaloUserId, 100));
        Add(command, "@DisplayName", Clean(displayName, 160));
        Add(command, "@MissingGender", missingGender ? 1 : 0);
        Add(command, "@MissingRole", missingRole ? 1 : 0);
        Add(command, "@MissingLevel", missingLevel ? 1 : 0);
        Add(command, "@PromptMessageId", CleanOptional(promptMessageId, 160));
        Add(command, "@PromptedAt", FormatDate(promptedAt));
        Add(command, "@ExpiresAt", FormatDate(expiresAt));
        Add(command, "@LastProcessedAt", FormatDate(promptedAt));
        Add(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadExactAsync(zaloConnectionId, groupId, sessionId, zaloUserId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<ZaloMissingProfilePromptContext>> GetActiveAsync(
        DateTimeOffset now,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 500);

        // Never LIMIT the raw incomplete rows before expiry filtering. Expired rows are
        // intentionally kept for audit/history, so doing that can permanently hide a
        // newer live prompt from every consumer once enough stale rows accumulate.
        var rows = await LoadIncompleteAsync(cancellationToken);
        return rows
            .Where(item => item.ExpiresAt > now)
            .OrderBy(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Resolve conversational state at the same identity boundary used by Zalo routing.
    /// A synchronous sender reply must never depend on whether unrelated active prompts
    /// happen to fit inside a global worker batch limit.
    /// </summary>
    public async Task<IReadOnlyList<ZaloMissingProfilePromptContext>> GetActiveForSenderAsync(
        DateTimeOffset now,
        string zaloConnectionId,
        string groupId,
        string zaloUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT * FROM "ZaloMissingProfilePrompts"
            WHERE "CompletedAt" IS NULL
              AND "ZaloConnectionId" = @ZaloConnectionId
              AND "GroupId" = @GroupId
              AND "ZaloUserId" = @ZaloUserId;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ZaloConnectionId", Clean(zaloConnectionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@ZaloUserId", Clean(zaloUserId, 100));
        var rows = await ReadAllAsync(command, cancellationToken);
        return rows
            .Where(item => item.ExpiresAt > now)
            .OrderBy(item => item.PromptedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }

    public async Task UpdateProgressAsync(
        string id,
        bool missingGender,
        bool missingRole,
        bool missingLevel,
        DateTimeOffset lastProcessedAt,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloMissingProfilePrompts"
            SET "MissingGender" = @MissingGender,
                "MissingRole" = @MissingRole,
                "MissingLevel" = @MissingLevel,
                "LastProcessedAt" = @LastProcessedAt,
                "CompletedAt" = @CompletedAt,
                "UpdatedAt" = @UpdatedAt
            WHERE "Id" = @Id;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Id", Clean(id, 100));
        Add(command, "@MissingGender", missingGender ? 1 : 0);
        Add(command, "@MissingRole", missingRole ? 1 : 0);
        Add(command, "@MissingLevel", missingLevel ? 1 : 0);
        Add(command, "@LastProcessedAt", FormatDate(lastProcessedAt));
        Add(command, "@CompletedAt", completed ? FormatDate(DateTimeOffset.UtcNow) : null);
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        string id,
        DateTimeOffset lastProcessedAt,
        CancellationToken cancellationToken = default) =>
        await UpdateProgressAsync(id, false, false, false, lastProcessedAt, true, cancellationToken);

    private async Task<IReadOnlyList<ZaloMissingProfilePromptContext>> LoadIncompleteAsync(
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloMissingProfilePrompts\" WHERE \"CompletedAt\" IS NULL;",
            cancellationToken);
        return await ReadAllAsync(command, cancellationToken);
    }

    private async Task<ZaloMissingProfilePromptContext?> LoadExactAsync(
        string zaloConnectionId,
        string groupId,
        string sessionId,
        string zaloUserId,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT * FROM "ZaloMissingProfilePrompts"
            WHERE "ZaloConnectionId" = @ZaloConnectionId
              AND "GroupId" = @GroupId
              AND "SessionId" = @SessionId
              AND "ZaloUserId" = @ZaloUserId
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ZaloConnectionId", Clean(zaloConnectionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@SessionId", Clean(sessionId, 100));
        Add(command, "@ZaloUserId", Clean(zaloUserId, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
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

    private static async Task<IReadOnlyList<ZaloMissingProfilePromptContext>> ReadAllAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ZaloMissingProfilePromptContext>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    private static ZaloMissingProfilePromptContext Read(DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("Id")),
        reader.GetString(reader.GetOrdinal("ZaloConnectionId")),
        reader.GetString(reader.GetOrdinal("GroupId")),
        reader.GetString(reader.GetOrdinal("SessionId")),
        reader.GetString(reader.GetOrdinal("SessionPlayerId")),
        reader.GetString(reader.GetOrdinal("ZaloUserId")),
        reader.GetString(reader.GetOrdinal("DisplayName")),
        ReadBool(reader, "MissingGender"),
        ReadBool(reader, "MissingRole"),
        ReadBool(reader, "MissingLevel"),
        ReadNullableString(reader, "PromptMessageId"),
        ParseDate(reader.GetValue(reader.GetOrdinal("PromptedAt"))),
        ParseDate(reader.GetValue(reader.GetOrdinal("ExpiresAt"))),
        ParseDate(reader.GetValue(reader.GetOrdinal("LastProcessedAt"))),
        ReadNullableDate(reader, "CompletedAt"),
        ParseDate(reader.GetValue(reader.GetOrdinal("UpdatedAt"))));

    private static bool ReadBool(DbDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            bool flag => flag,
            byte number => number != 0,
            short number => number != 0,
            int number => number != 0,
            long number => number != 0,
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0
        };
    }

    private static string? ReadNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetValue(ordinal));
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
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
