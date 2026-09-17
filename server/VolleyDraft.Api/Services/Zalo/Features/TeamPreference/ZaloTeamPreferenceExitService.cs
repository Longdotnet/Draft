using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloTeamPreferenceExitMember(
    string SessionPlayerId,
    string? ZaloUserId,
    string DisplayName);

internal sealed record ZaloTeamPreferenceExitCandidate(
    string SessionId,
    string SessionName,
    DateTimeOffset? StartTime,
    SessionStatus Status,
    string GroupId,
    string SenderPlayerId,
    string SenderDisplayName,
    IReadOnlyList<ZaloTeamPreferenceExitMember> Members,
    bool SenderHasProvenance);

internal enum ZaloTeamPreferenceExitAction
{
    RemoveSelf,
    RemoveTarget
}

internal sealed record ZaloTeamPreferenceExitPlan(
    string SessionId,
    string SessionName,
    string GroupId,
    string SenderPlayerId,
    string SenderDisplayName,
    ZaloTeamPreferenceExitAction Action,
    string RemovePlayerId,
    string RemoveDisplayName,
    IReadOnlyList<string> ExpectedMemberIds,
    IReadOnlyList<string> ExpectedMemberNames,
    IReadOnlyList<string> RemainingMemberNames,
    bool SenderHadProvenance,
    string SourceMessageId,
    bool AiInterpreted,
    double SemanticConfidence,
    string SemanticReason);

internal sealed record ZaloTeamPreferenceExitApplyResult(
    bool GroupDeleted,
    IReadOnlyList<string> RemainingMemberNames,
    string RemovedDisplayName);

