using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDraftReminderBucket(
    string Key,
    DateTimeOffset DueAt,
    bool Urgent);

internal static class ZaloDraftPreparationReminderPolicy
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    internal static ZaloDraftReminderBucket? GetDueBucket(
        DateTimeOffset startTime,
        DateTimeOffset now,
        int stopNudgingMinutes)
    {
        var localNow = now.ToOffset(VietnamOffset);
        var localStart = startTime.ToOffset(VietnamOffset);
        if (localNow.Date != localStart.Date) return null;

        var stopAt = localStart.AddMinutes(-Math.Max(10, stopNudgingMinutes));
        var noon = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day, 12, 0, 0, VietnamOffset);
        if (localNow < noon || localNow > stopAt) return null;

        DateTimeOffset dueAt;
        var twoPm = noon.AddHours(2);
        var fourPm = noon.AddHours(4);
        if (localNow < twoPm)
        {
            dueAt = noon;
        }
        else if (localNow < fourPm)
        {
            dueAt = twoPm;
        }
        else
        {
            var elapsed = localNow - fourPm;
            var bucketMinutes = Math.Floor(elapsed.TotalMinutes / 30d) * 30d;
            dueAt = fourPm.AddMinutes(bucketMinutes);
        }

        if (dueAt > stopAt) return null;
        return new ZaloDraftReminderBucket(
            dueAt.ToString("yyyyMMdd-HHmm"),
            dueAt,
            dueAt >= fourPm);
    }
}

public sealed partial class ZaloOverbookService
{
    private async Task<(bool Success, string? Error)> RefreshLinkedPollForDraftReminderAsync(
        MatchSession session,
        CancellationToken cancellationToken)
    {
        if (session.ZaloConnection is null ||
            string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return (false, "session_not_linked_to_zalo_group");

        var linkedImport = await db.PollImports
            .AsNoTracking()
            .Where(item => item.SessionId == session.Id)
            .OrderByDescending(item => item.ImportedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkedImport is null || string.IsNullOrWhiteSpace(linkedImport.PollId))
            return (false, "session_has_no_linked_poll");

        List<string> selectedOptionIds;
        try
        {
            selectedOptionIds = JsonSerializer.Deserialize<List<string>>(linkedImport.SelectedOptionIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return (false, "linked_poll_option_ids_invalid");
        }
        selectedOptionIds = selectedOptionIds
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selectedOptionIds.Count == 0)
            return (false, "session_has_no_linked_poll_option");

        try
        {
            using var document = JsonDocument.Parse(
                protector.Unprotect(session.ZaloConnection.EncryptedCredentials));
            var poll = await bridge.GetPollAsync(document.RootElement.Clone(), linkedImport.PollId);
            if (poll.IsAnonymous)
                return (false, "linked_poll_is_anonymous");

            var selectedOptions = poll.Options
                .Where(option => selectedOptionIds.Contains(
                    ZaloOverbookLogic.NormalizeId(option.Id),
                    StringComparer.Ordinal))
                .ToList();
            if (selectedOptions.Count != selectedOptionIds.Count)
                return (false, "linked_poll_option_missing");

            var voterIds = selectedOptions
                .SelectMany(option => option.VoterIds)
                .Select(ZaloOverbookLogic.NormalizeId)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (voterIds.Count == 0)
            {
                if (poll.HideVotePreview)
                    return (false, "linked_poll_hides_voters");

                await DeactivateRemovedLinkedPollPlayersAsync(
                    session.Id,
                    linkedImport.PollId,
                    new HashSet<string>(StringComparer.Ordinal),
                    cancellationToken);
                return (true, null);
            }

            var previewResult = await integration.CreateImportPreviewAsync(
                session.AdminUserId,
                session.Id,
                new CreateZaloImportPreviewRequest(linkedImport.PollId, selectedOptionIds));
            if (!previewResult.IsSuccess || previewResult.Value is null)
                return (false, previewResult.Error ?? "linked_poll_preview_failed");

            var preview = previewResult.Value;
            if (!string.Equals(preview.PollId, linkedImport.PollId, StringComparison.Ordinal))
                return (false, "linked_poll_changed_during_refresh");

            var decisions = preview.Candidates
                .Select(candidate => new ZaloImportCandidateDecision(
                    candidate.ZaloUserId,
                    true,
                    candidate.Gender ?? PlayerGender.Unknown,
                    candidate.Role,
                    candidate.Level))
                .ToList();
            var imported = await integration.ConfirmImportAsync(
                session.AdminUserId,
                session.Id,
                new ConfirmZaloPollImportRequest(
                    preview.PollId,
                    selectedOptionIds,
                    preview.PollUpdatedAtUnixMs,
                    decisions),
                preserveMissingProfileFields: true);
            if (!imported.IsSuccess)
                return (false, imported.Error ?? "linked_poll_import_failed");

            var activeZaloIds = preview.Candidates
                .Select(candidate => ZaloOverbookLogic.NormalizeId(candidate.ZaloUserId))
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            await DeactivateRemovedLinkedPollPlayersAsync(
                session.Id,
                linkedImport.PollId,
                activeZaloIds,
                cancellationToken);
            return (true, null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, exception.Message);
        }
    }

    private async Task<int> CountActiveSlotRisksAsync(
        MatchSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return 0;

        var ownerIds = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == session.Id &&
                             player.IsPresent &&
                             player.PlayerProfile != null &&
                             player.PlayerProfile.ZaloUserId != null)
            .Select(player => player.PlayerProfile!.ZaloUserId!)
            .ToListAsync(cancellationToken);
        ownerIds = ownerIds
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ownerIds.Count == 0) return 0;

