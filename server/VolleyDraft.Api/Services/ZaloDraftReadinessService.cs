using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public enum ZaloDraftReadinessState
{
    Ready,
    AlreadyDrafted,
    RosterNotFull,
    RosterOverCapacity,
    MissingProfiles,
    SessionStarted,
    MissingStartTime,
    InvalidStatus,
    NoRoster
}

public sealed record ZaloDraftReadinessSnapshot(
    string SessionId,
    string SessionName,
    string AdminUserId,
    string ZaloConnectionId,
    string GroupId,
    DateTimeOffset? StartTime,
    int PresentPlayerCount,
    int EffectiveSlotCount,
    int Capacity,
    int MissingProfileCount,
    IReadOnlyList<string> MissingProfileNames,
    bool HasTeams,
    bool HasLinkedPoll,
    string Fingerprint,
    ZaloDraftReadinessState State,
    string ReasonCode,
    bool IsRosterReady,
    bool CanEscalate);

/// <summary>
/// Single deterministic source of truth for the conversational/proactive draft pilot.
/// This deliberately uses the configured session capacity rather than the lower-level
/// draft engine's divisibility minimum so the bot never urges a partial 9/12 draft.
/// </summary>
public sealed class ZaloDraftReadinessService(VolleyDraftDbContext db)
{
    public async Task<ZaloDraftReadinessSnapshot?> BuildAsync(
        string sessionId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null ||
            string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return null;

        var current = now ?? DateTimeOffset.UtcNow;
        var capacity = Math.Max(1, session.TeamCount * session.TeamSize);
        var presentIds = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == session.Id && player.IsPresent)
            .Select(player => player.Id)
            .ToListAsync(cancellationToken);
        var presentCount = presentIds.Count;
        var presentIdSet = presentIds.ToHashSet(StringComparer.Ordinal);

        var sharedSlotIds = await db.DraftSlots
            .AsNoTracking()
            .Where(slot => slot.SessionId == session.Id && slot.Type == DraftSlotType.Shared)
            .Select(slot => slot.Id)
            .ToListAsync(cancellationToken);
        var sharedLinks = sharedSlotIds.Count == 0
            ? []
            : await db.DraftSlotPlayers
                .AsNoTracking()
                .Where(link => sharedSlotIds.Contains(link.DraftSlotId))
                .Select(link => new { link.DraftSlotId, link.SessionPlayerId })
                .ToListAsync(cancellationToken);
        var collapsedPlayers = sharedLinks
            .Where(link => presentIdSet.Contains(link.SessionPlayerId))
            .GroupBy(link => link.DraftSlotId, StringComparer.Ordinal)
            .Sum(group => Math.Max(0, group.Select(item => item.SessionPlayerId).Distinct(StringComparer.Ordinal).Count() - 1));
        var effectiveSlots = Math.Max(0, presentCount - collapsedPlayers);

        var hasTeams = session.Status == SessionStatus.Finished ||
                       await db.Teams.AsNoTracking().AnyAsync(team => team.SessionId == session.Id, cancellationToken) ||
                       await db.DraftSlots.AsNoTracking().AnyAsync(
                           slot => slot.SessionId == session.Id && slot.AssignedTeamId != null,
                           cancellationToken);
        var hasLinkedPoll = await db.PollImports.AsNoTracking()
            .AnyAsync(import => import.SessionId == session.Id, cancellationToken);

        IReadOnlyList<string> missingNames = [];
        if (!hasTeams && session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection)
        {
            var incomplete = await new SessionDraftService(db)
                .GetIncompletePlayerProfilesAsync(session.AdminUserId, session.Id);
            if (incomplete.IsSuccess && incomplete.Value is not null)
            {
                missingNames = incomplete.Value
                    .Select(item => item.DisplayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Take(20)
                    .ToList();
            }
        }

        string fingerprint;
        try
        {
            fingerprint = await new ZaloBotActionHistoryService(
                    db,
                    NullLogger<ZaloBotActionHistoryService>.Instance)
                .CaptureShareStateHashAsync(session.Id, cancellationToken);
        }
        catch
        {
            // A missing fingerprint must make the autopilot fail closed. The caller
            // can still fall through to the existing explicitly-addressed draft flow.
            fingerprint = string.Empty;
        }

        var state = ZaloDraftReadinessState.Ready;
        var reason = "draft_ready";
        var rosterReady = effectiveSlots == capacity && presentCount > 0 && missingNames.Count == 0;
        var canEscalate = false;

        if (hasTeams || session.Status == SessionStatus.Finished)
        {
            state = ZaloDraftReadinessState.AlreadyDrafted;
            reason = "draft_already_exists";
        }
        else if (session.Status is not (SessionStatus.Setup or SessionStatus.CaptainSelection))
        {
            state = session.Status == SessionStatus.Cancelled
                ? ZaloDraftReadinessState.InvalidStatus
                : ZaloDraftReadinessState.InvalidStatus;
            reason = "draft_blocked_invalid_status";
        }
        else if (session.StartTime is { } start && start <= current)
        {
            state = ZaloDraftReadinessState.SessionStarted;
            reason = "draft_blocked_session_started";
        }
        else if (presentCount == 0)
        {
            state = ZaloDraftReadinessState.NoRoster;
            reason = "draft_blocked_roster_empty";
        }
        else if (effectiveSlots < capacity)
        {
            state = ZaloDraftReadinessState.RosterNotFull;
            reason = "draft_blocked_roster_not_full";
        }
        else if (effectiveSlots > capacity)
        {
            state = ZaloDraftReadinessState.RosterOverCapacity;
            reason = "draft_blocked_roster_over_capacity";
        }
        else if (missingNames.Count > 0)
        {
            state = ZaloDraftReadinessState.MissingProfiles;
            reason = "draft_blocked_missing_profile";
        }
        else if (session.StartTime is null)
        {
            state = ZaloDraftReadinessState.MissingStartTime;
            reason = "draft_blocked_missing_start_time";
        }
        else if (fingerprint.Length == 0)
        {
            state = ZaloDraftReadinessState.InvalidStatus;
            reason = "draft_blocked_fingerprint_unavailable";
        }
        else
        {
            canEscalate = true;
        }

        return new ZaloDraftReadinessSnapshot(
            session.Id,
            session.Name,
            session.AdminUserId,
            session.ZaloConnectionId!,
            session.ZaloGroupId!,
            session.StartTime,
            presentCount,
            effectiveSlots,
            capacity,
            missingNames.Count,
            missingNames,
            hasTeams,
            hasLinkedPoll,
            fingerprint,
            state,
            reason,
            rosterReady,
            canEscalate);
    }
}
