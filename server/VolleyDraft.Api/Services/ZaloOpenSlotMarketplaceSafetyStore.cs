using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloOpenSlotOpenDisposition
{
    Opened,
    Refreshed,
    ClaimPreserved,
    ApplyingPreserved
}

internal sealed record ZaloOpenSlotOpenResult(
    ZaloOpenSlotOfferSnapshot Offer,
    ZaloOpenSlotOpenDisposition Disposition);

/// <summary>
/// Production-path safety around the durable open-slot store.
///
/// The legacy store intentionally exposes low-level transitions for tests and repair
/// flows. This helper adds the stronger marketplace invariants used by ambient chat:
/// a repeated owner announcement must never erase somebody else's live reservation,
/// and stale Applying rows may only be surfaced for canonical-state recovery.
/// </summary>
internal sealed class ZaloOpenSlotMarketplaceSafetyStore(VolleyDraftDbContext db)
{
    private const string Projection = """
        "Id", "ConnectionId", "GroupId", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
        "SourceMessageId", "ClaimantZaloUserId", "ClaimantDisplayName", "ClaimMessageId",
        "Status", "Version", "ExpiresAt", "NextNudgeAt", "LastNudgeAt", "NudgeCount", "ClaimExpiresAt",
        "ClosedReason", "ReminderLeaseToken", "ReminderLeaseUntil", "CreatedAt", "UpdatedAt"
        """;

    private readonly ZaloOpenSlotOfferStore store = new(db);

    public async Task<ZaloOpenSlotOpenResult> OpenOrRefreshAsync(
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
        var now = DateTimeOffset.UtcNow;
        var active = (await store.ListOwnedActiveAsync(
                connectionId,
                groupId,
                ownerZaloUserId,
                cancellationToken))
            .FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));

        if (active?.Status == ZaloOpenSlotOfferStatus.Applying)
            return new(active, ZaloOpenSlotOpenDisposition.ApplyingPreserved);

        if (active?.Status == ZaloOpenSlotOfferStatus.ClaimPending)
        {
            if (active.ClaimExpiresAt is null || active.ClaimExpiresAt > now)
                return new(active, ZaloOpenSlotOpenDisposition.ClaimPreserved);

            if (!string.IsNullOrWhiteSpace(active.ClaimantZaloUserId))
                await store.ReleaseClaimAsync(active.Id, active.ClaimantZaloUserId, cancellationToken);

            active = (await store.ListOwnedActiveAsync(
                    connectionId,
                    groupId,
                    ownerZaloUserId,
                    cancellationToken))
                .FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        }

        if (active?.Status == ZaloOpenSlotOfferStatus.Open)
        {
            var refreshed = await TryRefreshOpenAsync(
                active,
                connectionId,
                ownerDisplayName,
                sessionName,
                sourceMessageId,
                expiresAt,
                nextNudgeAt,
                now,
                cancellationToken);
            if (refreshed is not null)
                return new(refreshed, ZaloOpenSlotOpenDisposition.Refreshed);

            // The only expected reason for the CAS to lose is that a claimant reserved
            // the offer between our read and refresh. Re-read instead of calling the
            // legacy OpenAsync, because OpenAsync would intentionally reset the row.
            var raced = (await store.ListOwnedActiveAsync(
                    connectionId,
                    groupId,
                    ownerZaloUserId,
                    cancellationToken))
                .FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
            if (raced?.Status == ZaloOpenSlotOfferStatus.ClaimPending)
                return new(raced, ZaloOpenSlotOpenDisposition.ClaimPreserved);
            if (raced?.Status == ZaloOpenSlotOfferStatus.Applying)
                return new(raced, ZaloOpenSlotOpenDisposition.ApplyingPreserved);
            if (raced?.Status == ZaloOpenSlotOfferStatus.Open)
                return new(raced, ZaloOpenSlotOpenDisposition.Refreshed);
        }

        var opened = await store.OpenAsync(
            connectionId,
            groupId,
            ownerZaloUserId,
            ownerDisplayName,
            sessionId,
            sessionName,
            sourceMessageId,
            expiresAt,
            nextNudgeAt,
            cancellationToken);
        return new(opened, ZaloOpenSlotOpenDisposition.Opened);
    }

    public async Task<IReadOnlyList<ZaloOpenSlotOfferSnapshot>> ListStaleApplyingAsync(
        DateTimeOffset staleBefore,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        // Force the shared store to bootstrap/migrate the raw table first.
        _ = await store.ListClaimableAsync("__schema__", "__schema__", "__schema__", cancellationToken);

        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Projection} FROM \"ZaloOpenSlotOffers\" WHERE \"Status\" = 'Applying' ORDER BY \"UpdatedAt\" ASC;";
        var rows = new List<ZaloOpenSlotOfferSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows
            .Where(item => item.UpdatedAt <= staleBefore)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    private async Task<ZaloOpenSlotOfferSnapshot?> TryRefreshOpenAsync(
        ZaloOpenSlotOfferSnapshot active,
        string connectionId,
        string ownerDisplayName,
        string sessionName,
        string? sourceMessageId,
        DateTimeOffset expiresAt,
        DateTimeOffset? nextNudgeAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloOpenSlotOffers"
            SET "ConnectionId" = @connectionId,
                "OwnerDisplayName" = @ownerName,
                "SessionName" = @sessionName,
                "SourceMessageId" = @sourceMessageId,
                "ExpiresAt" = @expiresAt,
                "NextNudgeAt" = @nextNudgeAt,
                "ClosedReason" = NULL,
                "Version" = "Version" + 1,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id AND "Status" = 'Open' AND "Version" = @version;
            """;
        Add(command, "@connectionId", Clean(connectionId, 100));
        Add(command, "@ownerName", Clean(ownerDisplayName, 160));
        Add(command, "@sessionName", Clean(sessionName, 160));
        Add(command, "@sourceMessageId", CleanOptional(sourceMessageId, 160));
        Add(command, "@expiresAt", expiresAt);
        Add(command, "@nextNudgeAt", nextNudgeAt is { } nudge && nudge < expiresAt ? nudge : null);
        Add(command, "@updatedAt", now);
        Add(command, "@id", active.Id);
        Add(command, "@version", active.Version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return null;

        return active with
        {
            ConnectionId = Clean(connectionId, 100),
            OwnerDisplayName = Clean(ownerDisplayName, 160),
            SessionName = Clean(sessionName, 160),
            SourceMessageId = CleanOptional(sourceMessageId, 160),
            Version = active.Version + 1,
            ExpiresAt = expiresAt,
            NextNudgeAt = nextNudgeAt is { } next && next < expiresAt ? next : null,
            ClosedReason = null,
            UpdatedAt = now
        };
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
