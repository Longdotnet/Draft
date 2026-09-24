using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloRecentJoinMember(
    string ZaloUserId,
    string DisplayName,
    DateTimeOffset JoinedAt,
    bool IsRejoin);

public sealed record ZaloRecentJoinResult(
    IReadOnlyList<ZaloRecentJoinMember> Members,
    int UnknownCurrentMemberCount,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    bool CoverageIsComplete);

public sealed record ZaloMemberJoinDateResult(
    string ZaloUserId,
    string DisplayName,
    DateTimeOffset? JoinedAt,
    bool IsRejoin,
    bool HasVerifiedEvidence);

public sealed class ZaloMembershipHistoryService(
    VolleyDraftDbContext db,
    ILogger<ZaloMembershipHistoryService> logger)
{
    public async Task<bool> RecordProviderEventAsync(
        ZaloMembershipChangedEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (incoming.EventType is not ("join" or "leave" or "remove_member"))
            return false;

        var groupId = NormalizeId(incoming.GroupId);
        var accountId = NormalizeId(incoming.AccountId);
        var memberIds = (incoming.MemberIds ?? [])
            .Select(NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToList();
        var occurredAt = FromUnixMs(incoming.OccurredAtUnixMs) ?? DateTimeOffset.MinValue;
        var now = DateTimeOffset.UtcNow;
        if (groupId.Length == 0 || accountId.Length == 0 || memberIds.Count == 0 ||
            string.IsNullOrWhiteSpace(incoming.EventId) ||
            occurredAt == DateTimeOffset.MinValue || occurredAt > now.AddMinutes(5))
        {
            // Missing provider event time cannot be substituted with observation time:
            // it would make an unverified member appear to have joined today.
            logger.LogWarning("Ignored membership event with missing identity or untrusted event timestamp Account={AccountId} Group={GroupId}", accountId, groupId);
            return false;
        }

        var accountConnectionIds = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.AccountZaloId == accountId &&
                                 connection.Status == ZaloConnectionStatus.Connected)
            .Select(connection => connection.Id)
            .ToListAsync(cancellationToken);
        // Durable tracked groups own membership observations even if every linked
        // session is finished, deleted, or has its bot temporarily disabled.
        var trackedGroups = await new ZaloAutoSessionSettingsStore(db).GetAllAsync(cancellationToken);
        var connectionIds = trackedGroups
            .Where(group => group.GroupId == groupId && accountConnectionIds.Contains(group.ZaloConnectionId))
            .Select(group => group.ZaloConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (connectionIds.Count == 0)
        {
            // Older installations may not have seeded ZaloTrackedGroups yet.
            connectionIds = await db.MatchSessions
                .AsNoTracking()
                .Where(session => session.ZaloGroupId == groupId && session.BotEnabled &&
                                  session.ZaloConnectionId != null &&
                                  accountConnectionIds.Contains(session.ZaloConnectionId))
                .Select(session => session.ZaloConnectionId!)
                .Distinct()
                .ToListAsync(cancellationToken);
        }
        if (connectionIds.Count != 1)
        {
            logger.LogWarning(
                "Ignored membership event because account/group did not resolve to one connection Account={AccountId} Group={GroupId} Candidates={Count}",
                accountId,
                groupId,
                connectionIds.Count);
            return false;
        }

        var connectionId = connectionIds[0];
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var memberId in memberIds)
        {
            var sourceEventId = $"{incoming.EventId}:{memberId}";
            var membershipChanged = false;
            var current = await db.ZaloGroupMembershipPeriods
                .Where(period =>
                    period.ZaloConnectionId == connectionId &&
                    period.GroupId == groupId &&
                    period.ZaloUserId == memberId &&
                    period.IsCurrentPeriod)
                .OrderByDescending(period => period.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            // Compare source event timestamps across closed periods before applying
            // a delayed webhook. A late leave cannot close a newer rejoin, and a
            // late join cannot resurrect a member after a later departure.
            var previousLeftAt = await db.ZaloGroupMembershipPeriods
                .AsNoTracking()
                .Where(period => period.ZaloConnectionId == connectionId &&
                                 period.GroupId == groupId &&
                                 period.ZaloUserId == memberId &&
                                 period.LeftAt != null)
                .MaxAsync(period => period.LeftAt, cancellationToken);

            if (incoming.EventType == "join")
            {
                var duplicate = await db.ZaloGroupMembershipPeriods
                    .AsNoTracking()
                    .AnyAsync(period =>
                        period.ZaloConnectionId == connectionId &&
                        period.GroupId == groupId &&
                        period.SourceEventId == sourceEventId,
                        cancellationToken);
                if (duplicate || (previousLeftAt is not null && occurredAt <= previousLeftAt))
                    continue;

                if (current is not null && current.EvidenceKind == ZaloMembershipEvidenceKind.ObservedOnly)
                {
                    var hadOlderPeriod = await db.ZaloGroupMembershipPeriods
                        .AsNoTracking()
                        .AnyAsync(period =>
                            period.ZaloConnectionId == connectionId &&
                            period.GroupId == groupId &&
                            period.ZaloUserId == memberId &&
                            period.Id != current.Id,
                            cancellationToken);
                    current.JoinedAt = occurredAt;
                    current.EvidenceKind = hadOlderPeriod
                        ? ZaloMembershipEvidenceKind.ProviderRejoinEvent
                        : ZaloMembershipEvidenceKind.ProviderJoinEvent;
                    current.SourceEventId = sourceEventId;
                    current.LastObservedAt = now;
                    current.UpdatedAt = now;
                    membershipChanged = true;
                }
                else if (current is null)
                {
                    var hadOlderPeriod = await db.ZaloGroupMembershipPeriods
                        .AsNoTracking()
                        .AnyAsync(period =>
                            period.ZaloConnectionId == connectionId &&
                            period.GroupId == groupId &&
                            period.ZaloUserId == memberId,
                            cancellationToken);
                    db.ZaloGroupMembershipPeriods.Add(new ZaloGroupMembershipPeriod
                    {
                        ZaloConnectionId = connectionId,
                        GroupId = groupId,
                        ZaloUserId = memberId,
                        JoinedAt = occurredAt,
                        FirstObservedAt = now,
                        LastObservedAt = now,
                        EvidenceKind = hadOlderPeriod
                            ? ZaloMembershipEvidenceKind.ProviderRejoinEvent
                            : ZaloMembershipEvidenceKind.ProviderJoinEvent,
                        SourceEventId = sourceEventId,
                        IsCurrentPeriod = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    membershipChanged = true;
                }
                else
                {
                    // A second join while a verified current period is open is out-of-order or
                    // duplicated provider data. Keep the established period instead of inventing
                    // another membership lifecycle.
                    logger.LogInformation(
                        "Ignored overlapping Zalo join event Connection={ConnectionId} Group={GroupId} Member={MemberId}",
                        connectionId,
                        groupId,
                        memberId);
                }
            }
            else
            {
                var duplicateLeave = await db.ZaloGroupMembershipPeriods
                    .AsNoTracking()
                    .AnyAsync(period =>
                        period.ZaloConnectionId == connectionId &&
                        period.GroupId == groupId &&
                        period.ZaloUserId == memberId &&
                        period.LeftAt == occurredAt,
                        cancellationToken);
                if (duplicateLeave ||
                    (previousLeftAt is not null && occurredAt <= previousLeftAt) ||
                    (current?.JoinedAt is { } joinedAt && occurredAt <= joinedAt) ||
                    (current is { EvidenceKind: ZaloMembershipEvidenceKind.ObservedOnly } &&
                     occurredAt < current.FirstObservedAt))
                    continue;

                if (current is null)
                {
                    db.ZaloGroupMembershipPeriods.Add(new ZaloGroupMembershipPeriod
                    {
                        ZaloConnectionId = connectionId,
                        GroupId = groupId,
                        ZaloUserId = memberId,
                        JoinedAt = null,
                        LeftAt = occurredAt,
                        FirstObservedAt = now,
                        LastObservedAt = now,
                        EvidenceKind = ZaloMembershipEvidenceKind.ObservedOnly,
                        SourceEventId = sourceEventId,
                        IsCurrentPeriod = false,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    membershipChanged = true;
                }
                else
                {
                    current.LeftAt = occurredAt;
                    current.IsCurrentPeriod = false;
                    current.LastObservedAt = now;
                    current.UpdatedAt = now;
                    membershipChanged = true;
                }
            }

            // The directory and event ledger share current-member semantics. Update
            // an existing profile only when the source event is at least as fresh
            // as its last directory snapshot, so delayed events cannot regress it.
            if (membershipChanged)
            {
                var profile = await db.ZaloGroupMembers.SingleOrDefaultAsync(member =>
                    member.ZaloConnectionId == connectionId && member.GroupId == groupId &&
                    member.ZaloUserId == memberId, cancellationToken);
                if (profile is not null && occurredAt >= profile.LastSyncedAt)
                {
                    profile.IsCurrentMember = incoming.EventType == "join";
                    profile.LeftAt = incoming.EventType == "join" ? null : occurredAt;
                    if (incoming.EventType == "join") profile.LastSeenAt = now;
                    profile.UpdatedAt = now;
                }
            }
        }

        await TouchCoverageAsync(connectionId, groupId, occurredAt, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ZaloRecentJoinResult> QueryRecentAsync(
        string connectionId,
        string groupId,
        int days,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days), days, "Recent-join window must be between 1 and 3650 days.");
        var windowStart = snapshotAt.AddDays(-days);

        var periods = await db.ZaloGroupMembershipPeriods
            .AsNoTracking()
            .Where(period =>
                period.ZaloConnectionId == connectionId &&
                period.GroupId == groupId &&
                period.IsCurrentPeriod)
            .ToListAsync(cancellationToken);
        var currentMembers = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member =>
                member.ZaloConnectionId == connectionId &&
                member.GroupId == groupId &&
                member.IsCurrentMember)
            .ToListAsync(cancellationToken);
        var names = currentMembers.ToDictionary(member => member.ZaloUserId, member => member.DisplayName, StringComparer.Ordinal);

        var verifiedCurrent = periods
            .Where(period => period.JoinedAt is not null &&
                             period.EvidenceKind != ZaloMembershipEvidenceKind.ObservedOnly)
            .ToList();
        var members = verifiedCurrent
            .Where(period => period.JoinedAt >= windowStart && period.JoinedAt <= snapshotAt)
            .OrderByDescending(period => period.JoinedAt)
            .Select(period => new ZaloRecentJoinMember(
                period.ZaloUserId,
                names.GetValueOrDefault(period.ZaloUserId) ?? $"Zalo {period.ZaloUserId}",
                period.JoinedAt!.Value,
                period.EvidenceKind == ZaloMembershipEvidenceKind.ProviderRejoinEvent))
            .ToList();

        var verifiedIds = verifiedCurrent.Select(period => period.ZaloUserId).ToHashSet(StringComparer.Ordinal);
        var unknownCount = currentMembers.Count(member => !verifiedIds.Contains(member.ZaloUserId));
        var coverage = await db.ZaloMembershipCoverages.AsNoTracking().SingleOrDefaultAsync(
            item => item.ZaloConnectionId == connectionId && item.GroupId == groupId,
            cancellationToken);
        // Realtime observation alone does not prove there were no listener gaps.
        // Only an explicitly verified historical source may claim complete membership coverage.
        var complete = coverage?.HasCompleteHistoricalJoinEvents == true;

        return new ZaloRecentJoinResult(members, unknownCount, windowStart, snapshotAt, complete);
    }

    public async Task<ZaloMemberJoinDateResult?> GetJoinDateAsync(
        string connectionId,
        string groupId,
        string zaloUserId,
        CancellationToken cancellationToken = default)
    {
        zaloUserId = NormalizeId(zaloUserId);
        var member = await db.ZaloGroupMembers.AsNoTracking().SingleOrDefaultAsync(
            item => item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    item.ZaloUserId == zaloUserId,
            cancellationToken);
        var period = await db.ZaloGroupMembershipPeriods.AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.ZaloUserId == zaloUserId &&
                item.IsCurrentPeriod)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (member is null && period is null) return null;
        var verified = period?.JoinedAt is not null &&
                       period.EvidenceKind != ZaloMembershipEvidenceKind.ObservedOnly;
        return new ZaloMemberJoinDateResult(
            zaloUserId,
            member?.DisplayName ?? $"Zalo {zaloUserId}",
            verified ? period!.JoinedAt : null,
            verified && period!.EvidenceKind == ZaloMembershipEvidenceKind.ProviderRejoinEvent,
            verified);
    }

    internal async Task<IReadOnlySet<string>> ObserveDirectoryAsync(
        string connectionId,
        string groupId,
        IReadOnlyCollection<string> returnedMemberIds,
        bool directoryIsComplete,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default,
        DateTimeOffset? directoryRequestedAt = null)
    {
        var normalizedIds = returnedMemberIds.Select(NormalizeId).Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var periods = await db.ZaloGroupMembershipPeriods
            .Where(period =>
                period.ZaloConnectionId == connectionId &&
                period.GroupId == groupId)
            .ToListAsync(cancellationToken);
        // The provider can publish a join/leave while its directory request is
        // in flight. Such a newer observation wins even if the directory is
        // processed later, because that directory may reflect an older snapshot.
        var requestedAt = directoryRequestedAt ?? observedAt;
        var newerMemberIds = periods
            .Where(period => period.LastObservedAt > requestedAt)
            .Select(period => period.ZaloUserId)
            .ToHashSet(StringComparer.Ordinal);
        var openPeriods = periods.Where(period => period.IsCurrentPeriod).ToList();
        var byMember = openPeriods.GroupBy(period => period.ZaloUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.Ordinal);

        foreach (var memberId in normalizedIds)
        {
            if (newerMemberIds.Contains(memberId)) continue;
            if (byMember.TryGetValue(memberId, out var period))
            {
                if (observedAt < period.LastObservedAt) continue;
                period.LastObservedAt = observedAt;
                period.UpdatedAt = observedAt;
                continue;
            }

            db.ZaloGroupMembershipPeriods.Add(new ZaloGroupMembershipPeriod
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                ZaloUserId = memberId,
                JoinedAt = null,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                EvidenceKind = ZaloMembershipEvidenceKind.ObservedOnly,
                IsCurrentPeriod = true,
                CreatedAt = observedAt,
                UpdatedAt = observedAt
            });
        }

        if (directoryIsComplete)
        {
            foreach (var period in openPeriods.Where(period =>
                         !normalizedIds.Contains(period.ZaloUserId) &&
                         !newerMemberIds.Contains(period.ZaloUserId) &&
                         observedAt >= period.LastObservedAt &&
                         (period.JoinedAt is null || period.JoinedAt <= observedAt)))
            {
                period.LeftAt ??= observedAt;
                period.IsCurrentPeriod = false;
                period.LastObservedAt = observedAt;
                period.UpdatedAt = observedAt;
            }
        }

        await TouchCoverageAsync(connectionId, groupId, observedAt, observedAt, cancellationToken);
        return newerMemberIds;
    }

    private async Task TouchCoverageAsync(
        string connectionId,
        string groupId,
        DateTimeOffset observedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var coverage = await db.ZaloMembershipCoverages.SingleOrDefaultAsync(
            item => item.ZaloConnectionId == connectionId && item.GroupId == groupId,
            cancellationToken);
        if (coverage is null)
        {
            db.ZaloMembershipCoverages.Add(new ZaloMembershipCoverage
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                CoveredFrom = observedAt,
                CoveredThrough = now,
                HasCompleteHistoricalJoinEvents = false,
                Source = "RealtimeListener",
                UpdatedAt = now
            });
            return;
        }

        coverage.CoveredFrom = coverage.CoveredFrom is null || observedAt < coverage.CoveredFrom
            ? observedAt
            : coverage.CoveredFrom;
        if (now > coverage.CoveredThrough) coverage.CoveredThrough = now;
        coverage.UpdatedAt = now;
    }

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();

    private static DateTimeOffset? FromUnixMs(long value)
    {
        if (value <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value < 10_000_000_000 ? value * 1000 : value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
