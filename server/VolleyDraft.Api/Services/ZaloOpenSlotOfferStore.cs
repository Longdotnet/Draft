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
    string ConnectionId,
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
    DateTimeOffset? NextNudgeAt,
    DateTimeOffset? LastNudgeAt,
    int NudgeCount,
    DateTimeOffset? ClaimExpiresAt,
    string? ClosedReason,
    string? ReminderLeaseToken,
    DateTimeOffset? ReminderLeaseUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable, group-scoped coordination state for a member explicitly offering their
/// own slot. Rescue metadata lives beside the offer so a scheduler can safely
/// resurface forgotten offers without turning ambient chat into domain mutations.
/// </summary>
public sealed class ZaloOpenSlotOfferStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private const string Projection = """
        "Id", "ConnectionId", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
        "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
        "Status", "Version", "ExpiresAt", "NextNudgeAt", "LastNudgeAt", "NudgeCount", "ClaimExpiresAt",
        "ClosedReason", "ReminderLeaseToken", "ReminderLeaseUntil", "CreatedAt", "UpdatedAt"
        """;

    // Compatibility overload for older tests/callers. New production paths should
    // always pass the Zalo connection so equal group IDs on different accounts can
    // never see each other's offers.
    public Task<ZaloOpenSlotOfferSnapshot> OpenAsync(
        string groupId,
        string ownerZaloUserId,
        string ownerDisplayName,
        string sessionId,
        string sessionName,
        string? sourceMessageId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        OpenAsync(
            string.Empty,
            groupId,
            ownerZaloUserId,
            ownerDisplayName,
            sessionId,
            sessionName,
            sourceMessageId,
            expiresAt,
            DateTimeOffset.UtcNow.AddMinutes(45),
            cancellationToken);

    public async Task<ZaloOpenSlotOfferSnapshot> OpenAsync(
        string connectionId,
        string groupId,
        string ownerZaloUserId,
        string ownerDisplayName,
        string sessionId,
        string sessionName,
        string? sourceMessageId,
        DateTimeOffset expiresAt,
        DateTimeOffset? nextNudgeAt,
        CancellationToken cancellationToken = default)
    {
        connectionId = Clean(connectionId, 100);
        groupId = Clean(groupId, 100);
        ownerZaloUserId = Clean(ownerZaloUserId, 100);
        ownerDisplayName = Clean(ownerDisplayName, 160);
        sessionId = Clean(sessionId, 100);
        sessionName = Clean(sessionName, 160);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        if (groupId.Length == 0 || ownerZaloUserId.Length == 0 || ownerDisplayName.Length == 0 ||
            sessionId.Length == 0 || sessionName.Length == 0)
            throw new ArgumentException("Group, owner and session are required for an open slot offer.");
        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (nextNudgeAt is { } nudge && nudge >= expiresAt) nextNudgeAt = null;

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var existing = await LoadOwnerSessionAnyAsync(
            connection, groupId, ownerZaloUserId, sessionId, cancellationToken);

        if (existing is null)
        {
            var id = Guid.NewGuid().ToString("n");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO "ZaloOpenSlotOffers" (
                    "Id", "ConnectionId", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                    "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
                    "Status", "Version", "ExpiresAt", "NextNudgeAt", "LastNudgeAt", "NudgeCount", "ClaimExpiresAt",
                    "ClosedReason", "ReminderLeaseToken", "ReminderLeaseUntil", "CreatedAt", "UpdatedAt")
                VALUES (@id, @connectionId, @groupId, @ownerId, @ownerName, @sessionId, @sessionName,
                        @sourceMessageId, NULL, NULL, NULL, 'Open', 1, @expiresAt, @nextNudgeAt, NULL, 0, NULL,
                        NULL, NULL, NULL, @createdAt, @updatedAt);
                """;
            Add(insert, "@id", id);
            Add(insert, "@connectionId", connectionId);
            Add(insert, "@groupId", groupId);
            Add(insert, "@ownerId", ownerZaloUserId);
            Add(insert, "@ownerName", ownerDisplayName);
            Add(insert, "@sessionId", sessionId);
            Add(insert, "@sessionName", sessionName);
            Add(insert, "@sourceMessageId", sourceMessageId);
            Add(insert, "@expiresAt", expiresAt);
            Add(insert, "@nextNudgeAt", nextNudgeAt);
            Add(insert, "@createdAt", now);
            Add(insert, "@updatedAt", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return new ZaloOpenSlotOfferSnapshot(
                id, connectionId, groupId, ownerZaloUserId, ownerDisplayName, sessionId, sessionName,
                sourceMessageId, null, null, null, ZaloOpenSlotOfferStatus.Open, 1, expiresAt,
                nextNudgeAt, null, 0, null, null, null, null, now, now);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ConnectionId" = @connectionId,
                "OwnerDisplayName" = @ownerName,
                "SessionName" = @sessionName,
                "SourceMessageId" = @sourceMessageId,
                "ClaimantZaloUserId" = NULL,
                "ClaimantDisplayName" = NULL,
                "ClaimMessageId" = NULL,
                "Status" = 'Open',
                "Version" = @version,
                "ExpiresAt" = @expiresAt,
                "NextNudgeAt" = @nextNudgeAt,
                "LastNudgeAt" = NULL,
                "NudgeCount" = 0,
                "ClaimExpiresAt" = NULL,
                "ClosedReason" = NULL,
                "ReminderLeaseToken" = NULL,
                "ReminderLeaseUntil" = NULL,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id;
            """;
        var version = existing.Version + 1;
        Add(update, "@connectionId", connectionId);
        Add(update, "@ownerName", ownerDisplayName);
        Add(update, "@sessionName", sessionName);
        Add(update, "@sourceMessageId", sourceMessageId);
        Add(update, "@version", version);
        Add(update, "@expiresAt", expiresAt);
        Add(update, "@nextNudgeAt", nextNudgeAt);
        Add(update, "@updatedAt", now);
        Add(update, "@id", existing.Id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return existing with
        {
            ConnectionId = connectionId,
            OwnerDisplayName = ownerDisplayName,
            SessionName = sessionName,
            SourceMessageId = sourceMessageId,
            ClaimantZaloUserId = null,
            ClaimantDisplayName = null,
            ClaimMessageId = null,
            Status = ZaloOpenSlotOfferStatus.Open,
            Version = version,
            ExpiresAt = expiresAt,
            NextNudgeAt = nextNudgeAt,
            LastNudgeAt = null,
            NudgeCount = 0,
            ClaimExpiresAt = null,
            ClosedReason = null,
            ReminderLeaseToken = null,
            ReminderLeaseUntil = null,
            UpdatedAt = now
        };
    }

    public Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListClaimableAsync(
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        ListClaimableCoreAsync(null, groupId, claimantZaloUserId, cancellationToken);

    public Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListClaimableAsync(
        string connectionId,
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        ListClaimableCoreAsync(Clean(connectionId, 100), groupId, claimantZaloUserId, cancellationToken);

    private async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListClaimableCoreAsync(
        string? connectionId,
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken)
    {
        groupId = Clean(groupId, 100);
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        if (groupId.Length == 0 || claimantZaloUserId.Length == 0) return [];
        var sql = connectionId is null
            ? $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"GroupId\" = @groupId AND \"Status\" = 'Open';"
            : $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"ConnectionId\" = @connectionId AND \"GroupId\" = @groupId AND \"Status\" = 'Open';";
        var parameters = new List<(string, object?)> { ("@groupId", groupId) };
        if (connectionId is not null) parameters.Add(("@connectionId", connectionId));
        var rows = await QueryAsync(sql, parameters, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows
            .Where(item => item.ExpiresAt > now &&
                           !string.Equals(item.OwnerZaloUserId, claimantZaloUserId, StringComparison.Ordinal))
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
    }

    public Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListOwnedActiveAsync(
        string groupId,
        string ownerZaloUserId,
        CancellationToken cancellationToken = default) =>
        ListOwnedActiveCoreAsync(null, groupId, ownerZaloUserId, cancellationToken);

    public Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListOwnedActiveAsync(
        string connectionId,
        string groupId,
        string ownerZaloUserId,
        CancellationToken cancellationToken = default) =>
        ListOwnedActiveCoreAsync(Clean(connectionId, 100), groupId, ownerZaloUserId, cancellationToken);

    private async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListOwnedActiveCoreAsync(
        string? connectionId,
        string groupId,
        string ownerZaloUserId,
        CancellationToken cancellationToken)
    {
        groupId = Clean(groupId, 100);
        ownerZaloUserId = Clean(ownerZaloUserId, 100);
        if (groupId.Length == 0 || ownerZaloUserId.Length == 0) return [];
        var connectionClause = connectionId is null ? string.Empty : " AND \"ConnectionId\" = @connectionId";
        var parameters = new List<(string, object?)> { ("@groupId", groupId), ("@ownerId", ownerZaloUserId) };
        if (connectionId is not null) parameters.Add(("@connectionId", connectionId));
        var rows = await QueryAsync(
            $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"GroupId\" = @groupId AND \"OwnerZaloUserId\" = @ownerId{connectionClause} AND \"Status\" IN ('Open', 'ClaimPending', 'Applying');",
            parameters,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows.Where(item => item.ExpiresAt > now).OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public Task<ZaloOpenSlotOfferSnapshot?> LoadPendingClaimAsync(
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        LoadPendingClaimCoreAsync(null, groupId, claimantZaloUserId, cancellationToken);

    public Task<ZaloOpenSlotOfferSnapshot?> LoadPendingClaimAsync(
        string connectionId,
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken = default) =>
        LoadPendingClaimCoreAsync(Clean(connectionId, 100), groupId, claimantZaloUserId, cancellationToken);

    private async Task<ZaloOpenSlotOfferSnapshot?> LoadPendingClaimCoreAsync(
        string? connectionId,
        string groupId,
        string claimantZaloUserId,
        CancellationToken cancellationToken)
    {
        groupId = Clean(groupId, 100);
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        if (groupId.Length == 0 || claimantZaloUserId.Length == 0) return null;
        var connectionClause = connectionId is null ? string.Empty : " AND \"ConnectionId\" = @connectionId";
        var parameters = new List<(string, object?)> { ("@groupId", groupId), ("@claimantId", claimantZaloUserId) };
        if (connectionId is not null) parameters.Add(("@connectionId", connectionId));
        var rows = await QueryAsync(
            $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"GroupId\" = @groupId AND \"ClaimantZaloUserId\" = @claimantId{connectionClause} AND \"Status\" IN ('ClaimPending', 'Applying');",
            parameters,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return rows.Where(item => item.ExpiresAt > now).OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
    }

    public Task<bool> TryClaimAsync(
        ZaloOpenSlotOfferSnapshot offer,
        string claimantZaloUserId,
        string claimantDisplayName,
        string? claimMessageId,
        CancellationToken cancellationToken = default) =>
        TryClaimAsync(
            offer,
            claimantZaloUserId,
            claimantDisplayName,
            claimMessageId,
            Min(offer.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(15)),
            cancellationToken);

    public async Task<bool> TryClaimAsync(
        ZaloOpenSlotOfferSnapshot offer,
        string claimantZaloUserId,
        string claimantDisplayName,
        string? claimMessageId,
        DateTimeOffset claimExpiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (offer.Status != ZaloOpenSlotOfferStatus.Open || offer.ExpiresAt <= now) return false;
        claimantZaloUserId = Clean(claimantZaloUserId, 100);
        claimantDisplayName = Clean(claimantDisplayName, 160);
        claimMessageId = CleanOptional(claimMessageId, 160);
        if (claimantZaloUserId.Length == 0 || claimantDisplayName.Length == 0 ||
            string.Equals(claimantZaloUserId, offer.OwnerZaloUserId, StringComparison.Ordinal))
            return false;
        claimExpiresAt = Min(offer.ExpiresAt, claimExpiresAt);
        if (claimExpiresAt <= now) return false;

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ClaimantZaloUserId" = @claimantId,
                "ClaimantDisplayName" = @claimantName,
                "ClaimMessageId" = @claimMessageId,
                "Status" = 'ClaimPending',
                "Version" = "Version" + 1,
                "ClaimExpiresAt" = @claimExpiresAt,
                "NextNudgeAt" = NULL,
                "ReminderLeaseToken" = NULL,
                "ReminderLeaseUntil" = NULL,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "Status" = 'Open' AND "Version" = @version;
            """;
        Add(command, "@claimantId", claimantZaloUserId);
        Add(command, "@claimantName", claimantDisplayName);
        Add(command, "@claimMessageId", claimMessageId);
        Add(command, "@claimExpiresAt", claimExpiresAt);
        Add(command, "@updatedAt", now);
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
        TransitionAsync(offerId, claimantZaloUserId, "Applying", "Completed", clearClaim: false, cancellationToken, "Completed");

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
                "ClaimMessageId" = NULL, "ClaimExpiresAt" = NULL, "NextNudgeAt" = @nextNudgeAt,
                "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId
              AND "Status" IN ('ClaimPending', 'Applying');
            """;
        var now = DateTimeOffset.UtcNow;
        Add(command, "@nextNudgeAt", MinNullable(DateTimeOffset.UtcNow.AddMinutes(10), await LoadExpiresAtAsync(connection, offerId, cancellationToken)));
        Add(command, "@updatedAt", now);
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
            SET "Status" = 'Cancelled', "ClosedReason" = 'OwnerCancelled', "ClaimExpiresAt" = NULL,
                "NextNudgeAt" = NULL, "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "OwnerZaloUserId" = @ownerId
              AND "Status" IN ('Open', 'ClaimPending');
            """;
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@ownerId", Clean(ownerZaloUserId, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListDueRescueAsync(
        DateTimeOffset now,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"Status\" IN ('Open', 'ClaimPending', 'Applying') ORDER BY \"UpdatedAt\" ASC;",
            [],
            cancellationToken);
        return rows
            .Where(item =>
                item.ExpiresAt <= now ||
                item.Status == ZaloOpenSlotOfferStatus.Open && item.NextNudgeAt is { } nudgeAt && nudgeAt <= now ||
                item.Status == ZaloOpenSlotOfferStatus.ClaimPending && item.ClaimExpiresAt is { } claimAt && claimAt <= now)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public async Task<bool> TryAcquireReminderLeaseAsync(
        ZaloOpenSlotOfferSnapshot offer,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (offer.ReminderLeaseUntil is { } leaseUntil && leaseUntil > now) return false;
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ReminderLeaseToken" = @leaseToken, "ReminderLeaseUntil" = @leaseUntil
            WHERE "Id" = @id AND "Version" = @version
              AND "Status" = @status
              AND ("ReminderLeaseUntil" IS NULL OR "ReminderLeaseUntil" < @now);
            """;
        Add(command, "@leaseToken", Clean(leaseToken, 100));
        Add(command, "@leaseUntil", now.Add(leaseDuration));
        Add(command, "@id", offer.Id);
        Add(command, "@version", offer.Version);
        Add(command, "@status", offer.Status.ToString());
        Add(command, "@now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkNudgedAsync(
        string offerId,
        string leaseToken,
        DateTimeOffset sentAt,
        DateTimeOffset? nextNudgeAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "LastNudgeAt" = @sentAt, "NextNudgeAt" = @nextNudgeAt,
                "NudgeCount" = "NudgeCount" + 1, "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "Version" = "Version" + 1, "UpdatedAt" = @sentAt
            WHERE "Id" = @id AND "ReminderLeaseToken" = @leaseToken AND "Status" = 'Open';
            """;
        Add(command, "@sentAt", sentAt);
        Add(command, "@nextNudgeAt", nextNudgeAt);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@leaseToken", Clean(leaseToken, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> ReleaseTimedOutClaimAsync(
        string offerId,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset? nextNudgeAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "Status" = 'Open', "ClaimantZaloUserId" = NULL, "ClaimantDisplayName" = NULL,
                "ClaimMessageId" = NULL, "ClaimExpiresAt" = NULL, "NextNudgeAt" = @nextNudgeAt,
                "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "ReminderLeaseToken" = @leaseToken AND "Status" = 'ClaimPending';
            """;
        Add(command, "@nextNudgeAt", nextNudgeAt);
        Add(command, "@updatedAt", now);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@leaseToken", Clean(leaseToken, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> CloseFromReminderAsync(
        string offerId,
        string leaseToken,
        ZaloOpenSlotOfferStatus status,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (status is not ZaloOpenSlotOfferStatus.Expired and not ZaloOpenSlotOfferStatus.Cancelled and not ZaloOpenSlotOfferStatus.Completed)
            throw new ArgumentOutOfRangeException(nameof(status));
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "Status" = @status, "ClosedReason" = @reason, "ClaimExpiresAt" = NULL,
                "NextNudgeAt" = NULL, "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "ReminderLeaseToken" = @leaseToken
              AND "Status" IN ('Open', 'ClaimPending', 'Applying');
            """;
        Add(command, "@status", status.ToString());
        Add(command, "@reason", Clean(reason, 160));
        Add(command, "@updatedAt", now);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@leaseToken", Clean(leaseToken, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseReminderLeaseAsync(
        string offerId,
        string leaseToken,
        DateTimeOffset? retryAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                "NextNudgeAt" = CASE WHEN "Status" = 'Open' THEN @retryAt ELSE "NextNudgeAt" END
            WHERE "Id" = @id AND "ReminderLeaseToken" = @leaseToken;
            """;
        Add(command, "@retryAt", retryAt);
        Add(command, "@id", Clean(offerId, 100));
        Add(command, "@leaseToken", Clean(leaseToken, 100));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> TransitionAsync(
        string offerId,
        string claimantZaloUserId,
        string fromStatus,
        string toStatus,
        bool clearClaim,
        CancellationToken cancellationToken,
        string? closedReason = null)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = clearClaim
            ? """
                UPDATE "ZaloOpenSlotOffers"
                SET "Status" = @toStatus, "ClaimantZaloUserId" = NULL, "ClaimantDisplayName" = NULL,
                    "ClaimMessageId" = NULL, "ClaimExpiresAt" = NULL, "ClosedReason" = @closedReason,
                    "NextNudgeAt" = NULL, "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                    "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
                WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId AND "Status" = @fromStatus;
                """
            : """
                UPDATE "ZaloOpenSlotOffers"
                SET "Status" = @toStatus, "ClaimExpiresAt" = NULL, "ClosedReason" = @closedReason,
                    "NextNudgeAt" = NULL, "ReminderLeaseToken" = NULL, "ReminderLeaseUntil" = NULL,
                    "Version" = "Version" + 1, "UpdatedAt" = @updatedAt
                WHERE "Id" = @id AND "ClaimantZaloUserId" = @claimantId AND "Status" = @fromStatus;
                """;
        Add(command, "@toStatus", toStatus);
        Add(command, "@closedReason", CleanOptional(closedReason, 160));
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
        command.CommandText = $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"GroupId\" = @groupId AND \"OwnerZaloUserId\" = @ownerId AND \"SessionId\" = @sessionId LIMIT 1;";
        Add(command, "@groupId", groupId);
        Add(command, "@ownerId", ownerZaloUserId);
        Add(command, "@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<DateTimeOffset?> LoadExpiresAtAsync(
        DbConnection connection,
        string offerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"ExpiresAt\" FROM \"ZaloOpenSlotOffers\" WHERE \"Id\" = @id LIMIT 1;";
        Add(command, "@id", Clean(offerId, 100));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value is DBNull ? null : Timestamp(value);
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
            var createSql = isPostgres
                ? """
                    CREATE TABLE IF NOT EXISTS "ZaloOpenSlotOffers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOpenSlotOffers" PRIMARY KEY,
                        "ConnectionId" TEXT NOT NULL DEFAULT '',
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
                        "NextNudgeAt" timestamp with time zone NULL,
                        "LastNudgeAt" timestamp with time zone NULL,
                        "NudgeCount" INTEGER NOT NULL DEFAULT 0,
                        "ClaimExpiresAt" timestamp with time zone NULL,
                        "ClosedReason" TEXT NULL,
                        "ReminderLeaseToken" TEXT NULL,
                        "ReminderLeaseUntil" timestamp with time zone NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "UX_ZaloOpenSlotOffers_OwnerSession" UNIQUE ("GroupId", "OwnerZaloUserId", "SessionId")
                    );
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloOpenSlotOffers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloOpenSlotOffers" PRIMARY KEY,
                        "ConnectionId" TEXT NOT NULL DEFAULT '',
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
                        "NextNudgeAt" TEXT NULL,
                        "LastNudgeAt" TEXT NULL,
                        "NudgeCount" INTEGER NOT NULL DEFAULT 0,
                        "ClaimExpiresAt" TEXT NULL,
                        "ClosedReason" TEXT NULL,
                        "ReminderLeaseToken" TEXT NULL,
                        "ReminderLeaseUntil" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "UX_ZaloOpenSlotOffers_OwnerSession" UNIQUE ("GroupId", "OwnerZaloUserId", "SessionId")
                    );
                    """;
            await db.Database.ExecuteSqlRawAsync(createSql, cancellationToken);

            if (isPostgres)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "ConnectionId" TEXT NOT NULL DEFAULT '';
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "NextNudgeAt" timestamp with time zone NULL;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "LastNudgeAt" timestamp with time zone NULL;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "NudgeCount" INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "ClaimExpiresAt" timestamp with time zone NULL;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "ClosedReason" TEXT NULL;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "ReminderLeaseToken" TEXT NULL;
                    ALTER TABLE "ZaloOpenSlotOffers" ADD COLUMN IF NOT EXISTS "ReminderLeaseUntil" timestamp with time zone NULL;
                    """, cancellationToken);
            }
            else
            {
                await EnsureSqliteColumnAsync("ConnectionId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
                await EnsureSqliteColumnAsync("NextNudgeAt", "TEXT NULL", cancellationToken);
                await EnsureSqliteColumnAsync("LastNudgeAt", "TEXT NULL", cancellationToken);
                await EnsureSqliteColumnAsync("NudgeCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await EnsureSqliteColumnAsync("ClaimExpiresAt", "TEXT NULL", cancellationToken);
                await EnsureSqliteColumnAsync("ClosedReason", "TEXT NULL", cancellationToken);
                await EnsureSqliteColumnAsync("ReminderLeaseToken", "TEXT NULL", cancellationToken);
                await EnsureSqliteColumnAsync("ReminderLeaseUntil", "TEXT NULL", cancellationToken);
            }

            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_GroupStatus"
                ON "ZaloOpenSlotOffers" ("GroupId", "Status");
                CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_ClaimantStatus"
                ON "ZaloOpenSlotOffers" ("GroupId", "ClaimantZaloUserId", "Status");
                CREATE INDEX IF NOT EXISTS "IX_ZaloOpenSlotOffers_ConnectionGroupStatus"
                ON "ZaloOpenSlotOffers" ("ConnectionId", "GroupId", "Status");
                """, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private async Task EnsureSqliteColumnAsync(
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(\"ZaloOpenSlotOffers\");";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(Convert.ToString(reader.GetValue(1)), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"ZaloOpenSlotOffers\" ADD COLUMN \"{columnName}\" {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ZaloOpenSlotOfferSnapshot Read(DbDataReader reader)
    {
        _ = Enum.TryParse<ZaloOpenSlotOfferStatus>(reader.GetString(11), true, out var status);
        return new ZaloOpenSlotOfferSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8),
            NullableString(reader, 9), NullableString(reader, 10), status, Convert.ToInt32(reader.GetValue(12)),
            Timestamp(reader.GetValue(13)), NullableTimestamp(reader, 14), NullableTimestamp(reader, 15),
            Convert.ToInt32(reader.GetValue(16)), NullableTimestamp(reader, 17), NullableString(reader, 18),
            NullableString(reader, 19), NullableTimestamp(reader, 20), Timestamp(reader.GetValue(21)), Timestamp(reader.GetValue(22)));
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

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static DateTimeOffset? MinNullable(DateTimeOffset candidate, DateTimeOffset? upperBound) =>
        upperBound is null || candidate < upperBound.Value ? candidate : null;

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
