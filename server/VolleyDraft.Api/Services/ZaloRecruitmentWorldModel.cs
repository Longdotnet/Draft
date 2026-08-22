using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloRecruitmentWorldPlayer(
    string SessionPlayerId,
    string DisplayName,
    bool IsPresent,
    PlayerGender Gender,
    PlayerLevel Level,
    PlayerRole Role);

internal sealed record ZaloRecruitmentWorldGuest(
    string ReservationId,
    string SponsorZaloUserId,
    string SponsorDisplayName,
    int SponsorSequence,
    string DisplayName,
    PlayerGender? Gender,
    PlayerLevel? Level,
    PlayerRole? Role,
    string Status,
    string? SessionPlayerId,
    string SourceMessageId,
    DateTimeOffset UpdatedAt);

internal sealed record ZaloRecruitmentWorldTask(
    string TaskKey,
    string Intent,
    string SessionId,
    string SessionName,
    string MissingArgumentsJson,
    string CandidateEntitiesJson,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record ZaloRecruitmentWorldSnapshot(
    string SessionId,
    string SessionName,
    DateTimeOffset? StartTime,
    int PresentPlayerCount,
    int EffectiveSlotCount,
    int Capacity,
    int MissingProfileCount,
    IReadOnlyList<string> MissingProfileNames,
    bool HasLinkedPoll,
    bool IsRosterReady,
    string ReadinessState,
    string? RecruitmentDecision,
    IReadOnlyList<ZaloRecruitmentWorldPlayer> Roster,
    IReadOnlyList<ZaloRecruitmentWorldGuest> Guests,
    IReadOnlyList<ZaloRecruitmentWorldTask> SenderTasks,
    DateTimeOffset CurrentUtc);

/// <summary>
/// Bounded read-only world model for recruitment reasoning. It centralizes the
/// facts that used to be scattered across readiness, guest rows, decision state,
/// and conversation state. The model is context only; mutation services still own
/// authorization/capacity and re-read DB before executing.
/// </summary>
internal sealed class ZaloRecruitmentWorldModelBuilder(VolleyDraftDbContext db)
{
    internal async Task<ZaloRecruitmentWorldSnapshot?> BuildAsync(
        string sessionId,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(sessionId, cancellationToken: cancellationToken);
        if (readiness is null) return null;

        var players = await db.SessionPlayers
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .OrderByDescending(item => item.IsPresent)
            .ThenBy(item => item.CreatedAt)
            .Take(40)
            .Select(item => new ZaloRecruitmentWorldPlayer(
                item.Id,
                item.DisplayName,
                item.IsPresent,
                item.Gender,
                item.Level,
                item.Role))
            .ToListAsync(cancellationToken);

        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var guestRows = await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId &&
                           (item.Status == ZaloGuestReservationStatus.Active ||
                            item.Status == ZaloGuestReservationStatus.Waitlisted))
            .Take(60)
            .ToListAsync(cancellationToken);
        var guests = guestRows
            .OrderBy(item => item.Status == ZaloGuestReservationStatus.Active ? 0 : 1)
            .ThenBy(item => item.CreatedAt)
            .Select(item => new ZaloRecruitmentWorldGuest(
                item.Id,
                item.SponsorZaloUserId,
                item.SponsorDisplayName,
                item.SponsorSequence,
                item.DisplayName,
                item.Gender,
                item.Level,
                item.Role,
                item.Status.ToString(),
                item.SessionPlayerId,
                item.SourceMessageId,
                item.UpdatedAt))
            .ToArray();

        string? decision = null;
        try
        {
            decision = (await new ZaloDraftPreparationDecisionStore(db)
                .GetAsync(sessionId, cancellationToken))?.Kind.ToString();
        }
        catch
        {
            // Decision state is context enrichment only. Readiness/DB grounding still
            // remains usable when an old deployment has not created this table yet.
        }

        IReadOnlyList<ZaloConversationTaskSnapshot> taskRows = [];
        try
        {
            taskRows = await new ZaloConversationTaskStackStore(db)
                .LoadActiveAsync(groupId, senderZaloUserId, "RecruitmentGuest", 12, cancellationToken);
        }
        catch
        {
            // Same fail-soft rule: world enrichment may be absent but never grants or
            // removes mutation authority.
        }
        var tasks = taskRows.Select(item => new ZaloRecruitmentWorldTask(
            item.TaskKey,
            item.Intent,
            item.SessionId,
            item.SessionName,
            item.MissingArgumentsJson,
            item.CandidateEntitiesJson,
            item.UpdatedAt,
            item.ExpiresAt)).ToArray();

        return new ZaloRecruitmentWorldSnapshot(
            readiness.SessionId,
            readiness.SessionName,
            readiness.StartTime,
            readiness.PresentPlayerCount,
            readiness.EffectiveSlotCount,
            readiness.Capacity,
            readiness.MissingProfileCount,
            readiness.MissingProfileNames,
            readiness.HasLinkedPoll,
            readiness.IsRosterReady,
            readiness.State.ToString(),
            decision,
            players,
            guests,
            tasks,
            DateTimeOffset.UtcNow);
    }
}
