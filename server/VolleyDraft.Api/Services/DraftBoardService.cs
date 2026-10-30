using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public static class DraftBoardStateToken
{
    public static string Create(IReadOnlyList<TeamPreviewResponse> teams)
    {
        var canonical = string.Join("|", teams
            .OrderBy(team => team.TeamId, StringComparer.Ordinal)
            .Select(team => string.Join(":",
                team.TeamId,
                team.CaptainName ?? string.Empty,
                string.Join(",", team.Slots
                    .OrderBy(slot => slot.Id, StringComparer.Ordinal)
                    .Select(slot => $"{slot.Id}>{team.TeamId}>{slot.DisplayName}>{slot.AverageScore:R}>{slot.IsCaptainSlot}")))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class DraftBoardService(
    VolleyDraftDbContext db,
    SessionDraftService draftService,
    ZaloBotActionHistoryService actionHistory)
{
    public async Task<ServiceResult<DraftStateResponse>> UpdateAsync(
        string adminUserId,
        string sessionId,
        UpdateDraftBoardRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedStateToken) || request.Assignments.Count == 0)
            return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Dữ liệu chỉnh đội hình chưa đầy đủ.");

        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentLease is null)
            return Failure<DraftStateResponse>(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return Failure<DraftStateResponse>(StatusCodes.Status409Conflict, "Đội hình đang được cập nhật ở nơi khác. Hãy thử lại sau ít giây.");

        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "ManualDraftBoardEdit")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)), cancellationToken);
        if (claimed == 0)
            return Failure<DraftStateResponse>(StatusCodes.Status409Conflict, "Đội hình vừa được cập nhật ở nơi khác. Hãy tải lại.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId, cancellationToken);
            if (session.Status != SessionStatus.Finished)
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Chỉ chỉnh tay sau khi draft đã hoàn tất.");

            var teams = await db.Teams
                .Include(team => team.CaptainSessionPlayer)
                .Where(team => team.SessionId == sessionId)
                .OrderBy(team => team.Name)
                .ToListAsync(cancellationToken);
            var teamIds = teams.Select(team => team.Id).ToHashSet(StringComparer.Ordinal);
            var slots = await db.DraftSlots
                .Include(slot => slot.Players)
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => slot.SessionId == sessionId && slot.AssignedTeamId != null)
                .OrderByDescending(slot => slot.IsCaptainSlot)
                .ThenBy(slot => slot.DisplayName)
                .ToListAsync(cancellationToken);
            var currentPreview = BuildPreview(teams, slots);
            if (!string.Equals(DraftBoardStateToken.Create(currentPreview), request.ExpectedStateToken, StringComparison.Ordinal))
                return Failure<DraftStateResponse>(StatusCodes.Status409Conflict, "Đội hình đã thay đổi sau khi bạn mở bảng chỉnh sửa. Hãy tải lại rồi thử lại.");

            if (request.Assignments.Count != slots.Count ||
                request.Assignments.Select(item => item.SlotId).Distinct(StringComparer.Ordinal).Count() != slots.Count)
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Phải giữ đủ toàn bộ slot khi lưu đội hình.");

            var assignmentBySlot = request.Assignments.ToDictionary(item => item.SlotId, StringComparer.Ordinal);
            if (slots.Any(slot => !assignmentBySlot.ContainsKey(slot.Id)) ||
                request.Assignments.Any(item => !teamIds.Contains(item.ExpectedTeamId) || !teamIds.Contains(item.TargetTeamId)))
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Có slot hoặc team không thuộc buổi đấu này.");

            foreach (var slot in slots)
            {
                var assignment = assignmentBySlot[slot.Id];
                if (!string.Equals(slot.AssignedTeamId, assignment.ExpectedTeamId, StringComparison.Ordinal))
                    return Failure<DraftStateResponse>(StatusCodes.Status409Conflict, "Một slot đã được chuyển sang team khác. Hãy tải lại bảng đội hình.");
            }

            var currentCounts = slots.GroupBy(slot => slot.AssignedTeamId!)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var targetCounts = request.Assignments.GroupBy(item => item.TargetTeamId)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            if (teamIds.Any(teamId => currentCounts.GetValueOrDefault(teamId) != targetCounts.GetValueOrDefault(teamId)))
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Mỗi team phải giữ nguyên số slot. Hãy chuyển bù một slot trước khi lưu.");

            var changed = slots.Where(slot => !string.Equals(slot.AssignedTeamId, assignmentBySlot[slot.Id].TargetTeamId, StringComparison.Ordinal)).ToList();
            if (changed.Count == 0)
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, "Đội hình chưa có thay đổi để lưu.");

            var captainTransfers = new List<(Team Team, DraftSlot PreviousSlot, DraftSlot NextSlot, SessionPlayer? PreviousCaptain, SessionPlayer NextCaptain)>();
            foreach (var team in teams)
            {
                var captainSlots = slots
                    .Where(slot => string.Equals(slot.AssignedTeamId, team.Id, StringComparison.Ordinal) && slot.IsCaptainSlot)
                    .ToList();
                if (captainSlots.Count != 1)
                    return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, $"{team.Name} phải có đúng một slot đội trưởng trước khi chỉnh đội hình.");

                var captainSlot = captainSlots[0];
                var captainTargetTeamId = assignmentBySlot[captainSlot.Id].TargetTeamId;
                if (string.Equals(captainTargetTeamId, team.Id, StringComparison.Ordinal))
                    continue;

                var incomingSlots = slots
                    .Where(slot =>
                        !string.Equals(slot.AssignedTeamId, team.Id, StringComparison.Ordinal) &&
                        string.Equals(assignmentBySlot[slot.Id].TargetTeamId, team.Id, StringComparison.Ordinal))
                    .ToList();
                var reciprocalSlots = incomingSlots
                    .Where(slot => string.Equals(slot.AssignedTeamId, captainTargetTeamId, StringComparison.Ordinal))
                    .ToList();
                var replacementSlot = reciprocalSlots.Count == 1
                    ? reciprocalSlots[0]
                    : incomingSlots.Count == 1
                        ? incomingSlots[0]
                        : null;
                if (replacementSlot is null)
                    return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest,
                        $"Khi chuyển đội trưởng {captainSlot.DisplayName}, hãy đổi trực tiếp với một slot khác để xác định đội trưởng mới của {team.Name}.");

                var nextCaptain = replacementSlot.Players
                    .OrderBy(link => link.RotationOrder)
                    .Select(link => link.SessionPlayer)
                    .FirstOrDefault();
                if (nextCaptain is null)
                    return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, $"Slot {replacementSlot.DisplayName} không có người để nhận vai trò đội trưởng.");

                var previousCaptain = captainSlot.Players
                    .Where(link => string.Equals(link.SessionPlayerId, team.CaptainSessionPlayerId, StringComparison.Ordinal))
                    .Select(link => link.SessionPlayer)
                    .FirstOrDefault();
                captainTransfers.Add((team, captainSlot, replacementSlot, previousCaptain, nextCaptain));
            }

            var relationshipOverrides = slots
                .SelectMany(slot => slot.Players.Select(link => new
                {
                    link.SessionPlayerId,
                    TeamId = (string?)assignmentBySlot[slot.Id].TargetTeamId
                }))
                .ToDictionary(
                    item => item.SessionPlayerId,
                    item => item.TeamId,
                    StringComparer.Ordinal);
            var relationshipError = await draftService.ValidateAssignedTeamRelationshipsAsync(
                sessionId,
                slots,
                relationshipOverrides,
                cancellationToken: cancellationToken);
            if (relationshipError is not null)
                return Failure<DraftStateResponse>(StatusCodes.Status400BadRequest, relationshipError);

            var before = await actionHistory.CaptureAsync(sessionId, cancellationToken);
            foreach (var slot in changed)
                slot.AssignedTeamId = assignmentBySlot[slot.Id].TargetTeamId;
            foreach (var transfer in captainTransfers)
            {
                transfer.PreviousSlot.IsCaptainSlot = false;
                if (transfer.PreviousCaptain is not null)
                    transfer.PreviousCaptain.IsCaptainEligible = false;
            }
            foreach (var transfer in captainTransfers)
            {
                transfer.NextSlot.IsCaptainSlot = true;
                transfer.NextCaptain.IsCaptainEligible = true;
                transfer.Team.CaptainSessionPlayerId = transfer.NextCaptain.Id;
                transfer.Team.CaptainSessionPlayer = transfer.NextCaptain;
            }
            foreach (var team in teams)
                team.TotalAverageScore = slots.Where(slot => slot.AssignedTeamId == team.Id).Sum(slot => slot.AverageScore);
            session.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            var affectedTeams = changed
                .SelectMany(slot => new[] { assignmentBySlot[slot.Id].ExpectedTeamId, assignmentBySlot[slot.Id].TargetTeamId })
                .Distinct(StringComparer.Ordinal)
                .Select(teamId => teams.Single(team => team.Id == teamId).Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            await actionHistory.RecordAsync(
                sessionId,
                adminUserId,
                "Admin website",
                "ManualDraftBoardEdit",
                $"Chỉnh đội hình thủ công giữa {string.Join(", ", affectedTeams)}",
                before,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await draftService.GetDraftStateAsync(adminUserId, sessionId);
        }
        finally
        {
            await db.MatchSessions
                .Where(session => session.Id == sessionId && session.BotActionLeaseToken == leaseToken)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(session => session.BotActionLeaseToken, (string?)null)
                    .SetProperty(session => session.BotActionLeaseName, (string?)null)
                    .SetProperty(session => session.BotActionLeaseUntil, (DateTimeOffset?)null), cancellationToken);
        }
    }

    public Task<ServiceResult<PagedResponse<DraftSnapshotResponse>>> GetSnapshotsAsync(
        string adminUserId,
        string sessionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        actionHistory.GetDraftSnapshotsAsync(adminUserId, sessionId, page, pageSize, cancellationToken);

    public Task<ServiceResult<DraftSnapshotResponse>> CreateSnapshotAsync(
        string adminUserId,
        string sessionId,
        CreateDraftSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        actionHistory.CreateDraftSnapshotAsync(adminUserId, sessionId, request.Name, "Admin website", cancellationToken);

    public async Task<ServiceResult<DraftStateResponse>> RestoreSnapshotAsync(
        string adminUserId,
        string sessionId,
        string snapshotId,
        RestoreDraftSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var restored = await actionHistory.RestoreDraftSnapshotAsync(
            adminUserId, sessionId, snapshotId, request.ExpectedStateToken, "Admin website", cancellationToken);
        return !restored.IsSuccess
            ? Failure<DraftStateResponse>(restored.StatusCode, restored.Error ?? "Không thể khôi phục snapshot.")
            : await draftService.GetDraftStateAsync(adminUserId, sessionId);
    }

    public Task<ServiceResult<DeleteResponse>> DeleteSnapshotAsync(
        string adminUserId,
        string sessionId,
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        actionHistory.DeleteDraftSnapshotAsync(adminUserId, sessionId, snapshotId, cancellationToken);

    private static IReadOnlyList<TeamPreviewResponse> BuildPreview(IReadOnlyList<Team> teams, IReadOnlyList<DraftSlot> slots) =>
        teams.Select(team => new TeamPreviewResponse(
            team.Id,
            team.Name,
            team.CaptainSessionPlayer?.DisplayName,
            slots.Where(slot => slot.AssignedTeamId == team.Id)
                .Select(slot => new TeamSlotPreviewResponse(
                    slot.Id, slot.DisplayName, slot.Type, slot.Gender,
                    slot.IsCaptainSlot, slot.AverageScore))
                .ToList()))
            .ToList();

    private static ServiceResult<T> Failure<T>(int statusCode, string error) =>
        ServiceResult<T>.Failure(statusCode, error);
}
