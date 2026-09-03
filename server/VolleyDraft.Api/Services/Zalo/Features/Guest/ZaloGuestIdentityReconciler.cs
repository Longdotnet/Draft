using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Prevents a named outside guest from counting twice if that same person later joins
/// the Zalo group and appears in the linked poll. Reconciliation is deliberately
/// conservative: exact normalized name, exactly one active guest reservation, exactly
/// one poll-backed player, and never a generated "Bạn của ..." placeholder.
/// </summary>
internal sealed class ZaloGuestIdentityReconciler(VolleyDraftDbContext db)
{
    internal async Task<int> ReconcileAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);

        var reservations = await db.ZaloGuestReservations
            .Where(item => item.SessionId == sessionId &&
                           item.Status == ZaloGuestReservationStatus.Active &&
                           item.SessionPlayerId != null)
            .ToListAsync(cancellationToken);
        if (reservations.Count == 0) return 0;

        var manualPlayerIds = reservations
            .Select(item => item.SessionPlayerId!)
            .ToHashSet(StringComparer.Ordinal);
        var manualPlayers = await db.SessionPlayers
            .Where(item => manualPlayerIds.Contains(item.Id) && item.IsPresent)
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, cancellationToken);
        var pollPlayers = await db.SessionPlayers
            .Where(item => item.SessionId == sessionId &&
                           item.IsPresent &&
                           item.SourcePollId != null &&
                           item.PlayerProfileId != null)
            .ToListAsync(cancellationToken);
        if (pollPlayers.Count == 0) return 0;

        var reservationGroups = reservations
            .Where(item => !IsGeneratedPlaceholder(item.DisplayName))
            .GroupBy(item => ZaloBotIntelligence.Normalize(item.DisplayName), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var pollGroups = pollPlayers
            .GroupBy(item => ZaloBotIntelligence.Normalize(item.DisplayName), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var changed = 0;
        foreach (var (normalizedName, reservation) in reservationGroups)
        {
            if (!pollGroups.TryGetValue(normalizedName, out var pollPlayer)) continue;
            if (string.Equals(reservation.SessionPlayerId, pollPlayer.Id, StringComparison.Ordinal)) continue;
            if (!manualPlayers.TryGetValue(reservation.SessionPlayerId!, out var manualPlayer)) continue;

            manualPlayer.IsPresent = false;
            reservation.SessionPlayerId = pollPlayer.Id;
            reservation.Status = ZaloGuestReservationStatus.Linked;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            changed += 1;
        }

        if (changed > 0) await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private static bool IsGeneratedPlaceholder(string displayName) =>
        ZaloBotIntelligence.Normalize(displayName).StartsWith("ban cua ", StringComparison.Ordinal);
}