        var offerStore = new ZaloOpenSlotOfferStore(db);
        var activeOfferIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ownerId in ownerIds)
        {
            var offers = await offerStore.ListOwnedActiveAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                ownerId,
                cancellationToken);
            foreach (var offer in offers)
            {
                if (string.Equals(offer.SessionId, session.Id, StringComparison.Ordinal))
                    activeOfferIds.Add(offer.Id);
            }
        }
        return activeOfferIds.Count;
    }

    private async Task DeactivateRemovedLinkedPollPlayersAsync(
        string sessionId,
        string pollId,
        IReadOnlySet<string> activeZaloIds,
        CancellationToken cancellationToken)
    {
        var importedPlayers = await db.SessionPlayers
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId &&
                             player.SourcePollId == pollId)
            .ToListAsync(cancellationToken);
        var removedPlayerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in importedPlayers)
        {
            var zaloId = ZaloOverbookLogic.NormalizeId(player.PlayerProfile?.ZaloUserId);
            if (zaloId.Length > 0 && activeZaloIds.Contains(zaloId)) continue;
            if (!player.IsPresent && !player.IsInsideSharedSlot) continue;
            player.IsPresent = false;
            player.IsCaptainEligible = false;
            removedPlayerIds.Add(player.Id);
        }

        if (removedPlayerIds.Count == 0) return;
        await ReconcileDraftReminderSharedSlotsAfterRemovalAsync(
            sessionId,
            removedPlayerIds,
            cancellationToken);
        await CleanupDraftReminderTeamPreferenceGroupsAsync(sessionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileDraftReminderSharedSlotsAfterRemovalAsync(
        string sessionId,
        IReadOnlySet<string> removedPlayerIds,
        CancellationToken cancellationToken)
    {
        var slots = await db.DraftSlots
            .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
            .ThenInclude(link => link.SessionPlayer)
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                slot.Players.Any(link => removedPlayerIds.Contains(link.SessionPlayerId)))
            .ToListAsync(cancellationToken);
        foreach (var slot in slots)
        {
            var removedLinks = slot.Players
                .Where(link => removedPlayerIds.Contains(link.SessionPlayerId))
                .ToList();
            foreach (var link in removedLinks)
            {
                link.SessionPlayer.IsInsideSharedSlot = false;
                link.SessionPlayer.IsCaptainEligible = false;
            }

            var remainingLinks = slot.Players
                .Except(removedLinks)
                .OrderBy(link => link.RotationOrder)
                .ToList();
            db.DraftSlotPlayers.RemoveRange(removedLinks);
            if (remainingLinks.Count < 2)
            {
                foreach (var link in remainingLinks)
                {
                    link.SessionPlayer.IsInsideSharedSlot = false;
                    link.SessionPlayer.IsCaptainEligible = true;
                }
                db.DraftSlotPlayers.RemoveRange(remainingLinks);
                db.DraftSlots.Remove(slot);
                continue;
            }

            for (var index = 0; index < remainingLinks.Count; index += 1)
            {
                remainingLinks[index].RotationOrder = index + 1;
                remainingLinks[index].SessionPlayer.IsInsideSharedSlot = true;
            }
            var remainingPlayers = remainingLinks.Select(link => link.SessionPlayer).ToList();
            slot.DisplayName = string.Join(" / ", remainingPlayers.Select(player => player.DisplayName));
            slot.Role = remainingPlayers[0].Role;
            slot.AverageScore = remainingPlayers.Average(player => player.Score);
            slot.Gender = remainingPlayers.All(player => player.Gender == remainingPlayers[0].Gender)
                ? remainingPlayers[0].Gender
                : PlayerGender.Unknown;
        }
    }

    private async Task CleanupDraftReminderTeamPreferenceGroupsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var groups = await db.TeamPreferenceGroups
            .Include(group => group.Players)
            .ThenInclude(link => link.SessionPlayer)
            .Where(group => group.SessionId == sessionId)
            .ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var inactiveLinks = group.Players
                .Where(link => !link.SessionPlayer.IsPresent)
                .ToList();
            var activeLinks = group.Players
                .Where(link => link.SessionPlayer.IsPresent)
                .OrderBy(link => link.RotationOrder)
                .ToList();
            if (activeLinks.Count < 2)
            {
                db.TeamPreferenceGroupPlayers.RemoveRange(group.Players);
                db.TeamPreferenceGroups.Remove(group);
                continue;
            }
            db.TeamPreferenceGroupPlayers.RemoveRange(inactiveLinks);
            for (var index = 0; index < activeLinks.Count; index += 1)
                activeLinks[index].RotationOrder = index + 1;
        }
    }

    private async Task SupersedeDraftReminderRequestAsync(
        ZaloDraftEscalationStore escalationStore,
        ZaloDraftEscalationSnapshot request,
        MatchSession session,
        CancellationToken cancellationToken)
    {
        await escalationStore.SetStateAsync(
            request.Id,
            ZaloDraftEscalationState.Superseded,
            cancellationToken);
        if (request.PrimaryApproverId is not null)
        {
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                request.PrimaryApproverId,
                session.Id,
                cancellationToken);
        }
        if (request.SecondaryApproverId is not null)
        {
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                request.SecondaryApproverId,
                session.Id,
                cancellationToken);
        }
    }
}
