using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds the bounded candidate universe available to semantic action planning.
/// IDs in this snapshot are candidates only; the deterministic validator and existing
/// domain services remain authoritative for every mutation.
/// </summary>
internal sealed class ZaloActionGroundingSnapshotBuilder(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<ZaloActionGroundingSnapshot> BuildAsync(
        string connectionId,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var readOnly = await new ZaloReadOnlyGroundingSnapshotBuilder(db).BuildAsync(
            connectionId,
            groupId,
            senderZaloUserId,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var localNow = now.ToOffset(VietnamOffset);
        var sessions = readOnly.Sessions
            .Select(session =>
            {
                var localStart = session.StartTime?.ToOffset(VietnamOffset);
                return new ZaloActionGroundingSession(
                    session.SessionId,
                    session.Name,
                    session.StartTime,
                    localStart?.ToString("yyyy-MM-dd"),
                    localStart?.ToString("HH:mm"),
                    session.Status,
                    session.Capacity,
                    session.PlayerCount,
                    groupId);
            })
            .ToArray();
        var sessionIds = sessions.Select(session => session.SessionId).ToHashSet(StringComparer.Ordinal);

        var senderId = Clean(senderZaloUserId, 100);
        var senderRows = readOnly.Members
            .Where(member =>
                member.IsPresent &&
                !string.IsNullOrWhiteSpace(member.ZaloUserId) &&
                string.Equals(Clean(member.ZaloUserId, 100), senderId, StringComparison.Ordinal))
            .ToArray();
        var senderMemberId = readOnly.Members
            .Where(member =>
                !string.IsNullOrWhiteSpace(member.ZaloUserId) &&
                string.Equals(Clean(member.ZaloUserId, 100), senderId, StringComparison.Ordinal))
            .Select(member => member.MemberId)
            .FirstOrDefault();
        var currentSender = new ZaloActionGroundingSender(
            senderId,
            senderMemberId,
            senderRows
                .Select(member => member.SessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        var store = new ZaloOpenSlotOfferStore(db);
        var claimable = await store.ListClaimableAsync(connectionId, groupId, senderId, cancellationToken);
        var owned = await store.ListOwnedActiveAsync(connectionId, groupId, senderId, cancellationToken);
        var pending = await store.LoadPendingClaimAsync(connectionId, groupId, senderId, cancellationToken);
        var offers = claimable
            .Concat(owned)
            .Concat(pending is null ? [] : [pending])
            .Where(offer => sessionIds.Contains(offer.SessionId))
            .GroupBy(offer => offer.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(offer => offer.UpdatedAt)
            .Take(24)
            .Select(offer => new ZaloActionGroundingOffer(
                offer.Id,
                Clean(offer.OwnerZaloUserId, 100),
                Clean(offer.OwnerDisplayName, 120),
                offer.SessionId,
                Clean(offer.SessionName, 120),
                CleanOptional(offer.SourceMessageId, 160),
                CleanOptional(offer.ClaimantZaloUserId, 100),
                CleanOptional(offer.ClaimantDisplayName, 120),
                offer.Status.ToString()))
            .ToArray();

        return new ZaloActionGroundingSnapshot(
            now,
            localNow,
            "Asia/Ho_Chi_Minh",
            sessions,
            readOnly.Members,
            offers,
            currentSender);
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
}
