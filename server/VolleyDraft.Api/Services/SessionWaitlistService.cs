using System.Data;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record WaitlistMutationResult(SessionWaitlistEntryResponse Entry, string Message);

public sealed class SessionWaitlistService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    AiAssistantService ai,
    ZaloBotActionHistoryService actionHistory,
    IConfiguration configuration,
    ILogger<SessionWaitlistService> logger)
{
    public async Task<ServiceResult<IReadOnlyList<SessionWaitlistEntryResponse>>> GetAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!await db.MatchSessions.AsNoTracking().AnyAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId, cancellationToken))
            return ServiceResult<IReadOnlyList<SessionWaitlistEntryResponse>>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        return ServiceResult<IReadOnlyList<SessionWaitlistEntryResponse>>.Success(await LoadResponsesAsync(sessionId, cancellationToken));
    }

    public async Task<ServiceResult<WaitlistMutationResult>> JoinAsync(
        string sessionId,
        string zaloUserId,
        string displayName,
        string actorZaloUserId,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        zaloUserId = NormalizeId(zaloUserId);
        displayName = Clean(displayName, 160) ?? $"Zalo {zaloUserId}";
        if (zaloUserId.Length == 0)
            return BadRequest("Không xác định được tài khoản Zalo cần thêm vào danh sách chờ.");
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return NotFound("Không tìm thấy buổi đấu.");
        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
            return BadRequest("Buổi này đã bắt đầu draft hoặc đã kết thúc nên không thể vào danh sách chờ.");
        var alreadyPresent = await db.SessionPlayers.AsNoTracking().AnyAsync(item =>
            item.SessionId == sessionId && item.IsPresent && item.PlayerProfile != null && item.PlayerProfile.ZaloUserId == zaloUserId,
            cancellationToken);
        if (alreadyPresent)
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict, $"{displayName} đã có tên trong danh sách chính thức của {session.Name}.");

        var before = await actionHistory.CaptureAsync(sessionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var entry = await db.SessionWaitlistEntries.SingleOrDefaultAsync(item =>
            item.SessionId == sessionId && item.ZaloUserId == zaloUserId, cancellationToken);
        if (entry is not null && entry.Status is SessionWaitlistStatus.Waiting or SessionWaitlistStatus.Invited)
        {
            var current = await ToResponseAsync(entry, cancellationToken);
            return ServiceResult<WaitlistMutationResult>.Success(new(current,
                entry.Status == SessionWaitlistStatus.Invited
                    ? $"{displayName} đang có lời mời nhận slot cho {session.Name}. Hãy trả lời @bot nhận slot hoặc @bot nhường người sau."
                    : $"{displayName} đã ở trong danh sách chờ của {session.Name}."));
        }
        if (entry is null)
        {
            entry = new SessionWaitlistEntry
            {
                SessionId = sessionId,
                ZaloUserId = zaloUserId,
                DisplayName = displayName,
                CreatedAt = now
            };
            db.SessionWaitlistEntries.Add(entry);
        }
        else
        {
            entry.DisplayName = displayName;
            entry.Status = SessionWaitlistStatus.Waiting;
            entry.SessionPlayerId = null;
            entry.InvitedAt = null;
            entry.InviteExpiresAt = null;
            entry.AcceptedAt = null;
            entry.CreatedAt = now;
        }
        entry.UpdatedAt = now;
        entry.Version += 1;
        await db.SaveChangesAsync(cancellationToken);
        await ProcessVacanciesAsync(sessionId, cancellationToken);
        await actionHistory.RecordAsync(sessionId, actorZaloUserId, actorName, "WaitlistJoin",
            $"Thêm {displayName} vào danh sách chờ của {session.Name}", before, cancellationToken);
        db.ChangeTracker.Clear();
        entry = await db.SessionWaitlistEntries.SingleAsync(item => item.Id == entry.Id, cancellationToken);
        var position = await GetPositionAsync(entry, cancellationToken);
        var message = entry.Status == SessionWaitlistStatus.Invited
            ? $"Đã xếp {displayName} vào danh sách chờ {session.Name}. Hiện đang có slot trống nên bot đã gửi lời mời nhận slot."
            : $"Đã xếp {displayName} vào danh sách chờ {session.Name}, vị trí {position}. Khi có người rút vote, bot sẽ gọi theo đúng thứ tự.";
        return ServiceResult<WaitlistMutationResult>.Created(new(await ToResponseAsync(entry, cancellationToken), message));
    }

    public async Task<ServiceResult<WaitlistMutationResult>> LeaveAsync(
        string sessionId,
        string zaloUserId,
        string actorZaloUserId,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        zaloUserId = NormalizeId(zaloUserId);
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return NotFound("Không tìm thấy buổi đấu.");
        var entry = await db.SessionWaitlistEntries.SingleOrDefaultAsync(item =>
            item.SessionId == sessionId && item.ZaloUserId == zaloUserId, cancellationToken);
        if (entry is null || entry.Status is SessionWaitlistStatus.Cancelled or SessionWaitlistStatus.Declined or SessionWaitlistStatus.Expired)
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status404NotFound, "Bạn hiện không ở trong danh sách chờ của buổi này.");
        if (entry.Status == SessionWaitlistStatus.Accepted)
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict,
                $"Bạn đã nhận slot và đang ở danh sách chính thức của {session.Name}. Lệnh rời waitlist sẽ không tự xoá suất đang chơi.");

        var before = await actionHistory.CaptureAsync(sessionId, cancellationToken);
        entry.Status = SessionWaitlistStatus.Cancelled;
        entry.InviteExpiresAt = null;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        entry.Version += 1;
        await db.SaveChangesAsync(cancellationToken);
        await ProcessVacanciesAsync(sessionId, cancellationToken);
        await actionHistory.RecordAsync(sessionId, actorZaloUserId, actorName, "WaitlistLeave",
            $"Rút {entry.DisplayName} khỏi danh sách chờ của {session.Name}", before, cancellationToken);
        return ServiceResult<WaitlistMutationResult>.Success(new(await ToResponseAsync(entry, cancellationToken),
            $"Đã rút bạn khỏi danh sách chờ {session.Name}. Nếu lời mời vừa được giữ cho bạn, bot sẽ chuyển sang người kế tiếp."));
    }

    public async Task<ServiceResult<WaitlistMutationResult>> AcceptAsync(
        string sessionId,
        string zaloUserId,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        zaloUserId = NormalizeId(zaloUserId);
        var before = await actionHistory.CaptureAsync(sessionId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var session = await db.MatchSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return NotFound("Không tìm thấy buổi đấu.");
        var entry = await db.SessionWaitlistEntries.SingleOrDefaultAsync(item =>
            item.SessionId == sessionId && item.ZaloUserId == zaloUserId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (entry is null || entry.Status != SessionWaitlistStatus.Invited)
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict, "Bạn chưa có lời mời nhận slot đang hoạt động cho buổi này.");
        if (entry.InviteExpiresAt is null || entry.InviteExpiresAt <= now)
        {
            entry.Status = SessionWaitlistStatus.Expired;
            entry.InviteExpiresAt = null;
            entry.UpdatedAt = now;
            entry.Version += 1;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ProcessVacanciesAsync(sessionId, cancellationToken);
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict,
                "Lời mời này đã hết hạn và slot đã được chuyển cho người kế tiếp. Bạn có thể xin vào waitlist lại.");
        }
        var effectiveCount = await GetEffectiveSlotCountAsync(sessionId, cancellationToken);
        if (effectiveCount >= session.TeamCount * session.TeamSize)
        {
            entry.Status = SessionWaitlistStatus.Waiting;
            entry.InviteExpiresAt = null;
            entry.UpdatedAt = now;
            entry.Version += 1;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict,
                "Slot vừa được lấp bởi thay đổi khác. Bot đã giữ nguyên vị trí chờ của bạn.");
        }

        var profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.ZaloUserId == zaloUserId, cancellationToken);
        if (profile is null)
        {
            profile = new PlayerProfile
            {
                ZaloUserId = zaloUserId,
                DisplayName = entry.DisplayName,
                Gender = null,
                DefaultRole = null,
                DefaultLevel = null,
                CreatedAt = now,
                UpdatedAt = now,
                LastSyncedAt = now
            };
            db.PlayerProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }
        var player = await db.SessionPlayers.SingleOrDefaultAsync(item =>
            item.SessionId == sessionId && item.PlayerProfileId == profile.Id, cancellationToken);
        if (player is null)
        {
            player = new SessionPlayer
            {
                SessionId = sessionId,
                PlayerProfileId = profile.Id,
                DisplayName = entry.DisplayName,
                AvatarUrl = profile.AvatarUrl,
                Gender = profile.Gender ?? PlayerGender.Unknown,
                Role = profile.DefaultRole ?? PlayerRole.New,
                Level = profile.DefaultLevel ?? PlayerLevel.New,
                Score = CalculateScore(profile.DefaultRole ?? PlayerRole.New, profile.DefaultLevel ?? PlayerLevel.New),
                IsPresent = true,
                IsCaptainEligible = true,
                CreatedAt = now
            };
            db.SessionPlayers.Add(player);
        }
        else
        {
            player.IsPresent = true;
            player.DisplayName = entry.DisplayName;
            player.SourcePollId = null;
            player.SourceOptionIdsJson = null;
        }
        entry.Status = SessionWaitlistStatus.Accepted;
        entry.SessionPlayerId = player.Id;
        entry.AcceptedAt = now;
        entry.InviteExpiresAt = null;
        entry.UpdatedAt = now;
        entry.Version += 1;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await actionHistory.RecordAsync(sessionId, zaloUserId, actorName, "WaitlistAccept",
            $"{entry.DisplayName} nhận slot trống của {session.Name}", before, cancellationToken);
        db.ChangeTracker.Clear();
        entry = await db.SessionWaitlistEntries.SingleAsync(item => item.Id == entry.Id, cancellationToken);
        return ServiceResult<WaitlistMutationResult>.Success(new(await ToResponseAsync(entry, cancellationToken),
            $"Đã chốt slot cho bạn trong {session.Name}. Bạn đã được thêm vào danh sách chính thức. Nếu hồ sơ còn thiếu giới tính, vị trí hoặc trình độ thì admin cần cập nhật trước khi draft."));
    }

    public async Task<ServiceResult<WaitlistMutationResult>> DeclineAsync(
        string sessionId,
        string zaloUserId,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        zaloUserId = NormalizeId(zaloUserId);
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return NotFound("Không tìm thấy buổi đấu.");
        var entry = await db.SessionWaitlistEntries.SingleOrDefaultAsync(item =>
            item.SessionId == sessionId && item.ZaloUserId == zaloUserId && item.Status == SessionWaitlistStatus.Invited,
            cancellationToken);
        if (entry is null)
            return ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status409Conflict, "Bạn không có lời mời nhận slot đang hoạt động cho buổi này.");
        var before = await actionHistory.CaptureAsync(sessionId, cancellationToken);
        entry.Status = SessionWaitlistStatus.Declined;
        entry.InviteExpiresAt = null;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        entry.Version += 1;
        await db.SaveChangesAsync(cancellationToken);
        await ProcessVacanciesAsync(sessionId, cancellationToken);
        await actionHistory.RecordAsync(sessionId, zaloUserId, actorName, "WaitlistDecline",
            $"{entry.DisplayName} nhường lời mời slot của {session.Name} cho người kế tiếp", before, cancellationToken);
        return ServiceResult<WaitlistMutationResult>.Success(new(await ToResponseAsync(entry, cancellationToken),
            $"Đã ghi nhận bạn nhường slot {session.Name}. Bot đang gọi người kế tiếp trong danh sách chờ."));
    }

    public async Task ProcessAllAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionIds = await db.MatchSessions.AsNoTracking()
            .Where(session => session.BotEnabled && session.StartTime != null && session.StartTime > now &&
                              session.Status != SessionStatus.Cancelled &&
                              session.WaitlistEntries.Any(entry => entry.Status == SessionWaitlistStatus.Waiting || entry.Status == SessionWaitlistStatus.Invited))
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);
        foreach (var sessionId in sessionIds)
        {
            try { await ProcessVacanciesAsync(sessionId, cancellationToken); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Could not process waitlist for session {SessionId}", sessionId);
            }
        }
    }

    public async Task ProcessVacanciesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await db.MatchSessions.Include(item => item.ZaloConnection)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session?.ZaloConnection is null || !session.BotEnabled || string.IsNullOrWhiteSpace(session.ZaloGroupId) ||
            session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
            return;

        var expired = await db.SessionWaitlistEntries
            .Where(item => item.SessionId == sessionId && item.Status == SessionWaitlistStatus.Invited &&
                           item.InviteExpiresAt != null && item.InviteExpiresAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var entry in expired)
        {
            entry.Status = SessionWaitlistStatus.Expired;
            entry.InviteExpiresAt = null;
            entry.UpdatedAt = now;
            entry.Version += 1;
        }
        if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken);

        var capacity = session.TeamCount * session.TeamSize;
        var effectiveCount = await GetEffectiveSlotCountAsync(sessionId, cancellationToken);
        var activeInvites = await db.SessionWaitlistEntries.CountAsync(item =>
            item.SessionId == sessionId && item.Status == SessionWaitlistStatus.Invited &&
            item.InviteExpiresAt != null && item.InviteExpiresAt > now, cancellationToken);
        var vacancyCount = Math.Max(0, capacity - effectiveCount - activeInvites);
        if (vacancyCount == 0) return;

        var waiting = await db.SessionWaitlistEntries
            .Where(item => item.SessionId == sessionId && item.Status == SessionWaitlistStatus.Waiting)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Take(vacancyCount)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0) return;
        var inviteMinutes = Math.Clamp(configuration.GetValue("ZaloBot:WaitlistInviteMinutes", 15), 5, 120);
        foreach (var entry in waiting)
        {
            entry.Status = SessionWaitlistStatus.Invited;
            entry.InvitedAt = now;
            entry.InviteExpiresAt = now.AddMinutes(inviteMinutes);
            entry.LastNotifiedAt = now;
            entry.UpdatedAt = now;
            entry.Version += 1;
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var entry in waiting)
        {
            var label = $"@{entry.DisplayName.TrimStart('@')}";
            var factual = $"{session.Name} vừa trống 1 slot và tới lượt bạn trong danh sách chờ. " +
                          $"Slot được giữ {inviteMinutes} phút; gõ @bot nhận slot để tham gia, hoặc @bot nhường người sau nếu bận.";
            var styled = ai.IsConfigured
                ? await ai.RewriteFactualAnswerAsync(new ZaloAiRewriteContext(
                    "Thông báo tự động khi có slot trống cho người trong danh sách chờ",
                    entry.DisplayName,
                    ZaloBotIntent.WaitlistStatus,
                    factual), cancellationToken)
                : null;
            var body = string.IsNullOrWhiteSpace(styled) ? factual : styled.Trim();
            try
            {
                await bridge.SendGroupMessageAsync(
                    session.ZaloConnection.AccountZaloId,
                    session.ZaloGroupId!,
                    $"{label} {body}",
                    [new BridgeOutgoingMention(entry.ZaloUserId, 0, label.Length)],
                    idempotencyKey: $"waitlist:{entry.Id}:{entry.Version}");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not notify waitlist entry {EntryId}", entry.Id);
                await db.SessionWaitlistEntries
                    .Where(item => item.Id == entry.Id && item.Status == SessionWaitlistStatus.Invited && item.Version == entry.Version)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, SessionWaitlistStatus.Waiting)
                        .SetProperty(item => item.InviteExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<SessionWaitlistEntryResponse>> LoadResponsesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var rows = await db.SessionWaitlistEntries.AsNoTracking()
            .Where(item => item.SessionId == sessionId &&
                           item.Status != SessionWaitlistStatus.Cancelled &&
                           item.Status != SessionWaitlistStatus.Declined &&
                           item.Status != SessionWaitlistStatus.Expired)
            .OrderBy(item => item.Status == SessionWaitlistStatus.Accepted ? 1 : 0)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var waitingPosition = 0;
        return rows.Select(item =>
        {
            var position = item.Status is SessionWaitlistStatus.Waiting or SessionWaitlistStatus.Invited
                ? ++waitingPosition
                : 0;
            return ToResponse(item, position);
        }).ToList();
    }

    private async Task<int> GetEffectiveSlotCountAsync(string sessionId, CancellationToken cancellationToken)
    {
        var regular = await db.SessionPlayers.CountAsync(item =>
            item.SessionId == sessionId && item.IsPresent && !item.IsInsideSharedSlot, cancellationToken);
        var shared = await db.DraftSlots.CountAsync(item =>
            item.SessionId == sessionId && item.Type == DraftSlotType.Shared &&
            item.Players.Any(link => link.SessionPlayer.IsPresent), cancellationToken);
        return regular + shared;
    }

    private async Task<int> GetPositionAsync(SessionWaitlistEntry entry, CancellationToken cancellationToken) =>
        entry.Status is not (SessionWaitlistStatus.Waiting or SessionWaitlistStatus.Invited)
            ? 0
            : 1 + await db.SessionWaitlistEntries.AsNoTracking().CountAsync(item =>
                item.SessionId == entry.SessionId &&
                (item.Status == SessionWaitlistStatus.Waiting || item.Status == SessionWaitlistStatus.Invited) &&
                (item.CreatedAt < entry.CreatedAt || item.CreatedAt == entry.CreatedAt && string.Compare(item.Id, entry.Id) < 0),
                cancellationToken);

    private async Task<SessionWaitlistEntryResponse> ToResponseAsync(SessionWaitlistEntry entry, CancellationToken cancellationToken) =>
        ToResponse(entry, await GetPositionAsync(entry, cancellationToken));

    private static SessionWaitlistEntryResponse ToResponse(SessionWaitlistEntry entry, int position) => new(
        entry.Id, entry.SessionId, entry.ZaloUserId, entry.DisplayName, entry.Status, position,
        entry.InviteExpiresAt, entry.CreatedAt, entry.UpdatedAt);
    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
    private static string? Clean(string? value, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
    private static double CalculateScore(PlayerRole role, PlayerLevel level)
    {
        var levelScore = level switch { PlayerLevel.Good => 3, PlayerLevel.Average => 2, _ => 1 };
        var roleScore = role switch { PlayerRole.FullStack => .5, PlayerRole.Setter => .25, _ => 0 };
        return levelScore + roleScore;
    }
    private static ServiceResult<WaitlistMutationResult> BadRequest(string message) =>
        ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status400BadRequest, message);
    private static ServiceResult<WaitlistMutationResult> NotFound(string message) =>
        ServiceResult<WaitlistMutationResult>.Failure(StatusCodes.Status404NotFound, message);
}
