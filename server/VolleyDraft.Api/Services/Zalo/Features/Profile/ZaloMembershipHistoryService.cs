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

        var groupId = incoming.GroupId.Trim();
        var accountId = incoming.AccountId.Trim();
        var memberIds = incoming.MemberIds
            .Select(NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToList();
        if (groupId.Length == 0 || accountId.Length == 0 || memberIds.Count == 0)
            return false;

        var connectionIds = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection =>
                connection.AccountZaloId == accountId &&
                connection.MatchSessions.Any(session =>
                    session.ZaloGroupId == groupId && session.BotEnabled))
            .Select(connection => connection.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
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
        var occurredAt = FromUnixMs(incoming.OccurredAtUnixMs) ?? DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var memberId in memberIds)
        {
            var current = await db.ZaloGroupMembershipPeriods
                .Where(period =>
                    period.ZaloConnectionId == connectionId &&
                    period.GroupId == groupId &&
                    period.ZaloUserId == memberId &&
                    period.IsCurrentPeriod)
                .OrderByDescending(period => period.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (incoming.EventType == "join")
            {
                var sourceEventId = $"{incoming.EventId}:{memberId}";
                var duplicate = await db.ZaloGroupMembershipPeriods
                    .AsNoTracking()
                    .AnyAsync(period =>
                        period.ZaloConnectionId == connectionId &&
                        period.GroupId == groupId &&
                        period.SourceEventId == sourceEventId,
                        cancellationToken);
                if (duplicate)
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
                if (duplicateLeave)
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
                        IsCurrentPeriod = false,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
                else
                {
                    current.LeftAt = occurredAt;
                    current.IsCurrentPeriod = false;
                    current.LastObservedAt = now;
                    current.UpdatedAt = now;
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
        days = Math.Clamp(days, 1, 3650);
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
        var complete = coverage?.HasCompleteHistoricalJoinEvents == true ||
                       (coverage?.CoveredFrom is not null &&
                        coverage.CoveredFrom <= windowStart &&
                        coverage.CoveredThrough >= snapshotAt);

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
        if (member is null) return null;

        var period = await db.ZaloGroupMembershipPeriods.AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.ZaloUserId == zaloUserId &&
                item.IsCurrentPeriod)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var verified = period?.JoinedAt is not null &&
                       period.EvidenceKind != ZaloMembershipEvidenceKind.ObservedOnly;
        return new ZaloMemberJoinDateResult(
            zaloUserId,
            member.DisplayName,
            verified ? period!.JoinedAt : null,
            verified && period!.EvidenceKind == ZaloMembershipEvidenceKind.ProviderRejoinEvent,
            verified);
    }

    internal async Task ObserveDirectoryAsync(
        string connectionId,
        string groupId,
        IReadOnlyCollection<string> returnedMemberIds,
        bool directoryIsComplete,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = returnedMemberIds.Select(NormalizeId).Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var openPeriods = await db.ZaloGroupMembershipPeriods
            .Where(period =>
                period.ZaloConnectionId == connectionId &&
                period.GroupId == groupId &&
                period.IsCurrentPeriod)
            .ToListAsync(cancellationToken);
        var byMember = openPeriods.GroupBy(period => period.ZaloUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.Ordinal);

        foreach (var memberId in normalizedIds)
        {
            if (byMember.TryGetValue(memberId, out var period))
            {
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
            foreach (var period in openPeriods.Where(period => !normalizedIds.Contains(period.ZaloUserId)))
            {
                period.LeftAt ??= observedAt;
                period.IsCurrentPeriod = false;
                period.LastObservedAt = observedAt;
                period.UpdatedAt = observedAt;
            }
        }

        await TouchCoverageAsync(connectionId, groupId, observedAt, observedAt, cancellationToken);
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
