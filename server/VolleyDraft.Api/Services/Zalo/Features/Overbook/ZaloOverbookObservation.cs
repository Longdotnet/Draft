using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<MatchSession?> GetOwnedSessionAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken) =>
        await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.ZaloConnection)
            .SingleOrDefaultAsync(session => session.Id == sessionId && session.AdminUserId == adminUserId, cancellationToken);

    private async Task<ZaloOverbookStateData> GetOrCreateStateAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(sessionId, cancellationToken);
        if (state is not null) return state;
        state = new ZaloOverbookStateData { SessionId = sessionId };
        await store.SaveAsync(state, cancellationToken);
        return state;
    }

    private async Task<OverbookObservation> ReadObservationAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy session.");
        if (session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            throw new InvalidOperationException("Session chưa liên kết group Zalo.");

        var latestImport = await db.PollImports
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .OrderByDescending(item => item.ImportedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Session chưa liên kết/import poll để theo dõi vượt slot.");
        var selectedOptionIds = ParseStringList(latestImport.SelectedOptionIdsJson);
        if (selectedOptionIds.Count == 0)
            throw new InvalidOperationException("Poll chưa có option được liên kết với session.");

        using var credentialDocument = JsonDocument.Parse(protector.Unprotect(session.ZaloConnection.EncryptedCredentials));
        var credentials = credentialDocument.RootElement.Clone();
        var poll = await bridge.GetPollAsync(credentials, latestImport.PollId);
        if (poll.IsAnonymous)
            throw new InvalidOperationException("Poll ẩn danh nên không thể tag người vote vượt slot.");

        var selectedSet = selectedOptionIds.ToHashSet(StringComparer.Ordinal);
        var selectedOptions = poll.Options.Where(option => selectedSet.Contains(option.Id)).ToList();
        if (selectedOptions.Count == 0)
            throw new InvalidOperationException("Không còn tìm thấy option đã liên kết trong poll Zalo.");
        var orderedVoterIds = selectedOptions
            .SelectMany(option => option.VoterIds)
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rosterPlayers = await db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .ToListAsync(cancellationToken);
        var currentVoterSet = orderedVoterIds.ToHashSet(StringComparer.Ordinal);
        var displayNames = rosterPlayers
            .Where(player => player.PlayerProfile is not null &&
                             !string.IsNullOrWhiteSpace(player.PlayerProfile.ZaloUserId))
            .GroupBy(player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);
        var missingNames = orderedVoterIds.Where(id => !displayNames.ContainsKey(id)).ToList();
        if (missingNames.Count > 0)
        {
            try
            {
                var members = await bridge.GetMembersAsync(credentials, missingNames);
                foreach (var member in members)
                    displayNames[ZaloOverbookLogic.NormalizeId(member.ZaloUserId)] = member.DisplayName;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not resolve all voter display names Session={SessionId}", sessionId);
            }
        }
        foreach (var voterId in orderedVoterIds)
            displayNames.TryAdd(voterId, $"Zalo {voterId}");

        var sharedSlots = await db.DraftSlots
            .AsNoTracking()
            .Include(slot => slot.Players)
            .ThenInclude(link => link.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .Where(slot => slot.SessionId == sessionId && slot.Type == DraftSlotType.Shared)
            .ToListAsync(cancellationToken);
        var sharedSlotByVoter = new Dictionary<string, string>(StringComparer.Ordinal);
        var reservedSharedSlotCount = 0;
        foreach (var slot in sharedSlots)
        {
            var presentLinks = slot.Players.Where(link => link.SessionPlayer.IsPresent).ToList();
            if (presentLinks.Count == 0) continue;
            var pollMembers = presentLinks
                .Select(link => ZaloOverbookLogic.NormalizeId(link.SessionPlayer.PlayerProfile?.ZaloUserId))
                .Where(id => id.Length > 0 && currentVoterSet.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (pollMembers.Count == 0)
            {
                reservedSharedSlotCount += 1;
                continue;
            }
            foreach (var voterId in pollMembers) sharedSlotByVoter[voterId] = slot.Id;
        }

        var reservedRegularCount = rosterPlayers.Count(player =>
        {
            if (player.IsInsideSharedSlot) return false;
            var zaloId = ZaloOverbookLogic.NormalizeId(player.PlayerProfile?.ZaloUserId);
            return zaloId.Length == 0 || !currentVoterSet.Contains(zaloId);
        });
        var reservedSlots = reservedRegularCount + reservedSharedSlotCount;
        var capacity = session.TeamCount * session.TeamSize;
        var evaluation = ZaloOverbookLogic.EvaluateCapacity(
            orderedVoterIds,
            capacity,
            reservedSlots,
            sharedSlotByVoter);

        return new OverbookObservation(
            session,
            selectedOptionIds,
            orderedVoterIds,
            displayNames,
            sharedSlotByVoter,
            evaluation,
            poll);
    }

    private async Task<ZaloOverbookStatusResponse> BuildStatusFromStoredStateAsync(
        MatchSession session,
        ZaloOverbookStateData state,
        CancellationToken cancellationToken)
    {
        var names = await db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == session.Id && player.PlayerProfile != null)
            .ToListAsync(cancellationToken);
        var nameMap = names
            .Where(player => !string.IsNullOrWhiteSpace(player.PlayerProfile!.ZaloUserId))
            .GroupBy(player => ZaloOverbookLogic.NormalizeId(player.PlayerProfile!.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);
        var voters = state.LastObservedVoterIds.Select((id, index) => new ZaloOverbookVoterResponse(
            id,
            nameMap.GetValueOrDefault(id, id),
            index + 1,
            state.SuggestedTargetVoterIds.Contains(id, StringComparer.Ordinal),
            state.ConfirmedTargetVoterIds.Contains(id, StringComparer.Ordinal),
            false)).ToList();
        var status = ToStatus(session, state, voters);
        return await AttachMatchLifecycleAsync(session, status, cancellationToken);
    }

    private async Task<ZaloOverbookStatusResponse> BuildStatusAsync(
        OverbookObservation observation,
        ZaloOverbookStateData state,
        CancellationToken cancellationToken)
    {
        var voters = observation.OrderedVoterIds.Select((id, index) => new ZaloOverbookVoterResponse(
            id,
            observation.DisplayNames.GetValueOrDefault(id, id),
            index + 1,
            state.SuggestedTargetVoterIds.Contains(id, StringComparer.Ordinal),
            state.ConfirmedTargetVoterIds.Contains(id, StringComparer.Ordinal),
            observation.SharedSlotByVoter.ContainsKey(id))).ToList();
        var status = ToStatus(observation.Session, state, voters);
        return await AttachMatchLifecycleAsync(observation.Session, status, cancellationToken);
    }

    private static ZaloOverbookStatusResponse ToStatus(
        MatchSession session,
        ZaloOverbookStateData state,
        IReadOnlyList<ZaloOverbookVoterResponse> voters) => new(
        session.Id,
        session.Name,
        session.ZaloGroupName,
        session.BotEnabled,
        state.Enabled,
        session.TeamCount * session.TeamSize,
        state.EffectiveSlotCount,
        state.RawVoterCount,
        state.ExcessSlotCount,
        state.GraceMinutes,
        state.ReminderIntervalMinutes,
        state.MaxReminders,
        state.MessageSource,
        state.FriendlyMessages,
        state.SeriousMessages,
        state.StrictMessages,
        ZaloOverbookMessageCatalog.GetUiBanks(state.ReminderMessageBanks),
        ZaloOverbookMessageCatalog.GetUiStageBanks(state.ReminderMessageBanks),
        ZaloOverbookMessageCatalog.GetDefaultStageBanks(),
        state.OrderConfidence,
        state.NeedsConfirmation,
        state.ReminderCount,
        state.LastReminderAt,
        state.NextReminderAt,
        state.CurrentPollId,
        state.CurrentSelectedOptionIds,
        voters,
        state.CurrentTargetVoterIds,
        state.LastError);

    private static void ResolveIncident(ZaloOverbookStateData state)
    {
        state.SuggestedTargetVoterIds = [];
        state.CurrentTargetVoterIds = [];
        state.ConfirmedTargetVoterIds = [];
        state.NeedsConfirmation = false;
        state.ReminderCount = 0;
        state.LastReminderAt = null;
        state.NextReminderAt = null;
        state.IncidentKey = null;
        state.UsedMessageKeys = [];
        state.LastMessageKey = null;
    }

    private static void RequireConfirmation(ZaloOverbookStateData state, string confidence)
    {
        state.NeedsConfirmation = true;
        state.OrderConfidence = confidence;
        state.CurrentTargetVoterIds = [];
        state.ConfirmedTargetVoterIds = [];
        state.NextReminderAt = null;
        state.ReminderCount = 0;
        state.LastReminderAt = null;
        state.IncidentKey = state.ExcessSlotCount > 0 ? state.IncidentKey ?? Guid.NewGuid().ToString("n") : null;
        state.UsedMessageKeys = [];
        state.LastMessageKey = null;
    }

    private static void StartOrUpdateObservedIncident(
        ZaloOverbookStateData state,
        IReadOnlyList<string> targets,
        DateTimeOffset now)
    {
        if (!state.CurrentTargetVoterIds.SequenceEqual(targets, StringComparer.Ordinal))
        {
            state.CurrentTargetVoterIds = targets.ToList();
            state.IncidentKey = Guid.NewGuid().ToString("n");
            ResetReminderProgress(state, now);
        }
    }

    private static void ResetReminderProgress(ZaloOverbookStateData state, DateTimeOffset now)
    {
        state.ReminderCount = 0;
        state.LastReminderAt = null;
        state.UsedMessageKeys = [];
        state.LastMessageKey = null;
        state.NextReminderAt = state.Enabled && !state.NeedsConfirmation
            ? now.AddMinutes(state.GraceMinutes)
            : null;
    }

    private static void ApplyConfirmedTargets(
        ZaloOverbookStateData state,
        IReadOnlyList<string> targets,
        DateTimeOffset now)
    {
        state.CurrentTargetVoterIds = targets.ToList();
        state.ConfirmedTargetVoterIds = targets.ToList();
        state.NeedsConfirmation = false;
        state.OrderConfidence = "AdminConfirmed";
        state.IncidentKey = Guid.NewGuid().ToString("n");
        ResetReminderProgress(state, now);
    }

    private static bool TargetsStillValid(OverbookObservation observation, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0 || observation.Capacity.ExcessSlotCount <= 0) return false;
        var validation = ValidateConfirmedTargets(observation, targets);
        return validation is null;
    }

    private static string? ValidateConfirmedTargets(OverbookObservation observation, IReadOnlyList<string> targets)
    {
        if (observation.Capacity.ExcessSlotCount <= 0) return "Poll hiện không còn vượt slot.";
        if (targets.Count == 0) return "Hãy chọn người/lượt vote dư cần tag.";
        var currentSet = observation.OrderedVoterIds.ToHashSet(StringComparer.Ordinal);
        if (targets.Any(id => !currentSet.Contains(id))) return "Có người được chọn không còn vote option này.";

        var targetUnits = observation.Capacity.OrderedPollUnits
            .Where(unit => unit.VoterIds.Any(id => targets.Contains(id, StringComparer.Ordinal)))
            .ToList();
        if (targetUnits.Count != observation.Capacity.ExcessSlotCount)
            return $"Cần xác nhận đúng {observation.Capacity.ExcessSlotCount} slot vượt. Shared slot được tính là một slot.";
        foreach (var unit in targetUnits)
        {
            if (unit.VoterIds.Any(id => !targets.Contains(id, StringComparer.Ordinal)))
                return "Nếu một shared slot là lượt dư, hãy chọn toàn bộ voter thuộc shared slot đó.";
        }
        return null;
    }

}