/// <summary>
/// Deterministic authority for leaving/changing an existing TeamPreference group.
/// Semantic AI may nominate a target, but every identity, permission, lifecycle and
/// stale-preview decision is rebuilt from durable database state here.
/// </summary>
internal sealed class ZaloTeamPreferenceExitService(VolleyDraftDbContext db)
{
    public async Task<IReadOnlyList<ZaloTeamPreferenceExitCandidate>> LoadCandidatesAsync(
        string connectionId,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var senderId = NormalizeId(senderZaloUserId);
        if (senderId.Length == 0) return [];
        var now = DateTimeOffset.UtcNow;

        var groups = await db.TeamPreferenceGroups
            .AsNoTracking()
            .Include(group => group.Session)
            .Include(group => group.Players)
            .ThenInclude(link => link.SessionPlayer)
            .ThenInclude(player => player.PlayerProfile)
            .Where(group =>
                group.Session.ZaloConnectionId == connectionId &&
                group.Session.ZaloGroupId == groupId &&
                group.Session.BotEnabled &&
                (group.Session.Status == SessionStatus.Setup || group.Session.Status == SessionStatus.CaptainSelection))
            .ToListAsync(cancellationToken);

        var candidates = new List<ZaloTeamPreferenceExitCandidate>();
        foreach (var group in groups)
        {
            if (group.Session.StartTime is not null && group.Session.StartTime <= now) continue;
            var members = group.Players
                .OrderBy(link => link.RotationOrder)
                .Select(link => new ZaloTeamPreferenceExitMember(
                    link.SessionPlayerId,
                    NormalizeId(link.SessionPlayer.PlayerProfile?.ZaloUserId) is { Length: > 0 } uid ? uid : null,
                    link.SessionPlayer.DisplayName))
                .ToList();
            if (members.Count < 2) continue;

            var senderMatches = members.Where(member => member.ZaloUserId == senderId).ToList();
            if (senderMatches.Count != 1) continue;
            var sender = senderMatches[0];
            var provenance = await HasCurrentGroupProvenanceAsync(
                group.SessionId,
                group.Id,
                senderId,
                cancellationToken);

            candidates.Add(new(
                group.SessionId,
                group.Session.Name,
                group.Session.StartTime,
                group.Session.Status,
                group.Id,
                sender.SessionPlayerId,
                sender.DisplayName,
                members,
                provenance));
        }

        return candidates
            .OrderBy(candidate => candidate.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.SessionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ZaloTeamPreferenceExitPlan? BuildPlan(
        IReadOnlyList<ZaloTeamPreferenceExitCandidate> candidates,
        string senderZaloUserId,
        string sourceMessageId,
        string? targetZaloUserId,
        string? targetDisplayName,
        string? sessionReference,
        bool aiInterpreted,
        double semanticConfidence,
        string semanticReason,
        out string? clarification)
    {
        clarification = null;
        if (candidates.Count == 0) return null;

        var selected = SelectCandidate(candidates, sessionReference, out clarification);
        if (selected is null) return null;

        var senderId = NormalizeId(senderZaloUserId);
        var otherMembers = selected.Members
            .Where(member => member.ZaloUserId != senderId)
            .ToList();
        ZaloTeamPreferenceExitMember? target = null;
        var normalizedTargetUid = NormalizeId(targetZaloUserId);
        if (normalizedTargetUid.Length > 0)
        {
            var byUid = otherMembers.Where(member => member.ZaloUserId == normalizedTargetUid).ToList();
            if (byUid.Count != 1)
            {
                clarification = "Người bạn muốn tách không nằm trong nhóm chung team hiện tại, nên mình chưa đổi gì.";
                return null;
            }
            target = byUid[0];
        }
        else if (!string.IsNullOrWhiteSpace(targetDisplayName))
        {
            var normalizedName = NormalizeText(targetDisplayName);
            var byName = otherMembers
                .Where(member => NormalizeText(member.DisplayName) == normalizedName)
                .ToList();
            if (byName.Count != 1)
            {
                clarification = byName.Count == 0
                    ? "Tên bạn nói không nằm trong nhóm chung team hiện tại, nên mình chưa đổi gì."
                    : "Tên đó đang trùng nhiều người; hãy @mention đúng người để mình không tách nhầm.";
                return null;
            }
            target = byName[0];
        }

        // Every member owns consent for their own participation. Removing somebody
        // else is stronger: only the sender who durably created/expanded this exact
        // current group through the bot may propose it.
        var action = target is not null && selected.SenderHasProvenance
            ? ZaloTeamPreferenceExitAction.RemoveTarget
            : ZaloTeamPreferenceExitAction.RemoveSelf;
        var remove = action == ZaloTeamPreferenceExitAction.RemoveTarget
            ? target!
            : selected.Members.Single(member => member.SessionPlayerId == selected.SenderPlayerId);
        var remaining = selected.Members
            .Where(member => member.SessionPlayerId != remove.SessionPlayerId)
            .Select(member => member.DisplayName)
            .ToList();

        return new(
            selected.SessionId,
            selected.SessionName,
            selected.GroupId,
            selected.SenderPlayerId,
            selected.SenderDisplayName,
            action,
            remove.SessionPlayerId,
            remove.DisplayName,
            selected.Members.Select(member => member.SessionPlayerId).Order(StringComparer.Ordinal).ToList(),
            selected.Members.Select(member => member.DisplayName).ToList(),
            remaining,
            selected.SenderHasProvenance,
            sourceMessageId,
            aiInterpreted,
            semanticConfidence,
            semanticReason);
    }

    public async Task<ServiceResult<ZaloTeamPreferenceExitApplyResult>> ApplyAsync(
        string senderZaloUserId,
        ZaloTeamPreferenceExitPlan plan,
        CancellationToken cancellationToken = default)
    {
        var senderId = NormalizeId(senderZaloUserId);
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.Id == plan.SessionId)
            .Select(session => new
            {
                session.BotActionLeaseToken,
                session.BotActionLeaseUntil,
                session.Status,
                session.StartTime
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentLease is null)
            return Fail(StatusCodes.Status404NotFound, "Buổi chơi không còn tồn tại.");
        if (currentLease.Status is not (SessionStatus.Setup or SessionStatus.CaptainSelection) ||
            currentLease.StartTime is not null && currentLease.StartTime <= now)
            return Fail(StatusCodes.Status409Conflict, "Buổi chơi đã bắt đầu/draft hoặc trạng thái đã đổi; mình không tách nhóm chung team nữa.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return Fail(StatusCodes.Status409Conflict, "Buổi chơi đang được cập nhật ở nơi khác. Hãy thử lại sau một chút.");

        var leaseToken = $"team-preference-exit:{Guid.NewGuid():n}";
        var claimed = await db.MatchSessions
            .Where(session => session.Id == plan.SessionId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "TeamPreferenceExit")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)), cancellationToken);
        if (claimed == 0)
            return Fail(StatusCodes.Status409Conflict, "Nhóm chung team vừa được cập nhật ở nơi khác. Hãy gửi lại yêu cầu.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var group = await db.TeamPreferenceGroups
                .AsNoTracking()
                .Include(item => item.Session)
                .Include(item => item.Players)
                .ThenInclude(link => link.SessionPlayer)
                .ThenInclude(player => player.PlayerProfile)
                .SingleOrDefaultAsync(item => item.Id == plan.GroupId && item.SessionId == plan.SessionId, cancellationToken);
            if (group is null)
                return Fail(StatusCodes.Status409Conflict, "Nhóm chung team đã thay đổi sau lúc xem trước. Hãy gửi lại yêu cầu.");
            if (group.Session.Status is not (SessionStatus.Setup or SessionStatus.CaptainSelection) ||
                group.Session.StartTime is not null && group.Session.StartTime <= DateTimeOffset.UtcNow)
                return Fail(StatusCodes.Status409Conflict, "Buổi chơi đã bắt đầu/draft; mình không áp dụng preview cũ.");

            var currentIds = group.Players
                .Select(link => link.SessionPlayerId)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (!currentIds.SequenceEqual(plan.ExpectedMemberIds, StringComparer.Ordinal))
                return Fail(StatusCodes.Status409Conflict, "Nhóm chung team đã đổi người sau lúc xem trước. Mình không áp dụng preview cũ để tránh tách nhầm.");

            var senderLinks = group.Players.Where(link =>
                NormalizeId(link.SessionPlayer.PlayerProfile?.ZaloUserId) == senderId).ToList();
            if (senderLinks.Count != 1 || senderLinks[0].SessionPlayerId != plan.SenderPlayerId)
                return Fail(StatusCodes.Status403Forbidden, "UID của bạn không còn khớp thành viên đã xem trước; mình không đổi dữ liệu.");

            if (plan.Action == ZaloTeamPreferenceExitAction.RemoveTarget)
            {
                if (!plan.SenderHadProvenance ||
                    !await HasCurrentGroupProvenanceAsync(plan.SessionId, plan.GroupId, senderId, cancellationToken))
                    return Fail(StatusCodes.Status403Forbidden, "Mình không còn chứng minh được bạn là người tạo/mở rộng nhóm này, nên không tự bỏ thành viên khác.");
                if (plan.RemovePlayerId == plan.SenderPlayerId)
                    return Fail(StatusCodes.Status409Conflict, "Preview bỏ thành viên không còn hợp lệ.");
            }
            else if (plan.RemovePlayerId != plan.SenderPlayerId)
            {
                return Fail(StatusCodes.Status403Forbidden, "Thành viên thường chỉ được tự rút chính mình khỏi nhóm chung team.");
            }

            var removed = await db.TeamPreferenceGroupPlayers
                .Where(link => link.TeamPreferenceGroupId == plan.GroupId &&
                               link.SessionPlayerId == plan.RemovePlayerId)
                .ExecuteDeleteAsync(cancellationToken);
            if (removed != 1)
                return Fail(StatusCodes.Status409Conflict, "Nhóm chung team vừa thay đổi; mình chưa áp dụng để tránh sửa nhầm.");

            var remaining = await db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Where(link => link.TeamPreferenceGroupId == plan.GroupId)
                .Include(link => link.SessionPlayer)
                .OrderBy(link => link.RotationOrder)
                .Select(link => new { link.SessionPlayerId, link.SessionPlayer.DisplayName })
                .ToListAsync(cancellationToken);
            var groupDeleted = remaining.Count < 2;
            if (groupDeleted)
            {
                await db.TeamPreferenceGroupPlayers
                    .Where(link => link.TeamPreferenceGroupId == plan.GroupId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.TeamPreferenceGroups
                    .Where(item => item.Id == plan.GroupId && item.SessionId == plan.SessionId)
                    .ExecuteDeleteAsync(cancellationToken);
                remaining.Clear();
            }

            await db.MatchSessions
                .Where(session => session.Id == plan.SessionId)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<ZaloTeamPreferenceExitApplyResult>.Success(new(
                groupDeleted,
                remaining.Select(item => item.DisplayName).ToList(),
                plan.RemoveDisplayName));
        }
        catch (DbUpdateException)
        {
            return Fail(StatusCodes.Status409Conflict, "Nhóm chung team vừa thay đổi đồng thời. Mình chưa áp dụng để tránh ghi đè dữ liệu mới.");
        }
        finally
        {
            await db.MatchSessions
                .Where(session => session.Id == plan.SessionId && session.BotActionLeaseToken == leaseToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(session => session.BotActionLeaseToken, (string?)null)
                    .SetProperty(session => session.BotActionLeaseName, (string?)null)
                    .SetProperty(session => session.BotActionLeaseUntil, (DateTimeOffset?)null), cancellationToken);
        }
    }

    private ZaloTeamPreferenceExitCandidate? SelectCandidate(
        IReadOnlyList<ZaloTeamPreferenceExitCandidate> candidates,
        string? sessionReference,
        out string? clarification)
    {
        clarification = null;
        if (candidates.Count == 1 && string.IsNullOrWhiteSpace(sessionReference))
            return candidates[0];

        if (!string.IsNullOrWhiteSpace(sessionReference))
        {
            var refs = candidates
                .Select(candidate => new ZaloSessionReference(candidate.SessionId, candidate.SessionName, candidate.StartTime))
                .ToList();
            var resolution = ZaloSessionResolver.Resolve(sessionReference, refs, DateTimeOffset.UtcNow);
            var matches = candidates.Where(candidate => resolution.CandidateIds.Contains(candidate.SessionId)).ToList();
            if (matches.Count == 1) return matches[0];
        }

        clarification = candidates.Count == 1
            ? $"Mình chưa khớp được buổi bạn nói với {candidates[0].SessionName}. Hãy nói lại đúng tên/ngày buổi."
            : "Bạn đang có nhóm chung team ở nhiều buổi. Hãy nói rõ buổi nào: " +
              string.Join("; ", candidates.Select(candidate => candidate.SessionName)) + ".";
        return null;
    }

    private async Task<bool> HasCurrentGroupProvenanceAsync(
        string sessionId,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken)
    {
        var rows = await db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(action => action.SessionId == sessionId &&
                             action.ActionType == "TeamPreference" &&
                             action.ActorZaloUserId != null)
            .Select(action => new { action.ActorZaloUserId, action.AfterStateJson })
            .ToListAsync(cancellationToken);
        return rows.Any(action =>
            NormalizeId(action.ActorZaloUserId) == senderZaloUserId &&
            action.AfterStateJson.Contains(groupId, StringComparison.Ordinal));
    }

    private static string NormalizeId(string? value) => ZaloOverbookLogic.NormalizeId(value);
    private static string NormalizeText(string? value) => ZaloBotIntelligence.Normalize(value ?? string.Empty);

    private static ServiceResult<ZaloTeamPreferenceExitApplyResult> Fail(int statusCode, string message) =>
        ServiceResult<ZaloTeamPreferenceExitApplyResult>.Failure(statusCode, message);
}
