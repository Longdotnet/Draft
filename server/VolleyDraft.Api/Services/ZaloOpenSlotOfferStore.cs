using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloOpenSlotOfferStatus
{
    Open,
    ClaimPending,
    Applying,
    Completed,
    Cancelled,
    Expired
}

public sealed record ZaloOpenSlotOfferSnapshot(
    string Id,
    string GroupId,
    string OwnerZaloUserId,
    string OwnerDisplayName,
    string SessionId,
    string SessionName,
    string? SourceMessageId,
    string? ClaimantZaloUserId,
    string? ClaimantDisplayName,
    string? ClaimMessageId,
    ZaloOpenSlotOfferStatus Status,
    int Version,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable, group-scoped coordination state for a member explicitly offering their
/// own slot. This is intentionally separate from sender-scoped ConversationStateV2:
/// an open offer is a multi-user group object and must not overwrite an unrelated
/// pending clarification owned by either the giver or the claimant.
/// </summary>
public sealed class ZaloOpenSlotOfferStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloOpenSlotOfferSnapshot> OpenAsync(
        string groupId,
        string ownerZaloUserId,
        string ownerDisplayName,
        string sessionId,
        string sessionName,
        string? sourceMessageId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        ownerZaloUserId = Clean(ownerZaloUserId, 100);
        ownerDisplayName = Clean(ownerDisplayName, 160);
        sessionId = Clean(sessionId, 100);
        sessionName = Clean(sessionName, 160);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        if (groupId.Length == 0 || ownerZaloUserId.Length == 0 || ownerDisplayName.Length == 0 ||
            sessionId.Length == 0 || sessionName.Length == 0)
            throw new ArgumentException("Group, owner and session are required for an open slot offer.");
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var existing = await LoadOwnerSessionAnyAsync(
            connection, groupId, ownerZaloUserId, sessionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            var id = Guid.NewGuid().ToString("n");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO "ZaloOpenSlotOffers" (
                    "Id", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                    "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                    "Status", "Version", "ExpiresAt", "CreatedAt", "UpdatedAt")
                VALUES (@id, @groupId, @ownerId, @ownerName, @sessionId, @sessionName,
                        @sourceMessageId, NULL, NULL, NULL, 'Open', 1, @expiresAt, @createdAt, @updatedAt);
                """;
            Add(insert, "@id", id);
            Add(insert, "@groupId", groupId);
            Add(insert, "@ownerId", ownerZaloUserId);
            Add(insert, "@ownerName", ownerDisplayName);
            Add(insert, "@sessionId", sessionId);
            Add(insert, "@sessionName", sessionName);
            Add(insert, "@sourceMessageId", sourceMessageId);
            Add(insert, "@expiresAt", expiresAt);
            Add(insert, "@createdAt", now);
            Add(insert, "@updatedAt", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return new ZaloOpenSlotOfferSnapshot(
                id, groupId, ownerZaloUserId, ownerDisplayName, sessionId, sessionName,
                sourceMessageId, null, null, null, ZaloOpenSlotOfferStatus.Open, 1,
                expiresAt, now, now);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "OwnerDisplayName" = @ownerName,
                "SessionName" = @sessionName,
                "SourceMessageId" = @sourceMessageId,
                "ClaimantZaloUserId" = NULL,
                "ClaimantDisplayName" = NULL,
                "ClaimMessageId" = NULL,
                "Status" = 'Open',
                "Version" = @version,
                "ExpiresAt" = @expiresAt,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id;
            """;
        var version = existing.Version + 1;
        Add(update, "@ownerName", ownerDisplayName);
        Add(update, "@sessionName", sessionName);
        Add(update, "@sourceMessageId", sourceMessageId);
        Add(update, "@version", version);
        Add(update, "@expiresAt", expiresAt);
        Add(update, "@updatedAt", now);
        Add(update, "@id", existing.Id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return existing with
        {
            OwnerDisplayName = ownerDisplayName,
            SessionName = sessionName,
            SourceMessageId = sourceMessageId,
            ClaimantZaloUserId = null,
            ClaimantDisplayName = null,
            ClaimMessageId = null,
            Status = ZaloOpenSlotOfferStatus.Open,
            Version = version,
            ExpiresAt = expiresAt,
            UpdatedAt = now
        };
    }

    public async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListClaimableAsync(
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        if (groupId.Length == 0 || claimantZaloUserId.Length == 0) return [];
        var rows = await QueryAsync(
            """
            SELECT "Id", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                   "Status", "Version", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloOpenSlotOffers"
            WHERE "GroupId" = @groupId AND "Status" = 'Open';
            """,
            [("@groupId", groupId)],
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows
            .Where(item => item.ExpiresAt > now &&
                           !string.Equals(item.OwnerZaloUserId, claimantZaloUserId, StringComparison.Ordinal))
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListOwnedActiveAsync(
        string groupId,
        string ownerZaloUserId,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        ownerZaloUserId = Clean(ownerZaloUserId, 100);
        if (groupId.Length == 0 || ownerZaloUserId.Length == 0) return [];
        var rows = await QueryAsync(
            """
            SELECT "Id", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                   "Status", "Version", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloOpenSlotOffers"
            WHERE "GroupId" = @groupId AND "OwnerZaloUserId" = @ownerId
              AND "Status" IN ('Open', 'ClaimPending', 'Applying');
            """,
            [("@groupId", groupId), ("@ownerId", ownerZaloUserId)],
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows.Where(item => item.ExpiresAt > now).OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public async Task<ZaloOpenSlotOfferSnapshot?> LoadPendingClaimAsync(
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        if (groupId.Length == 0 || claimantZaloUserId.Length == 0) return null;
        var rows = await QueryAsync(
            """
            SELECT "Id", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                   "Status", "Version", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloOpenSlotOffers"
            WHERE "GroupId" = @groupId AND "ClaimantZaloUserId" = @claimantId
              AND "Status" IN ('ClaimPending', 'Applying');
            """,
            [("@groupId", groupId), ("@claimantId", claimantZaloUserId)],
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows.Where(item => item.ExpiresAt > now).OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
    }

    public async Task<bool> TryClaimAsync(
        ZaloOpenSlotOfferSnapshot offer,
        string claimantZaloUserId,
        string claimantDisplayName,
        string? claimMessageId,
        CancellationToken cancellationToken = default)
    {
        if (offer.Status != ZaloOpenSlotOfferStatus.Open || offer.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        claimantDisplayName = Clean(claimantDisplayName, 160);
        claimMessageId = CleanOptional(claimMessageId, 160);
        if (claimantZaloUserId.Length == 0 || claimantDisplayName.Length == 0 ||
            string.Equals(claimantZaloUserId, offer.OwnerZaloUserId, StringComparison.Ordinal))
            return false;

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        // One person may only hold one pending claim in a group at a time. Releasing
        // an older unconfirmed claim keeps stale chat from blocking a new explicit
        // "tui nhận" turn.
        await using (var releaseOld = connection.CreateCommand())
        {
            releaseOld.CommandText = """
                UPDATE "ZaloOpenSlotOffers"
                SET "Status" = 'Open', "ClaimantZaloUserId" = NULL, "ClaimantDisplayName" = NULL,
                    "ClaimMessageId" = NULL, "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
                WHERE "GroupId" = @groupId AND "ClaimantZaloUserId" = @claimantId
                  AND "Status" = 'ClaimPending' AND "Id" <> @offerId;
                """;
            Add(releaseOld, "@updatedAt", DateTimeOffset.UtcNow);
            Add(releaseOld, "@groupId", offer.GroupId);
            Add(releaseOld, "@claimantId", claimantZaloUserId);
            Add(releaseOld, "@offerId", offer.Id);
            await releaseOld.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ClaimantZaloUserId" = @claimantId,
                "ClaimantDisplayName" = @claimantName,
                "ClaimMessageId" = @claimMessageId,
                "Status" = 'ClaimPending',
                "Version" = "Version" + 1,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "Status" = 'Open' AND "Version" = @version;
            """;
        Add(command, "@claimantId", claimantZaloUserId);
        Add(command, "@claimantName", claimantDisplayName);
        Add(command, "@claimMessageId", claimMessageId);
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", offer.Id);
        Add(command, "@version", offer.Version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task<bool> TryBeginApplyAsync(
        string offerId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(offerId, claimantZaloUserId, "ClaimPending", "Applying", clearClaim: false, cancellationToken);

    public Task<bool> CompleteAsync(
        string offerId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(offerId, claimantZaloUserId, "Applying", "Completed", clearClaim: false, cancellationToken);

    public async Task<bool> ReleaseClaimAsync(
        string offerId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "Status" = 'Open', "ClaimantZaloUserId" = NULL, "ClaimantDisplayName" = NULL,
                "ClaimMessageId" = NULL, "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId
              AND "Status" IN ('ClaimPending', 'Applying');
            """;
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@claimantId", Clean(claimantZaloUserId, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> CancelAsync(
        string offerId,
        string ownerZaloUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "Status" = 'Cancelled', "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "OwnerZaloUserId" = @ownerId
              AND "Status" IN ('Open', 'ClaimPending');
            """;
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@ownerId", Clean(ownerZaloUserId, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> TransitionAsync(
        string offerId,
        string claimantZaloUserId,
        string fromStatus,
        string toStatus,
        bool clearClaim,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = clearClaim
            ? """
                UPDATE "ZaloOpenSlotOffers"
                SET "Status" = @toStatus, "ClaimantZaloUserId" = NULL, "ClaimantDisplayName" = NULL,
                    "ClaimMessageId" = NULL, "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
                WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId AND "Status" = @fromStatus;
                """
            : """
                UPDATE "ZaloOpenSlotOffers"
                SET "Status" = @toStatus, "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
                WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId AND "Status" = @fromStatus;
                """;
        Add(command, "@toStatus", toStatus);
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@claimantId", Clean(claimantZaloUserId, 100));
        Add(command, "@fromStatus", fromStatus);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> QueryAsync(
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        var rows = new List<ZaloOpenSlotOfferSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    private static async Task<ZaloOpenSlotOfferSnapshot?> LoadOwnerSessionAnyAsync(
        DbConnection connection,
        string groupId,
        string ownerZaloUserId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                   "Status", "Version", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloOpenSlotOffers"
            WHERE "GroupId" = @groupId AND "OwnerZaloUserId" = @ownerId AND "SessionId" = @sessionId
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@ownerId", ownerZaloUserId);
        Add(command, "@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
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
            var sql = isPostgres
                ? """
                    CREATE TABLE IF NOT EXISTS "ZaloOpenSlotOffers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOpenSlotOffers" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "OwnerZaloUserId" TEXT NOT NULL,
                        "OwnerDisplayName" TEXT NOT NULL,
                        "SessionId" TEXT NOT NULL,
                        "SessionName" TEXT NOT NULL,
                        "SourceMessageId" TEXT NULL,
                        "ClaimantZaloUserId" TEXT NULL,
                        "ClaimantDisplayName" TEXT NULL,
                        "ClaimMessageId" TEXT NULL,
                        "Status" TEXT NOT NULL,
                        "Version" INTEGER NOT NULL DEFAULT 1,
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "UX_ZaloOpenSlotOffers_OwnerSession" UNIQUE ("GroupId", "OwnerZaloUserId", "SessionId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_GroupStatus"
                    ON "ZaloOpenSlotOffers" ("GroupId", "Status");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_ClaimantStatus"
                    ON "ZaloOpenSlotOffers" ("GroupId", "ClaimantZaloUserId", "Status");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloOpenSlotOffers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOpenSlotOffers" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "OwnerZaloUserId" TEXT NOT NULL,
                        "OwnerDisplayName" TEXT NOT NULL,
                        "SessionId" TEXT NOT NULL,
                        "SessionName" TEXT NOT NULL,
                        "SourceMessageId" TEXT NULL,
                        "ClaimantZaloUserId" TEXT NULL,
                        "ClaimantDisplayName" TEXT NULL,
                        "ClaimMessageId" TEXT NULL,
                        "Status" TEXT NOT NULL,
                        "Version" INTEGER NOT NULL DEFAULT 1,
                        "ExpiresAt" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "UX_ZaloOpenSlotOffers_OwnerSession" UNIQUE ("GroupId", "OwnerZaloUserId", "SessionId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_GroupStatus"
                    ON "ZaloOpenSlotOffers" ("GroupId", "Status");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_ClaimantStatus"
                    ON "ZaloOpenSlotOffers" ("GroupId", "ClaimantZaloUserId", "Status");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static ZaloOpenSlotOfferSnapshot Read(DbDataReader reader)
    {
        _ = Enum.TryParse<ZaloOpenSlotOfferStatus>(reader.GetString(10), true, out var status);
        return new ZaloOpenSlotOfferSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7),
            NullableString(reader, 8), NullableString(reader, 9), status, Convert.ToInt32(reader.GetValue(11)),
            Timestamp(reader.GetValue(12)), Timestamp(reader.GetValue(13)), Timestamp(reader.GetValue(14)));
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
