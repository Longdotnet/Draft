using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloRecentJoinItem(
    string ZaloUserId,
    string DisplayName,
    DateTimeOffset JoinedAt,
    bool IsRejoin);

public sealed record ZaloRecentJoinPage(
    IReadOnlyList<ZaloRecentJoinItem> Items,
    int TotalItems,
    int UnknownCurrentJoinDateCount,
    bool HasKnownCoverageGap,
    DateTimeOffset AsOf);

/// <summary>
/// Owns membership-history evidence. Directory observation can prove current membership,
/// but only provider membership events are allowed to populate JoinedAt.
/// </summary>
public sealed class ZaloMembershipHistoryService(VolleyDraftDbContext db)
{
    public async Task ApplyEventAsync(
        ZaloMembershipChangedEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var accountId = Normalize(incoming.AccountId);
        var groupId = Normalize(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return;

        var connection = await db.ZaloConnections
            .Where(item =>
                item.AccountZaloId == accountId &&
                item.MatchSessions.Any(session => session.ZaloGroupId == groupId && session.BotEnabled))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null) return;

        var occurredAt = ToSafeTimestamp(incoming.OccurredAtUnixMs);
        var eventType = Normalize(incoming.EventType).ToLowerInvariant();
        if (eventType is not ("join" or "leave" or "remove_member")) return;

        foreach (var changed in incoming.Members.Take(500))
        {
            var userId = Normalize(changed.ZaloUserId);
            if (userId.Length == 0) continue;
            var displayName = string.IsNullOrWhiteSpace(changed.DisplayName)
                ? $"Zalo {userId}"
                : changed.DisplayName.Trim();
            var eventId = $"{connection.Id}:{groupId}:{eventType}:{incoming.OccurredAtUnixMs}:{userId}";

            if (eventType == "join")
            {
                if (await db.ZaloGroupMembershipPeriods.AsNoTracking()
                    .AnyAsync(item => item.JoinEventId == eventId, cancellationToken))
                    continue;

                var period = await db.ZaloGroupMembershipPeriods
                    .Where(item =>
                        item.ZaloConnectionId == connection.Id &&
                        item.GroupId == groupId &&
                        item.ZaloUserId == userId &&
                        item.IsCurrentPeriod)
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (period is null)
                {
                    period = new ZaloGroupMembershipPeriod
                    {
                        ZaloConnectionId = connection.Id,
                        GroupId = groupId,
                        ZaloUserId = userId,
                        DisplayNameSnapshot = displayName,
                        JoinedAt = occurredAt,
                        JoinEvidenceSource = "provider_event:join",
                        JoinEventId = eventId,
                        FirstObservedAt = occurredAt,
                        LastObservedAt = occurredAt,
                        IsCurrentPeriod = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    db.ZaloGroupMembershipPeriods.Add(period);
                }
                else
                {
                    // A directory-only row has no join date. Promote it when the real
                    // provider event arrives; never overwrite a previously evidenced join.
                    if (period.JoinedAt is null)
                    {
                        period.JoinedAt = occurredAt;
                        period.JoinEvidenceSource = "provider_event:join";
                        period.JoinEventId = eventId;
                    }
                    period.DisplayNameSnapshot = displayName;
                    period.LastObservedAt = occurredAt;
                    period.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await UpsertCurrentMemberAsync(
                    connection.Id,
                    groupId,
                    userId,
                    displayName,
                    changed.AvatarUrl,
                    occurredAt,
                    true,
                    cancellationToken);
            }
            else
            {
                if (await db.ZaloGroupMembershipPeriods.AsNoTracking()
                    .AnyAsync(item => item.LeaveEventId == eventId, cancellationToken))
                    continue;

                var period = await db.ZaloGroupMembershipPeriods
                    .Where(item =>
                        item.ZaloConnectionId == connection.Id &&
                        item.GroupId == groupId &&
                        item.ZaloUserId == userId &&
                        item.IsCurrentPeriod)
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (period is not null)
                {
                    period.IsCurrentPeriod = false;
                    period.LeftAt = occurredAt;
                    period.LeaveEvidenceSource = $"provider_event:{eventType}";
                    period.LeaveEventId = eventId;
                    period.LastObservedAt = occurredAt;
                    period.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await UpsertCurrentMemberAsync(
                    connection.Id,
                    groupId,
                    userId,
                    displayName,
                    changed.AvatarUrl,
                    occurredAt,
                    false,
                    cancellationToken);
            }
        }

        var coverage = await GetOrCreateCoverageAsync(connection.Id, groupId, cancellationToken);
        coverage.ProviderEventTrackingStartedAt ??= occurredAt;
        if (coverage.LastProviderEventAt is null || occurredAt > coverage.LastProviderEventAt)
            coverage.LastProviderEventAt = occurredAt;
        // Listener restarts and historical gaps are not proven away by seeing one event.
        coverage.HasKnownGap = true;
        coverage.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Provider/webhook retries use deterministic event IDs. A concurrent duplicate
            // can win the unique index; the next delivery/read observes the committed fact.
            db.ChangeTracker.Clear();
        }
    }

    public async Task ObserveDirectoryAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<BridgeMember> members,
        bool isComplete,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        connectionId = Normalize(connectionId);
        groupId = Normalize(groupId);
        var currentIds = members
            .Select(member => Normalize(member.ZaloUserId))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var periods = await db.ZaloGroupMembershipPeriods
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsCurrentPeriod)
            .ToListAsync(cancellationToken);
        var byUser = periods
            .GroupBy(item => item.ZaloUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.Ordinal);

        foreach (var member in members)
        {
            var userId = Normalize(member.ZaloUserId);
            if (userId.Length == 0) continue;
            var displayName = string.IsNullOrWhiteSpace(member.DisplayName)
                ? $"Zalo {userId}"
                : member.DisplayName.Trim();
            if (!byUser.TryGetValue(userId, out var period))
            {
                period = new ZaloGroupMembershipPeriod
                {
                    ZaloConnectionId = connectionId,
                    GroupId = groupId,
                    ZaloUserId = userId,
                    DisplayNameSnapshot = displayName,
                    JoinedAt = null,
                    JoinEvidenceSource = "directory_observation",
                    FirstObservedAt = observedAt,
                    LastObservedAt = observedAt,
                    IsCurrentPeriod = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.ZaloGroupMembershipPeriods.Add(period);
                byUser[userId] = period;
            }
            else
            {
                period.DisplayNameSnapshot = displayName;
                period.LastObservedAt = observedAt;
                period.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        if (isComplete)
        {
            foreach (var period in periods.Where(item => !currentIds.Contains(item.ZaloUserId)))
            {
                period.IsCurrentPeriod = false;
                period.LeftAt = null;
                period.LeaveEvidenceSource = "directory_absence";
                period.LastObservedAt = observedAt;
                period.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        var coverage = await GetOrCreateCoverageAsync(connectionId, groupId, cancellationToken);
        coverage.LastDirectorySyncAt = observedAt;
        coverage.LastDirectorySyncWasComplete = isComplete;
        coverage.HasKnownGap = true;
        coverage.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ZaloRecentJoinPage> QueryRecentCurrentMembersAsync(
        string connectionId,
        string groupId,
        int days,
        DateTimeOffset asOf,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 3650);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 30);
        var since = asOf.AddDays(-days);

        // Group directories are bounded. Materialize before DateTimeOffset comparisons so
        // SQLite and PostgreSQL follow the same semantics.
        var current = await db.ZaloGroupMembershipPeriods
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsCurrentPeriod)
            .ToListAsync(cancellationToken);
        var known = current
            .Where(item =>
                item.JoinedAt is not null &&
                item.JoinedAt.Value >= since &&
                item.JoinedAt.Value <= asOf &&
                item.JoinEvidenceSource.StartsWith("provider_event:", StringComparison.Ordinal))
            .OrderByDescending(item => item.JoinedAt)
            .ThenBy(item => item.DisplayNameSnapshot)
            .ToList();
        var knownUserIds = known.Select(item => item.ZaloUserId).Distinct(StringComparer.Ordinal).ToList();
        var historicalUserIds = knownUserIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.ZaloGroupMembershipPeriods.AsNoTracking()
                .Where(item =>
                    item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    knownUserIds.Contains(item.ZaloUserId) &&
                    !item.IsCurrentPeriod)
                .Select(item => item.ZaloUserId)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        var total = known.Count;
        var items = known
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new ZaloRecentJoinItem(
                item.ZaloUserId,
                item.DisplayNameSnapshot,
                item.JoinedAt!.Value,
                historicalUserIds.Contains(item.ZaloUserId)))
            .ToList();
        var coverage = await db.ZaloMembershipCoverages.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId && item.GroupId == groupId,
                cancellationToken);
        return new ZaloRecentJoinPage(
            items,
            total,
            current.Count(item => item.JoinedAt is null),
            coverage?.HasKnownGap ?? true,
            asOf);
    }

    public async Task<ZaloRecentJoinItem?> GetCurrentJoinAsync(
        string connectionId,
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        userId = Normalize(userId);
        var current = await db.ZaloGroupMembershipPeriods
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.ZaloUserId == userId &&
                item.IsCurrentPeriod)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (current?.JoinedAt is null ||
            !current.JoinEvidenceSource.StartsWith("provider_event:", StringComparison.Ordinal))
            return null;

        var rejoin = await db.ZaloGroupMembershipPeriods.AsNoTracking()
            .AnyAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.ZaloUserId == userId &&
                !item.IsCurrentPeriod,
                cancellationToken);
        return new ZaloRecentJoinItem(
            current.ZaloUserId,
            current.DisplayNameSnapshot,
            current.JoinedAt.Value,
            rejoin);
    }

    private async Task<ZaloMembershipCoverage> GetOrCreateCoverageAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var coverage = await db.ZaloMembershipCoverages.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId,
            cancellationToken);
        if (coverage is not null) return coverage;
        coverage = new ZaloMembershipCoverage
        {
            ZaloConnectionId = connectionId,
            GroupId = groupId,
            HasKnownGap = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloMembershipCoverages.Add(coverage);
        return coverage;
    }

    private async Task UpsertCurrentMemberAsync(
        string connectionId,
        string groupId,
        string userId,
        string displayName,
        string? avatarUrl,
        DateTimeOffset observedAt,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        var member = await db.ZaloGroupMembers.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.ZaloUserId == userId,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (member is null)
        {
            member = new ZaloGroupMember
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                ZaloUserId = userId,
                DisplayName = displayName,
                AvatarUrl = avatarUrl,
                FirstSeenAt = observedAt,
                LastSeenAt = observedAt,
                LastSyncedAt = now,
                IsCurrentMember = isCurrent,
                LeftAt = isCurrent ? null : observedAt,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.ZaloGroupMembers.Add(member);
            return;
        }

        member.DisplayName = displayName;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) member.AvatarUrl = avatarUrl;
        member.LastSeenAt = observedAt;
        member.LastSyncedAt = now;
        member.IsCurrentMember = isCurrent;
        member.LeftAt = isCurrent ? null : observedAt;
        member.UpdatedAt = now;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static DateTimeOffset ToSafeTimestamp(long unixMs)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var value = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            return value < now.AddYears(-10) || value > now.AddDays(2) ? now : value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return now;
        }
    }
}
