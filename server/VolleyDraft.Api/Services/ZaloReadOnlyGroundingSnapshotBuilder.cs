using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds a bounded, read-only database snapshot for semantic question planning.
/// The snapshot contains candidate IDs the model may reference; it is never mutation
/// authority and deliberately excludes unrelated historical data.
/// </summary>
internal sealed class ZaloReadOnlyGroundingSnapshotBuilder(VolleyDraftDbContext db)
{
    public async Task<ZaloReadOnlyGroundingSnapshot> BuildAsync(
        string connectionId,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionRows = await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.Players)
                .ThenInclude(player => player.PlayerProfile)
            .Include(session => session.WaitlistEntries)
            .Include(session => session.ReminderSchedules)
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);

        var sessions = sessionRows
            .Where(session => session.StartTime is null || session.StartTime >= now.AddHours(-4))
            .OrderBy(session => session.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(session => session.Name, StringComparer.Ordinal)
            .Take(8)
            .ToList();
        var sessionIds = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);

        var sessionSnapshots = sessions.Select(session => new ZaloReadOnlyGroundingSession(
            session.Id,
            Clean(session.Name, 120),
            session.StartTime,
            CleanOptional(session.Location, 180),
            session.Status.ToString(),
            Math.Max(0, session.TeamCount * session.TeamSize),
            session.Players.Count(player => player.IsPresent)))
            .ToArray();

        var members = sessions
            .SelectMany(session => session.Players
                .OrderByDescending(player => player.IsPresent)
                .ThenBy(player => player.DisplayName, StringComparer.Ordinal)
                .Take(40)
                .Select(player => new ZaloReadOnlyGroundingMember(
                    StableMemberId(player.PlayerProfile?.ZaloUserId, player.PlayerProfileId, player.Id),
                    player.Id,
                    session.Id,
                    CleanOptional(player.PlayerProfileId, 100),
                    CleanOptional(player.PlayerProfile?.ZaloUserId, 100),
                    Clean(player.DisplayName, 120),
                    player.IsPresent)))
            .Take(120)
            .ToList();

        // A person can be a valid subject even when they do not yet have a roster row
        // in the target session. Add a small current-group identity pool so the model
        // can ground "Nam" to a stable Zalo/profile identity without inventing one.
        var groupMemberRows = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member => member.GroupId == groupId && member.IsCurrentMember)
            .Select(member => new { member.ZaloUserId, member.DisplayName, member.LastSeenAt })
            .ToListAsync(cancellationToken);
        var recentGroupMembers = groupMemberRows
            .Where(member => !string.IsNullOrWhiteSpace(member.ZaloUserId))
            .OrderByDescending(member => member.LastSeenAt)
            .GroupBy(member => member.ZaloUserId.Trim(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(80)
            .ToArray();
        var groupUids = recentGroupMembers
            .Select(member => member.ZaloUserId.Trim())
            .ToArray();
        var groupProfiles = groupUids.Length == 0
            ? []
            : await db.PlayerProfiles
                .AsNoTracking()
                .Where(profile => groupUids.Contains(profile.ZaloUserId))
                .Select(profile => new { profile.Id, profile.ZaloUserId, profile.DisplayName })
                .ToArrayAsync(cancellationToken);
        var profileByUid = groupProfiles
            .GroupBy(profile => profile.ZaloUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var existingStableIds = members
            .Select(member => member.MemberId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var groupMember in recentGroupMembers)
        {
            var uid = Clean(groupMember.ZaloUserId, 100);
            profileByUid.TryGetValue(uid, out var profile);
            var stableId = StableMemberId(uid, profile?.Id, null);
            if (!existingStableIds.Add(stableId)) continue;
            members.Add(new ZaloReadOnlyGroundingMember(
                stableId,
                null,
                null,
                CleanOptional(profile?.Id, 100),
                uid,
                Clean(string.IsNullOrWhiteSpace(groupMember.DisplayName) ? profile?.DisplayName : groupMember.DisplayName, 120),
                false));
        }

        var stableIdBySessionPlayer = members
            .Where(member => !string.IsNullOrWhiteSpace(member.SessionPlayerId))
            .GroupBy(member => member.SessionPlayerId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().MemberId, StringComparer.Ordinal);

        var teamRows = sessionIds.Count == 0
            ? new List<Team>()
            : await db.Teams
                .AsNoTracking()
                .Include(team => team.AssignedSlots)
                    .ThenInclude(slot => slot.Players)
                .Where(team => sessionIds.Contains(team.SessionId))
                .OrderBy(team => team.SessionId)
                .ThenBy(team => team.Name)
                .Take(32)
                .ToListAsync(cancellationToken);
        var teams = teamRows
            .Select(team => new ZaloReadOnlyGroundingTeam(
                team.Id,
                Clean(team.Name, 120),
                team.SessionId,
                team.AssignedSlots
                    .SelectMany(slot => slot.Players)
                    .Select(player => stableIdBySessionPlayer.TryGetValue(player.SessionPlayerId, out var stableId)
                        ? stableId
                        : $"session-player:{player.SessionPlayerId}")
                    .Distinct(StringComparer.Ordinal)
                    .Take(40)
                    .ToArray()))
            .ToArray();

        var waitlist = sessions
            .SelectMany(session => session.WaitlistEntries
                .Where(entry => entry.Status is SessionWaitlistStatus.Waiting or SessionWaitlistStatus.Invited)
                .OrderBy(entry => entry.CreatedAt)
                .Take(20)
                .Select(entry => new ZaloReadOnlyGroundingWaitlistEntry(
                    entry.Id,
                    session.Id,
                    !string.IsNullOrWhiteSpace(entry.SessionPlayerId) && stableIdBySessionPlayer.TryGetValue(entry.SessionPlayerId, out var stableId)
                        ? stableId
                        : StableMemberId(entry.ZaloUserId, null, entry.SessionPlayerId),
                    Clean(entry.ZaloUserId, 100),
                    Clean(entry.DisplayName, 120),
                    entry.Status.ToString())))
            .Take(60)
            .ToArray();

        var offerStore = new ZaloOpenSlotOfferStore(db);
        var claimable = await offerStore.ListClaimableAsync(
            connectionId,
            groupId,
            Clean(senderZaloUserId, 100),
            cancellationToken);
        var owned = await offerStore.ListOwnedActiveAsync(
            connectionId,
            groupId,
            Clean(senderZaloUserId, 100),
            cancellationToken);
        var offers = claimable
            .Concat(owned)
            .Where(offer => offer.Status == ZaloOpenSlotOfferStatus.Open && sessionIds.Contains(offer.SessionId))
            .GroupBy(offer => offer.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(offer => offer.UpdatedAt)
            .Take(16)
            .Select(offer => new ZaloReadOnlyGroundingOffer(
                offer.Id,
                offer.OwnerZaloUserId,
                Clean(offer.OwnerDisplayName, 120),
                offer.SessionId,
                Clean(offer.SessionName, 120),
                CleanOptional(offer.SourceMessageId, 160),
                offer.Status.ToString()))
            .ToArray();

        var reminders = sessions
            .Select(session =>
            {
                var enabled = session.ReminderSchedules
                    .Where(reminder => reminder.Enabled)
                    .OrderBy(reminder => reminder.NextRunAt)
                    .ToArray();
                return new ZaloReadOnlyGroundingReminder(
                    session.Id,
                    enabled.Length,
                    enabled.FirstOrDefault()?.NextRunAt);
            })
            .ToArray();

        return new ZaloReadOnlyGroundingSnapshot(
            sessionSnapshots,
            members.Take(160).ToArray(),
            teams,
            waitlist,
            offers,
            reminders);
    }

    private static string StableMemberId(string? zaloUserId, string? playerProfileId, string? sessionPlayerId)
    {
        var uid = Clean(zaloUserId, 100);
        if (uid.Length > 0) return $"zalo:{uid}";
        var profileId = Clean(playerProfileId, 100);
        if (profileId.Length > 0) return $"profile:{profileId}";
        var playerId = Clean(sessionPlayerId, 100);
        return playerId.Length > 0 ? $"session-player:{playerId}" : "unresolved";
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
