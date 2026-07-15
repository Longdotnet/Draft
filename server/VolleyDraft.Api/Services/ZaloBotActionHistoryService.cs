using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record BotSessionStateCapture(string Json, string Hash);

public sealed class ZaloBotActionHistoryService(VolleyDraftDbContext db, ILogger<ZaloBotActionHistoryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BotSessionStateCapture> CaptureAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions.AsNoTracking()
            .SingleAsync(item => item.Id == sessionId, cancellationToken);
        var players = await db.SessionPlayers.AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new PlayerState(
                item.Id, item.UserId, item.PlayerProfileId, item.DisplayName, item.AvatarUrl,
                item.Role, item.Level, item.Gender, item.Score, item.IsPresent,
                item.IsCaptainEligible, item.IsInsideSharedSlot, item.SourcePollId,
                item.SourceOptionIdsJson, item.CreatedAt))
            .ToListAsync(cancellationToken);
        var profileIds = players.Where(item => item.PlayerProfileId is not null)
            .Select(item => item.PlayerProfileId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var profiles = await db.PlayerProfiles.AsNoTracking()
            .Where(item => profileIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new ProfileState(
                item.Id, item.ZaloUserId, item.DisplayName, item.AvatarUrl, item.Gender,
                item.DefaultRole, item.DefaultLevel, item.LastSyncedAt, item.GenderUpdatedAt,
                item.GenderUpdatedByUserId, item.CreatedAt, item.UpdatedAt))
            .ToListAsync(cancellationToken);
        var teams = await db.Teams.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new TeamState(item.Id, item.Name, item.CaptainSessionPlayerId, item.TotalAverageScore, item.CreatedAt))
            .ToListAsync(cancellationToken);
        var slots = await db.DraftSlots.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new SlotState(item.Id, item.Type, item.DisplayName, item.Role, item.Gender,
                item.AverageScore, item.AssignedTeamId, item.IsCaptainSlot, item.CreatedAt))
            .ToListAsync(cancellationToken);
        var slotIds = slots.Select(item => item.Id).ToList();
        var slotPlayers = await db.DraftSlotPlayers.AsNoTracking().Where(item => slotIds.Contains(item.DraftSlotId))
            .OrderBy(item => item.Id)
            .Select(item => new SlotPlayerState(item.Id, item.DraftSlotId, item.SessionPlayerId, item.RotationOrder))
            .ToListAsync(cancellationToken);
        var preferenceGroups = await db.TeamPreferenceGroups.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new PreferenceGroupState(item.Id, item.CreatedAt))
            .ToListAsync(cancellationToken);
        var preferenceIds = preferenceGroups.Select(item => item.Id).ToList();
        var preferencePlayers = await db.TeamPreferenceGroupPlayers.AsNoTracking()
            .Where(item => preferenceIds.Contains(item.TeamPreferenceGroupId))
            .OrderBy(item => item.TeamPreferenceGroupId).ThenBy(item => item.SessionPlayerId)
            .Select(item => new PreferencePlayerState(item.TeamPreferenceGroupId, item.SessionPlayerId, item.RotationOrder))
            .ToListAsync(cancellationToken);
        var rounds = await db.DraftRounds.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new RoundState(item.Id, item.RoundNumber, item.Label, item.Status, item.CreatedAt))
            .ToListAsync(cancellationToken);
        var bags = await db.BlindBags.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new BagState(item.Id, item.RoundId, item.DraftSlotId, item.PreparedDraftSlotId,
                item.BagNumber, item.IsOpened, item.OpenedByUserId, item.OpenedForTeamId, item.OpenedAt))
            .ToListAsync(cancellationToken);
        var turns = await db.DraftTurns.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new TurnState(item.Id, item.RoundId, item.TeamId, item.CaptainSessionPlayerId,
                item.TurnOrder, item.Status, item.OpenedBagId, item.CreatedAt, item.CompletedAt))
            .ToListAsync(cancellationToken);
        var reminders = await db.ZaloReminderSchedules.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new ReminderState(item.Id, item.CreatedBySenderId, item.CreatedBySenderName,
                item.Message, item.Audience, item.OnlyIfMissingSlots, item.StopWhenFull,
                item.AllowAfterSessionStart, item.IncludePaymentQr, item.Repeats,
                item.IntervalMinutes, item.Enabled, item.NextRunAt, item.LastRunAt, item.FailureCount,
                item.LastError, item.CreatedAt, item.UpdatedAt))
            .ToListAsync(cancellationToken);
        var waitlist = await db.SessionWaitlistEntries.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new WaitlistState(item.Id, item.ZaloUserId, item.DisplayName, item.Status,
                item.SessionPlayerId, item.InvitedAt, item.InviteExpiresAt, item.AcceptedAt,
                item.LastNotifiedAt, item.CreatedAt, item.UpdatedAt, item.Version))
            .ToListAsync(cancellationToken);
        var imports = await db.PollImports.AsNoTracking().Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.Id)
            .Select(item => new PollImportState(item.Id, item.ImportedByUserId, item.ZaloGroupId,
                item.PollId, item.PollQuestion, item.SelectedOptionIdsJson, item.ImportedPlayerCount, item.ImportedAt))
            .ToListAsync(cancellationToken);

        var snapshot = new SessionState(
            new MatchState(
                session.Name, session.StartTime, session.Location, session.ParkingInstructions,
                session.LocationImageUrl, session.PaymentInstructions, session.PaymentQrImageUrl,
                session.BotEnabled, session.BotCustomInstructions, session.BotOperatorZaloUserIdsJson,
                session.ReminderEnabled, session.ReminderLeadHours, session.ReminderIntervalHours,
                session.ReminderIntervalMinutes, session.ReminderRepeats, session.LastReminderAt,
                session.NextReminderAt, session.ReminderLastKnownPlayerCount, session.ReminderFailureCount,
                session.LastReminderError, session.Status, session.TeamCount, session.TeamSize,
                session.TotalSets, session.CurrentRoundNumber, session.CurrentTurnTeamId,
                session.CurrentTurnCaptainSessionPlayerId, session.UpdatedAt),
            players, profiles, teams, slots, slotPlayers, preferenceGroups, preferencePlayers,
            rounds, bags, turns, reminders, waitlist, imports);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return new BotSessionStateCapture(json, Hash(json));
    }

    public async Task<ZaloBotActionHistory?> RecordAsync(
        string sessionId,
        string? actorZaloUserId,
        string actorName,
        string actionType,
        string summary,
        BotSessionStateCapture before,
        CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var after = await CaptureAsync(sessionId, cancellationToken);
        if (before.Hash == after.Hash) return null;
        var action = new ZaloBotActionHistory
        {
            SessionId = sessionId,
            ActorZaloUserId = Clean(actorZaloUserId, 100),
            ActorName = Clean(actorName, 160) ?? "Hệ thống",
            ActionType = Clean(actionType, 80) ?? "Unknown",
            Summary = Clean(summary, 1000) ?? "Cập nhật dữ liệu buổi đấu",
            BeforeStateJson = before.Json,
            AfterStateJson = after.Json,
            BeforeHash = before.Hash,
            AfterHash = after.Hash,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotActionHistory.Add(action);
        await db.SaveChangesAsync(cancellationToken);

        var activeRows = await db.ZaloBotActionHistory.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.IsUndoable && item.UndoneAt == null)
            .Select(item => new { item.Id, item.CreatedAt })
            .ToListAsync(cancellationToken);
        var staleIds = activeRows
            .OrderByDescending(item => item.CreatedAt)
            .Skip(30)
            .Select(item => item.Id)
            .ToList();
        if (staleIds.Count > 0)
        {
            await db.ZaloBotActionHistory.Where(item => staleIds.Contains(item.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsUndoable, false), cancellationToken);
        }
        return action;
    }

    public async Task<ServiceResult<IReadOnlyList<ZaloBotActionHistoryResponse>>> GetHistoryAsync(
        string adminUserId,
        string sessionId,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        if (!await db.MatchSessions.AsNoTracking().AnyAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId, cancellationToken))
            return ServiceResult<IReadOnlyList<ZaloBotActionHistoryResponse>>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        var allRows = await db.ZaloBotActionHistory.AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .Select(item => new ZaloBotActionHistoryResponse(
                item.Id, item.SessionId, item.ActorZaloUserId, item.ActorName, item.ActionType,
                item.Summary, item.IsUndoable, item.CreatedAt, item.UndoneAt,
                item.UndoneByZaloUserId, item.UndoFailure))
            .ToListAsync(cancellationToken);
        var rows = allRows.OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(count, 1, 50))
            .ToList();
        return ServiceResult<IReadOnlyList<ZaloBotActionHistoryResponse>>.Success(rows);
    }

    public async Task<ZaloBotActionHistory?> GetLatestUndoableAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var rows = await db.ZaloBotActionHistory.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.IsUndoable && item.UndoneAt == null)
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
    }

    public async Task<ServiceResult<PagedResponse<DraftSnapshotResponse>>> GetDraftSnapshotsAsync(
        string adminUserId,
        string sessionId,
        int page = 1,
        int pageSize = 6,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsSessionAsync(adminUserId, sessionId, cancellationToken))
            return ServiceResult<PagedResponse<DraftSnapshotResponse>>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);
        var query = db.ZaloBotActionHistory.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.ActionType == "DraftSnapshot");
        var total = await query.CountAsync(cancellationToken);
        var allRows = await query
            .Select(item => new DraftSnapshotResponse(
                item.Id, item.SessionId, item.Summary, item.ActorName, item.CreatedAt, item.AfterHash))
            .ToListAsync(cancellationToken);
        var rows = allRows.OrderByDescending(item => item.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return ServiceResult<PagedResponse<DraftSnapshotResponse>>.Success(new(
            rows,
            page,
            pageSize,
            total,
            Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))));
    }

    public async Task<ServiceResult<DraftSnapshotResponse>> CreateDraftSnapshotAsync(
        string adminUserId,
        string sessionId,
        string? name,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId, cancellationToken);
        if (session is null)
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (session.Status != SessionStatus.Finished ||
            !await db.DraftSlots.AsNoTracking().AnyAsync(item => item.SessionId == sessionId && item.AssignedTeamId != null, cancellationToken))
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status400BadRequest, "Chỉ lưu snapshot sau khi đã có đội hình hoàn chỉnh.");

        var capture = await CaptureAsync(sessionId, cancellationToken);
        var localNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var snapshotName = Clean(name, 160) ?? $"Đội hình {localNow:dd/MM HH:mm}";
        var row = new ZaloBotActionHistory
        {
            SessionId = sessionId,
            ActorZaloUserId = Clean(adminUserId, 100),
            ActorName = Clean(actorName, 160) ?? "Admin website",
            ActionType = "DraftSnapshot",
            Summary = snapshotName,
            BeforeStateJson = capture.Json,
            AfterStateJson = capture.Json,
            BeforeHash = capture.Hash,
            AfterHash = capture.Hash,
            IsUndoable = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotActionHistory.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<DraftSnapshotResponse>.Success(ToSnapshotResponse(row));
    }

    public async Task<ServiceResult<DraftSnapshotResponse>> RestoreDraftSnapshotAsync(
        string adminUserId,
        string sessionId,
        string snapshotId,
        string expectedStateToken,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil, session.StartTime })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentLease is null)
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (currentLease.StartTime is not null && currentLease.StartTime <= now)
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status400BadRequest, "Buổi đấu đã bắt đầu nên không thể khôi phục snapshot đội hình.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status409Conflict, "Đội hình đang được cập nhật ở nơi khác.");

        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "RestoreDraftSnapshot")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(3)), cancellationToken);
        if (claimed == 0)
            return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status409Conflict, "Đội hình vừa được cập nhật ở nơi khác. Hãy tải lại.");

        try
        {
            var snapshotRow = await db.ZaloBotActionHistory.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == snapshotId && item.SessionId == sessionId && item.ActionType == "DraftSnapshot", cancellationToken);
            if (snapshotRow is null)
                return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy snapshot đội hình.");
            if (!string.Equals(await GetDraftStateTokenAsync(sessionId, cancellationToken), expectedStateToken, StringComparison.Ordinal))
                return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status409Conflict, "Đội hình đã thay đổi. Hãy tải lại trước khi khôi phục snapshot.");

            SessionState? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<SessionState>(snapshotRow.AfterStateJson, JsonOptions);
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Invalid draft snapshot {SnapshotId}", snapshotId);
                snapshot = null;
            }
            if (snapshot is null)
                return ServiceResult<DraftSnapshotResponse>.Failure(StatusCodes.Status500InternalServerError, "Snapshot đội hình không hợp lệ.");

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var before = await CaptureAsync(sessionId, cancellationToken);
            await RestoreDraftOnlyAsync(sessionId, snapshot, cancellationToken);
            await RecordAsync(
                sessionId,
                adminUserId,
                actorName,
                "DraftSnapshotRestore",
                $"Khôi phục snapshot “{snapshotRow.Summary}”",
                before,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<DraftSnapshotResponse>.Success(ToSnapshotResponse(snapshotRow));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not restore draft snapshot {SnapshotId}", snapshotId);
            return ServiceResult<DraftSnapshotResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Không thể khôi phục snapshot. Không có thay đổi dở dang nào được lưu.");
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

    public async Task<ServiceResult<DeleteResponse>> DeleteDraftSnapshotAsync(
        string adminUserId,
        string sessionId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsSessionAsync(adminUserId, sessionId, cancellationToken))
            return ServiceResult<DeleteResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        var deleted = await db.ZaloBotActionHistory
            .Where(item => item.Id == snapshotId && item.SessionId == sessionId && item.ActionType == "DraftSnapshot")
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0
            ? ServiceResult<DeleteResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy snapshot đội hình.")
            : ServiceResult<DeleteResponse>.Success(new DeleteResponse("Đã xoá snapshot đội hình."));
    }

    public async Task<ServiceResult<ZaloBotActionHistoryResponse>> UndoAsync(
        string adminUserId,
        string sessionId,
        string actionId,
        string undoneByZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var ownsSession = await db.MatchSessions.AsNoTracking()
            .AnyAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId, cancellationToken);
        if (!ownsSession)
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        var action = await db.ZaloBotActionHistory.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actionId && item.SessionId == sessionId, cancellationToken);
        if (action is null)
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy thao tác cần hoàn tác.");
        if (!action.IsUndoable || action.UndoneAt is not null)
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(StatusCodes.Status409Conflict, "Thao tác này không còn có thể hoàn tác.");

        db.ChangeTracker.Clear();
        var current = await CaptureAsync(sessionId, cancellationToken);
        if (current.Hash != action.AfterHash)
        {
            const string reason = "Dữ liệu buổi đấu đã thay đổi sau thao tác này. Bot không tự ghi đè thay đổi mới hơn.";
            await db.ZaloBotActionHistory.Where(item => item.Id == actionId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UndoFailure, reason), cancellationToken);
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(StatusCodes.Status409Conflict, reason);
        }

        SessionState? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SessionState>(action.BeforeStateJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Invalid bot action snapshot {ActionId}", actionId);
            snapshot = null;
        }
        if (snapshot is null)
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(StatusCodes.Status500InternalServerError, "Bản lưu hoàn tác không hợp lệ.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await RestoreAsync(sessionId, snapshot, cancellationToken);
            var stored = await db.ZaloBotActionHistory.SingleAsync(item => item.Id == actionId, cancellationToken);
            stored.UndoneAt = DateTimeOffset.UtcNow;
            stored.UndoneByZaloUserId = Clean(undoneByZaloUserId, 100);
            stored.UndoFailure = null;
            stored.IsUndoable = false;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<ZaloBotActionHistoryResponse>.Success(ToResponse(stored));
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(exception, "Could not undo bot action {ActionId}", actionId);
            return ServiceResult<ZaloBotActionHistoryResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Không thể khôi phục dữ liệu của thao tác này. Không có thay đổi dở dang nào được lưu.");
        }
    }

    private Task<bool> OwnsSessionAsync(string adminUserId, string sessionId, CancellationToken cancellationToken) =>
        db.MatchSessions.AsNoTracking()
            .AnyAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId, cancellationToken);

    private async Task<string> GetDraftStateTokenAsync(string sessionId, CancellationToken cancellationToken)
    {
        var teams = await db.Teams.AsNoTracking()
            .Include(team => team.CaptainSessionPlayer)
            .Where(team => team.SessionId == sessionId)
            .OrderBy(team => team.Name)
            .ToListAsync(cancellationToken);
        var teamIds = teams.Select(team => team.Id).ToList();
        var slots = await db.DraftSlots.AsNoTracking()
            .Where(slot => slot.AssignedTeamId != null && teamIds.Contains(slot.AssignedTeamId))
            .OrderByDescending(slot => slot.IsCaptainSlot)
            .ThenBy(slot => slot.DisplayName)
            .ToListAsync(cancellationToken);
        var preview = teams.Select(team => new TeamPreviewResponse(
            team.Id,
            team.Name,
            team.CaptainSessionPlayer?.DisplayName,
            slots.Where(slot => slot.AssignedTeamId == team.Id)
                .Select(slot => new TeamSlotPreviewResponse(
                    slot.Id, slot.DisplayName, slot.Type, slot.Gender,
                    slot.IsCaptainSlot, slot.AverageScore))
                .ToList()))
            .ToList();
        return DraftBoardStateToken.Create(preview);
    }

    private async Task RestoreDraftOnlyAsync(
        string sessionId,
        SessionState snapshot,
        CancellationToken cancellationToken)
    {
        var waitlistPlayerLinks = await db.SessionWaitlistEntries
            .Where(item => item.SessionId == sessionId && item.SessionPlayerId != null)
            .Select(item => new { item.Id, item.SessionPlayerId })
            .ToListAsync(cancellationToken);

        await db.Teams.Where(item => item.SessionId == sessionId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CaptainSessionPlayerId, (string?)null), cancellationToken);
        await db.MatchSessions.Where(item => item.Id == sessionId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CurrentTurnTeamId, (string?)null)
                .SetProperty(item => item.CurrentTurnCaptainSessionPlayerId, (string?)null), cancellationToken);
        await db.DraftTurns.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.BlindBags.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.DraftRounds.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        var preferenceIds = await db.TeamPreferenceGroups.Where(item => item.SessionId == sessionId)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        await db.TeamPreferenceGroupPlayers.Where(item => preferenceIds.Contains(item.TeamPreferenceGroupId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.TeamPreferenceGroups.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        var slotIds = await db.DraftSlots.Where(item => item.SessionId == sessionId)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        await db.DraftSlotPlayers.Where(item => slotIds.Contains(item.DraftSlotId)).ExecuteDeleteAsync(cancellationToken);
        await db.DraftSlots.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.SessionPlayers.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var profileIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in snapshot.Profiles)
        {
            var profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.Id == state.Id, cancellationToken);
            if (profile is null && !string.IsNullOrWhiteSpace(state.ZaloUserId))
                profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.ZaloUserId == state.ZaloUserId, cancellationToken);
            if (profile is null)
            {
                profile = new PlayerProfile
                {
                    Id = state.Id,
                    ZaloUserId = state.ZaloUserId,
                    DisplayName = state.DisplayName,
                    AvatarUrl = state.AvatarUrl,
                    Gender = state.Gender,
                    DefaultRole = state.DefaultRole,
                    DefaultLevel = state.DefaultLevel,
                    LastSyncedAt = state.LastSyncedAt,
                    GenderUpdatedAt = state.GenderUpdatedAt,
                    GenderUpdatedByUserId = state.GenderUpdatedByUserId,
                    CreatedAt = state.CreatedAt,
                    UpdatedAt = state.UpdatedAt
                };
                db.PlayerProfiles.Add(profile);
                await db.SaveChangesAsync(cancellationToken);
            }
            profileIdMap[state.Id] = profile.Id;
        }

        db.SessionPlayers.AddRange(snapshot.Players.Select(state => new SessionPlayer
        {
            Id = state.Id,
            SessionId = sessionId,
            UserId = state.UserId,
            PlayerProfileId = state.PlayerProfileId is not null && profileIdMap.TryGetValue(state.PlayerProfileId, out var mappedProfileId)
                ? mappedProfileId
                : null,
            DisplayName = state.DisplayName,
            AvatarUrl = state.AvatarUrl,
            Role = state.Role,
            Level = state.Level,
            Gender = state.Gender,
            Score = state.Score,
            IsPresent = state.IsPresent,
            IsCaptainEligible = state.IsCaptainEligible,
            IsInsideSharedSlot = state.IsInsideSharedSlot,
            SourcePollId = state.SourcePollId,
            SourceOptionIdsJson = state.SourceOptionIdsJson,
            CreatedAt = state.CreatedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftSlots.AddRange(snapshot.Slots.Select(state => new DraftSlot
        {
            Id = state.Id,
            SessionId = sessionId,
            Type = state.Type,
            DisplayName = state.DisplayName,
            Role = state.Role,
            Gender = state.Gender,
            AverageScore = state.AverageScore,
            AssignedTeamId = state.AssignedTeamId,
            IsCaptainSlot = state.IsCaptainSlot,
            CreatedAt = state.CreatedAt
        }));
        db.TeamPreferenceGroups.AddRange(snapshot.PreferenceGroups.Select(state => new TeamPreferenceGroup
        {
            Id = state.Id,
            SessionId = sessionId,
            CreatedAt = state.CreatedAt
        }));
        db.DraftRounds.AddRange(snapshot.Rounds.Select(state => new DraftRound
        {
            Id = state.Id,
            SessionId = sessionId,
            RoundNumber = state.RoundNumber,
            Label = state.Label,
            Status = state.Status,
            CreatedAt = state.CreatedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftSlotPlayers.AddRange(snapshot.SlotPlayers.Select(state => new DraftSlotPlayer
        {
            Id = state.Id,
            DraftSlotId = state.DraftSlotId,
            SessionPlayerId = state.SessionPlayerId,
            RotationOrder = state.RotationOrder
        }));
        db.TeamPreferenceGroupPlayers.AddRange(snapshot.PreferencePlayers.Select(state => new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = state.TeamPreferenceGroupId,
            SessionPlayerId = state.SessionPlayerId,
            RotationOrder = state.RotationOrder
        }));
        db.BlindBags.AddRange(snapshot.Bags.Select(state => new BlindBag
        {
            Id = state.Id,
            SessionId = sessionId,
            RoundId = state.RoundId,
            DraftSlotId = state.DraftSlotId,
            PreparedDraftSlotId = state.PreparedDraftSlotId,
            BagNumber = state.BagNumber,
            IsOpened = state.IsOpened,
            OpenedByUserId = state.OpenedByUserId,
            OpenedForTeamId = state.OpenedForTeamId,
            OpenedAt = state.OpenedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftTurns.AddRange(snapshot.Turns.Select(state => new DraftTurn
        {
            Id = state.Id,
            SessionId = sessionId,
            RoundId = state.RoundId,
            TeamId = state.TeamId,
            CaptainSessionPlayerId = state.CaptainSessionPlayerId,
            TurnOrder = state.TurnOrder,
            Status = state.Status,
            OpenedBagId = state.OpenedBagId,
            CreatedAt = state.CreatedAt,
            CompletedAt = state.CompletedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId, cancellationToken);
        session.Status = snapshot.Match.Status;
        session.TeamCount = snapshot.Match.TeamCount;
        session.TeamSize = snapshot.Match.TeamSize;
        session.CurrentRoundNumber = snapshot.Match.CurrentRoundNumber;
        session.CurrentTurnTeamId = snapshot.Match.CurrentTurnTeamId;
        session.CurrentTurnCaptainSessionPlayerId = snapshot.Match.CurrentTurnCaptainSessionPlayerId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var teamState in snapshot.Teams)
        {
            var team = await db.Teams.SingleAsync(item => item.Id == teamState.Id && item.SessionId == sessionId, cancellationToken);
            team.Name = teamState.Name;
            team.CaptainSessionPlayerId = teamState.CaptainSessionPlayerId;
            team.TotalAverageScore = teamState.TotalAverageScore;
        }
        await db.SaveChangesAsync(cancellationToken);

        var restoredPlayerIds = snapshot.Players.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var link in waitlistPlayerLinks.Where(link => link.SessionPlayerId is not null && restoredPlayerIds.Contains(link.SessionPlayerId)))
        {
            await db.SessionWaitlistEntries.Where(item => item.Id == link.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SessionPlayerId, link.SessionPlayerId), cancellationToken);
        }
    }

    private async Task RestoreAsync(string sessionId, SessionState snapshot, CancellationToken cancellationToken)
    {
        await db.Teams.Where(item => item.SessionId == sessionId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CaptainSessionPlayerId, (string?)null), cancellationToken);
        await db.MatchSessions.Where(item => item.Id == sessionId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CurrentTurnTeamId, (string?)null)
                .SetProperty(item => item.CurrentTurnCaptainSessionPlayerId, (string?)null), cancellationToken);
        await db.SessionWaitlistEntries.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.DraftTurns.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.BlindBags.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.DraftRounds.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        var preferenceIds = await db.TeamPreferenceGroups.Where(item => item.SessionId == sessionId).Select(item => item.Id).ToListAsync(cancellationToken);
        await db.TeamPreferenceGroupPlayers.Where(item => preferenceIds.Contains(item.TeamPreferenceGroupId)).ExecuteDeleteAsync(cancellationToken);
        await db.TeamPreferenceGroups.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        var slotIds = await db.DraftSlots.Where(item => item.SessionId == sessionId).Select(item => item.Id).ToListAsync(cancellationToken);
        await db.DraftSlotPlayers.Where(item => slotIds.Contains(item.DraftSlotId)).ExecuteDeleteAsync(cancellationToken);
        await db.DraftSlots.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.ZaloReminderSchedules.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.PollImports.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        await db.SessionPlayers.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();

        foreach (var state in snapshot.Profiles)
        {
            var profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.Id == state.Id, cancellationToken);
            if (profile is null)
            {
                profile = new PlayerProfile { Id = state.Id, ZaloUserId = state.ZaloUserId };
                db.PlayerProfiles.Add(profile);
            }
            profile.ZaloUserId = state.ZaloUserId;
            profile.DisplayName = state.DisplayName;
            profile.AvatarUrl = state.AvatarUrl;
            profile.Gender = state.Gender;
            profile.DefaultRole = state.DefaultRole;
            profile.DefaultLevel = state.DefaultLevel;
            profile.LastSyncedAt = state.LastSyncedAt;
            profile.GenderUpdatedAt = state.GenderUpdatedAt;
            profile.GenderUpdatedByUserId = state.GenderUpdatedByUserId;
            profile.CreatedAt = state.CreatedAt;
            profile.UpdatedAt = state.UpdatedAt;
        }
        await db.SaveChangesAsync(cancellationToken);

        db.SessionPlayers.AddRange(snapshot.Players.Select(state => new SessionPlayer
        {
            Id = state.Id, SessionId = sessionId, UserId = state.UserId, PlayerProfileId = state.PlayerProfileId,
            DisplayName = state.DisplayName, AvatarUrl = state.AvatarUrl, Role = state.Role, Level = state.Level,
            Gender = state.Gender, Score = state.Score, IsPresent = state.IsPresent,
            IsCaptainEligible = state.IsCaptainEligible, IsInsideSharedSlot = state.IsInsideSharedSlot,
            SourcePollId = state.SourcePollId, SourceOptionIdsJson = state.SourceOptionIdsJson, CreatedAt = state.CreatedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftSlots.AddRange(snapshot.Slots.Select(state => new DraftSlot
        {
            Id = state.Id, SessionId = sessionId, Type = state.Type, DisplayName = state.DisplayName,
            Role = state.Role, Gender = state.Gender, AverageScore = state.AverageScore,
            AssignedTeamId = state.AssignedTeamId, IsCaptainSlot = state.IsCaptainSlot, CreatedAt = state.CreatedAt
        }));
        db.TeamPreferenceGroups.AddRange(snapshot.PreferenceGroups.Select(state => new TeamPreferenceGroup
        {
            Id = state.Id, SessionId = sessionId, CreatedAt = state.CreatedAt
        }));
        db.DraftRounds.AddRange(snapshot.Rounds.Select(state => new DraftRound
        {
            Id = state.Id, SessionId = sessionId, RoundNumber = state.RoundNumber,
            Label = state.Label, Status = state.Status, CreatedAt = state.CreatedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftSlotPlayers.AddRange(snapshot.SlotPlayers.Select(state => new DraftSlotPlayer
        {
            Id = state.Id, DraftSlotId = state.DraftSlotId, SessionPlayerId = state.SessionPlayerId, RotationOrder = state.RotationOrder
        }));
        db.TeamPreferenceGroupPlayers.AddRange(snapshot.PreferencePlayers.Select(state => new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = state.TeamPreferenceGroupId, SessionPlayerId = state.SessionPlayerId, RotationOrder = state.RotationOrder
        }));
        db.BlindBags.AddRange(snapshot.Bags.Select(state => new BlindBag
        {
            Id = state.Id, SessionId = sessionId, RoundId = state.RoundId, DraftSlotId = state.DraftSlotId,
            PreparedDraftSlotId = state.PreparedDraftSlotId, BagNumber = state.BagNumber, IsOpened = state.IsOpened,
            OpenedByUserId = state.OpenedByUserId, OpenedForTeamId = state.OpenedForTeamId, OpenedAt = state.OpenedAt
        }));
        await db.SaveChangesAsync(cancellationToken);

        db.DraftTurns.AddRange(snapshot.Turns.Select(state => new DraftTurn
        {
            Id = state.Id, SessionId = sessionId, RoundId = state.RoundId, TeamId = state.TeamId,
            CaptainSessionPlayerId = state.CaptainSessionPlayerId, TurnOrder = state.TurnOrder,
            Status = state.Status, OpenedBagId = state.OpenedBagId, CreatedAt = state.CreatedAt, CompletedAt = state.CompletedAt
        }));
        db.ZaloReminderSchedules.AddRange(snapshot.Reminders.Select(state => new ZaloReminderSchedule
        {
            Id = state.Id, SessionId = sessionId, CreatedBySenderId = state.CreatedBySenderId,
            CreatedBySenderName = state.CreatedBySenderName, Message = state.Message, Audience = state.Audience,
            OnlyIfMissingSlots = state.OnlyIfMissingSlots, StopWhenFull = state.StopWhenFull,
            AllowAfterSessionStart = state.AllowAfterSessionStart, IncludePaymentQr = state.IncludePaymentQr,
            Repeats = state.Repeats, IntervalMinutes = state.IntervalMinutes, Enabled = state.Enabled,
            NextRunAt = state.NextRunAt, LastRunAt = state.LastRunAt, FailureCount = state.FailureCount,
            LastError = state.LastError, CreatedAt = state.CreatedAt, UpdatedAt = state.UpdatedAt
        }));
        db.PollImports.AddRange(snapshot.PollImports.Select(state => new PollImport
        {
            Id = state.Id, SessionId = sessionId, ImportedByUserId = state.ImportedByUserId,
            ZaloGroupId = state.ZaloGroupId, PollId = state.PollId, PollQuestion = state.PollQuestion,
            SelectedOptionIdsJson = state.SelectedOptionIdsJson, ImportedPlayerCount = state.ImportedPlayerCount,
            ImportedAt = state.ImportedAt
        }));
        db.SessionWaitlistEntries.AddRange(snapshot.Waitlist.Select(state => new SessionWaitlistEntry
        {
            Id = state.Id, SessionId = sessionId, ZaloUserId = state.ZaloUserId, DisplayName = state.DisplayName,
            Status = state.Status, SessionPlayerId = state.SessionPlayerId, InvitedAt = state.InvitedAt,
            InviteExpiresAt = state.InviteExpiresAt, AcceptedAt = state.AcceptedAt,
            LastNotifiedAt = state.LastNotifiedAt, CreatedAt = state.CreatedAt, UpdatedAt = state.UpdatedAt,
            Version = state.Version
        }));
        await db.SaveChangesAsync(cancellationToken);

        var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId, cancellationToken);
        var match = snapshot.Match;
        session.Name = match.Name; session.StartTime = match.StartTime; session.Location = match.Location;
        session.ParkingInstructions = match.ParkingInstructions; session.LocationImageUrl = match.LocationImageUrl;
        session.PaymentInstructions = match.PaymentInstructions; session.PaymentQrImageUrl = match.PaymentQrImageUrl;
        session.BotEnabled = match.BotEnabled; session.BotCustomInstructions = match.BotCustomInstructions;
        session.BotOperatorZaloUserIdsJson = match.BotOperatorZaloUserIdsJson;
        session.ReminderEnabled = match.ReminderEnabled; session.ReminderLeadHours = match.ReminderLeadHours;
        session.ReminderIntervalHours = match.ReminderIntervalHours; session.ReminderIntervalMinutes = match.ReminderIntervalMinutes;
        session.ReminderRepeats = match.ReminderRepeats; session.LastReminderAt = match.LastReminderAt;
        session.NextReminderAt = match.NextReminderAt; session.ReminderLastKnownPlayerCount = match.ReminderLastKnownPlayerCount;
        session.ReminderFailureCount = match.ReminderFailureCount; session.LastReminderError = match.LastReminderError;
        session.Status = match.Status; session.TeamCount = match.TeamCount; session.TeamSize = match.TeamSize;
        session.TotalSets = match.TotalSets; session.CurrentRoundNumber = match.CurrentRoundNumber;
        session.CurrentTurnTeamId = match.CurrentTurnTeamId;
        session.CurrentTurnCaptainSessionPlayerId = match.CurrentTurnCaptainSessionPlayerId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var teamState in snapshot.Teams)
        {
            var team = await db.Teams.SingleAsync(item => item.Id == teamState.Id && item.SessionId == sessionId, cancellationToken);
            team.Name = teamState.Name;
            team.CaptainSessionPlayerId = teamState.CaptainSessionPlayerId;
            team.TotalAverageScore = teamState.TotalAverageScore;
            team.CreatedAt = teamState.CreatedAt;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? Clean(string? value, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
    private static ZaloBotActionHistoryResponse ToResponse(ZaloBotActionHistory action) => new(
        action.Id, action.SessionId, action.ActorZaloUserId, action.ActorName, action.ActionType,
        action.Summary, action.IsUndoable, action.CreatedAt, action.UndoneAt,
        action.UndoneByZaloUserId, action.UndoFailure);

    private static DraftSnapshotResponse ToSnapshotResponse(ZaloBotActionHistory action) => new(
        action.Id,
        action.SessionId,
        action.Summary,
        action.ActorName,
        action.CreatedAt,
        action.AfterHash);

    private sealed record SessionState(
        MatchState Match, IReadOnlyList<PlayerState> Players, IReadOnlyList<ProfileState> Profiles,
        IReadOnlyList<TeamState> Teams, IReadOnlyList<SlotState> Slots, IReadOnlyList<SlotPlayerState> SlotPlayers,
        IReadOnlyList<PreferenceGroupState> PreferenceGroups, IReadOnlyList<PreferencePlayerState> PreferencePlayers,
        IReadOnlyList<RoundState> Rounds, IReadOnlyList<BagState> Bags, IReadOnlyList<TurnState> Turns,
        IReadOnlyList<ReminderState> Reminders, IReadOnlyList<WaitlistState> Waitlist,
        IReadOnlyList<PollImportState> PollImports);
    private sealed record MatchState(
        string Name, DateTimeOffset? StartTime, string? Location, string? ParkingInstructions,
        string? LocationImageUrl, string? PaymentInstructions, string? PaymentQrImageUrl,
        bool BotEnabled, string? BotCustomInstructions, string BotOperatorZaloUserIdsJson,
        bool ReminderEnabled, int ReminderLeadHours, int ReminderIntervalHours, int ReminderIntervalMinutes,
        bool ReminderRepeats, DateTimeOffset? LastReminderAt, DateTimeOffset? NextReminderAt,
        int? ReminderLastKnownPlayerCount, int ReminderFailureCount, string? LastReminderError,
        SessionStatus Status, int TeamCount, int TeamSize, int TotalSets, int? CurrentRoundNumber,
        string? CurrentTurnTeamId, string? CurrentTurnCaptainSessionPlayerId, DateTimeOffset UpdatedAt);
    private sealed record PlayerState(string Id, string? UserId, string? PlayerProfileId, string DisplayName,
        string? AvatarUrl, PlayerRole Role, PlayerLevel Level, PlayerGender Gender, double Score,
        bool IsPresent, bool IsCaptainEligible, bool IsInsideSharedSlot, string? SourcePollId,
        string? SourceOptionIdsJson, DateTimeOffset CreatedAt);
    private sealed record ProfileState(string Id, string ZaloUserId, string DisplayName, string? AvatarUrl,
        PlayerGender? Gender, PlayerRole? DefaultRole, PlayerLevel? DefaultLevel, DateTimeOffset LastSyncedAt,
        DateTimeOffset? GenderUpdatedAt, string? GenderUpdatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record TeamState(string Id, string Name, string? CaptainSessionPlayerId, double TotalAverageScore, DateTimeOffset CreatedAt);
    private sealed record SlotState(string Id, DraftSlotType Type, string DisplayName, PlayerRole Role,
        PlayerGender Gender, double AverageScore, string? AssignedTeamId, bool IsCaptainSlot, DateTimeOffset CreatedAt);
    private sealed record SlotPlayerState(string Id, string DraftSlotId, string SessionPlayerId, int RotationOrder);
    private sealed record PreferenceGroupState(string Id, DateTimeOffset CreatedAt);
    private sealed record PreferencePlayerState(string TeamPreferenceGroupId, string SessionPlayerId, int RotationOrder);
    private sealed record RoundState(string Id, int RoundNumber, string Label, DraftRoundStatus Status, DateTimeOffset CreatedAt);
    private sealed record BagState(string Id, string RoundId, string DraftSlotId, string? PreparedDraftSlotId,
        int BagNumber, bool IsOpened, string? OpenedByUserId, string? OpenedForTeamId, DateTimeOffset? OpenedAt);
    private sealed record TurnState(string Id, string RoundId, string TeamId, string CaptainSessionPlayerId,
        int TurnOrder, DraftTurnStatus Status, string? OpenedBagId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
    private sealed record ReminderState(string Id, string CreatedBySenderId, string CreatedBySenderName,
        string? Message, ZaloReminderAudience Audience, bool OnlyIfMissingSlots, bool StopWhenFull,
        bool AllowAfterSessionStart, bool IncludePaymentQr, bool Repeats, int? IntervalMinutes,
        bool Enabled, DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt,
        int FailureCount, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record WaitlistState(string Id, string ZaloUserId, string DisplayName, SessionWaitlistStatus Status,
        string? SessionPlayerId, DateTimeOffset? InvitedAt, DateTimeOffset? InviteExpiresAt,
        DateTimeOffset? AcceptedAt, DateTimeOffset? LastNotifiedAt, DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt, int Version);
    private sealed record PollImportState(string Id, string ImportedByUserId, string ZaloGroupId,
        string PollId, string PollQuestion, string SelectedOptionIdsJson, int ImportedPlayerCount, DateTimeOffset ImportedAt);
}
