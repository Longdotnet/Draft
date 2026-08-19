using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloAutoSessionSettingsService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector credentialProtector,
    ZaloListenerCoordinator listenerCoordinator)
{
    private readonly ZaloAutoSessionSettingsStore store = new(db);
    private readonly ZaloAutoSessionStore bootstrapStore = new(db);
    private readonly ZaloAutoSessionObservabilityService observability = new(db);
    private readonly ZaloAutoSessionV2Store v2Store = new(db);
    private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizerStore = new(db);

    public async Task<ServiceResult<IReadOnlyList<ZaloAutoSessionGroupResponse>>> GetGroupsAsync(
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Users.AnyAsync(user => user.Id == adminUserId, cancellationToken))
            return Unauthorized<IReadOnlyList<ZaloAutoSessionGroupResponse>>();

        await bootstrapStore.SeedFromExistingSessionsAsync(cancellationToken);
        await v2Store.EnsureAsync(cancellationToken);
        var runtime = await v2Store.GetRuntimeAsync(cancellationToken);
        var trackedGroups = await store.GetForAdminAsync(adminUserId, cancellationToken);
        var connections = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.AdminUserId == adminUserId)
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);
        var sessionKeys = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.AdminUserId == adminUserId &&
                              session.ZaloConnectionId != null &&
                              session.ZaloGroupId != null)
            .Select(session => new { session.ZaloConnectionId, session.ZaloGroupId })
            .ToListAsync(cancellationToken);
        var sessionCounts = sessionKeys
            .GroupBy(item => $"{item.ZaloConnectionId}\n{NormalizeId(item.ZaloGroupId)}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var response = trackedGroups
            .Select(tracked =>
            {
                connections.TryGetValue(tracked.ZaloConnectionId, out var connection);
                sessionCounts.TryGetValue(
                    $"{tracked.ZaloConnectionId}\n{NormalizeId(tracked.GroupId)}",
                    out var sessionCount);
                return ToResponse(tracked, connection, sessionCount);
            })
            .OrderByDescending(item => item.AutoSessionEnabled)
            .ThenBy(item => item.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var index = 0; index < response.Count; index += 1)
        {
            connections.TryGetValue(response[index].ZaloConnectionId, out var connection);
            response[index] = await WithOperationalDataAsync(
                adminUserId,
                response[index],
                connection,
                runtime.GlobalEnabled,
                cancellationToken);
        }
        return ServiceResult<IReadOnlyList<ZaloAutoSessionGroupResponse>>.Success(response);
    }

    public async Task<ServiceResult<ZaloAutoSessionGroupResponse>> CreateGroupAsync(
        string adminUserId,
        CreateZaloAutoSessionGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionId = request.ConnectionId?.Trim() ?? string.Empty;
        var requestedGroupId = NormalizeId(request.GroupId);
        if (connectionId.Length == 0 || requestedGroupId.Length == 0)
            return BadRequest<ZaloAutoSessionGroupResponse>("Cần chọn tài khoản Zalo và group muốn theo dõi.");

        var connection = await db.ZaloConnections
            .SingleOrDefaultAsync(item =>
                item.Id == connectionId &&
                item.AdminUserId == adminUserId,
                cancellationToken);
        if (connection is null)
            return NotFound<ZaloAutoSessionGroupResponse>("Không tìm thấy kết nối Zalo của admin này.");
        if (connection.Status != ZaloConnectionStatus.Connected)
            return BadRequest<ZaloAutoSessionGroupResponse>("Kết nối Zalo đang không ở trạng thái Connected.");

        try
        {
            using var document = JsonDocument.Parse(
                credentialProtector.Unprotect(connection.EncryptedCredentials));
            var groups = await bridge.GetGroupsAsync(document.RootElement.Clone());
            var group = groups.FirstOrDefault(item =>
                string.Equals(NormalizeId(item.Id), requestedGroupId, StringComparison.Ordinal));
            if (group is null)
                return NotFound<ZaloAutoSessionGroupResponse>("Group không còn tồn tại hoặc tài khoản Zalo này không truy cập được group.");

            var existing = await store.GetByConnectionAndGroupAsync(
                adminUserId,
                connection.Id,
                requestedGroupId,
                cancellationToken);
            var tracked = existing ?? await store.InsertIfMissingAsync(new ZaloTrackedGroupData
            {
                AdminUserId = adminUserId,
                ZaloConnectionId = connection.Id,
                GroupId = requestedGroupId,
                GroupName = string.IsNullOrWhiteSpace(group.Name) ? requestedGroupId : group.Name.Trim(),
                AutoSessionEnabled = true,
                RequireOrganizerApproval = true,
                DefaultTeamCount = 3,
                DefaultTeamSize = 6,
                DefaultTotalSets = 4,
                DefaultStartMinutes = 17 * 60 + 30,
                AssumePmForHourUnder12 = true,
                BotEnabledForCreatedSessions = true
            }, cancellationToken);

            if (!tracked.AutoSessionEnabled)
            {
                tracked.AutoSessionEnabled = true;
                tracked.GroupName = string.IsNullOrWhiteSpace(group.Name) ? tracked.GroupName : group.Name.Trim();
                tracked = await store.UpdateAsync(tracked, cancellationToken) ?? tracked;
            }

            await v2Store.EnsureAsync(cancellationToken);
            await listenerCoordinator.EnsureConnectionAsync(connection.Id, cancellationToken);
            var sessionCount = await CountSessionsAsync(tracked, cancellationToken);
            var response = ToResponse(tracked, connection, sessionCount);
            var runtime = await v2Store.GetRuntimeAsync(cancellationToken);
            return ServiceResult<ZaloAutoSessionGroupResponse>.Created(
                await WithOperationalDataAsync(
                    adminUserId,
                    response,
                    connection,
                    runtime.GlobalEnabled,
                    cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BridgeFailure<ZaloAutoSessionGroupResponse>(exception);
        }
    }

    public async Task<ServiceResult<ZaloAutoSessionGroupResponse>> UpdateGroupAsync(
        string adminUserId,
        string trackedGroupId,
        UpdateZaloAutoSessionGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var tracked = await store.GetForAdminAsync(adminUserId, trackedGroupId, cancellationToken);
        if (tracked is null)
            return NotFound<ZaloAutoSessionGroupResponse>("Không tìm thấy group Auto Session của admin này.");

        if (request.DefaultTeamSize is < 2 or > 30)
            return BadRequest<ZaloAutoSessionGroupResponse>("Số người mỗi team phải từ 2 đến 30.");
        if (request.DefaultTotalSets is < 1 or > 20)
            return BadRequest<ZaloAutoSessionGroupResponse>("Số set mặc định phải từ 1 đến 20.");
        if (!TryParseStartTime(request.DefaultStartTime, out var startMinutes))
            return BadRequest<ZaloAutoSessionGroupResponse>("Giờ mặc định phải theo dạng HH:mm, ví dụ 17:30.");

        var location = string.IsNullOrWhiteSpace(request.DefaultLocation)
            ? null
            : request.DefaultLocation.Trim();
        if (location is { Length: > 500 })
            return BadRequest<ZaloAutoSessionGroupResponse>("Tên sân/địa điểm tối đa 500 ký tự.");

        ZaloAutoSessionRolloutMode? rollout = null;
        if (!string.IsNullOrWhiteSpace(request.RolloutMode))
        {
            if (!Enum.TryParse<ZaloAutoSessionRolloutMode>(request.RolloutMode.Trim(), true, out var parsed))
                return BadRequest<ZaloAutoSessionGroupResponse>("RolloutMode chỉ nhận Disabled, PreviewOnly hoặc Live.");
            rollout = parsed;
        }

        ZaloAutoSessionLearningStatus? learningDecision = null;
        if (!string.IsNullOrWhiteSpace(request.LearningSignalId) || !string.IsNullOrWhiteSpace(request.LearningDecision))
        {
            if (string.IsNullOrWhiteSpace(request.LearningSignalId) ||
                !Enum.TryParse<ZaloAutoSessionLearningStatus>(request.LearningDecision?.Trim(), true, out var parsed) ||
                parsed == ZaloAutoSessionLearningStatus.Pending)
                return BadRequest<ZaloAutoSessionGroupResponse>("Learning decision cần signal id và Approved hoặc Rejected.");
            learningDecision = parsed;
        }

        var trustedOrganizerId = NormalizeId(request.TrustedOrganizerZaloUserId);
        var trustedOrganizerChange =
            request.TrustedOrganizerEnabled is not null ||
            trustedOrganizerId.Length > 0;
        if (trustedOrganizerChange &&
            (trustedOrganizerId.Length == 0 || request.TrustedOrganizerEnabled is null))
            return BadRequest<ZaloAutoSessionGroupResponse>("Trusted Backup cần Zalo user id và trạng thái bật/tắt.");

        tracked.AutoSessionEnabled = request.AutoSessionEnabled;
        tracked.RequireOrganizerApproval = request.RequireOrganizerApproval;
        tracked.DefaultTeamCount = 3;
        tracked.DefaultTeamSize = request.DefaultTeamSize;
        tracked.DefaultTotalSets = request.DefaultTotalSets;
        tracked.DefaultStartMinutes = startMinutes;
        tracked.AssumePmForHourUnder12 = request.AssumePmForHourUnder12;
        tracked.DefaultLocation = location;
        tracked.BotEnabledForCreatedSessions = request.BotEnabledForCreatedSessions;

        tracked = await store.UpdateAsync(tracked, cancellationToken);
        if (tracked is null)
            return NotFound<ZaloAutoSessionGroupResponse>("Group Auto Session vừa bị thay đổi hoặc không còn tồn tại.");

        await v2Store.EnsureAsync(cancellationToken);
        await trustedOrganizerStore.EnsureAsync(cancellationToken);
        if (!tracked.AutoSessionEnabled)
        {
            await ZaloAutoSessionRolloutGuard.SupersedePendingAsync(
                db,
                tracked.Id,
                "auto_session_group_disabled",
                cancellationToken);
        }
        if (request.GlobalEnabled is not null)
        {
            await v2Store.SetGlobalEnabledAsync(request.GlobalEnabled.Value, adminUserId, cancellationToken);
            if (!request.GlobalEnabled.Value)
            {
                await ZaloAutoSessionRolloutGuard.SupersedePendingAsync(
                    db,
                    null,
                    "global_kill_switch_disabled",
                    cancellationToken);
            }
        }
        if (rollout is not null)
        {
            await v2Store.SetRolloutModeAsync(tracked.Id, rollout.Value, adminUserId, cancellationToken);
            if (rollout.Value != ZaloAutoSessionRolloutMode.Live)
            {
                await ZaloAutoSessionRolloutGuard.SupersedePendingAsync(
                    db,
                    tracked.Id,
                    $"rollout_changed_to_{rollout.Value.ToString().ToLowerInvariant()}",
                    cancellationToken);
            }
        }
        if (learningDecision is not null)
        {
            var reviewed = await v2Store.ReviewLearningSignalAsync(
                tracked.Id,
                request.LearningSignalId!.Trim(),
                learningDecision.Value,
                adminUserId,
                request.LearningReviewNote,
                cancellationToken);
            if (reviewed is null)
                return NotFound<ZaloAutoSessionGroupResponse>("Không tìm thấy learning signal cần duyệt trong group này.");
        }

        if (trustedOrganizerChange)
        {
            var enabled = request.TrustedOrganizerEnabled!.Value;
            var displayName = string.IsNullOrWhiteSpace(request.TrustedOrganizerDisplayName)
                ? trustedOrganizerId
                : request.TrustedOrganizerDisplayName.Trim();

            if (enabled)
            {
                var trustedConnection = await db.ZaloConnections
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.Id == tracked.ZaloConnectionId &&
                        item.AdminUserId == adminUserId,
                        cancellationToken);
                if (trustedConnection is null || trustedConnection.Status != ZaloConnectionStatus.Connected)
                    return BadRequest<ZaloAutoSessionGroupResponse>("Cần Zalo connection đang Connected để bật Trusted Backup.");

                try
                {
                    using var trustedDocument = JsonDocument.Parse(
                        credentialProtector.Unprotect(trustedConnection.EncryptedCredentials));
                    var roles = await bridge.GetGroupRolesAsync(trustedDocument.RootElement.Clone(), tracked.GroupId);
                    var creatorId = NormalizeId(roles.CreatorId);
                    if (string.Equals(trustedOrganizerId, creatorId, StringComparison.Ordinal))
                        return BadRequest<ZaloAutoSessionGroupResponse>("Trưởng nhóm đã là fallback mặc định, không cần thêm Trusted Backup.");

                    var currentOrganizerIds = new[] { creatorId }
                        .Concat(roles.AdminIds.Select(NormalizeId))
                        .Where(id => id.Length > 0)
                        .ToHashSet(StringComparer.Ordinal);
                    if (!currentOrganizerIds.Contains(trustedOrganizerId))
                        return BadRequest<ZaloAutoSessionGroupResponse>("Chỉ trưởng/phó nhóm Zalo hiện tại mới được bật Trusted Backup.");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    return BridgeFailure<ZaloAutoSessionGroupResponse>(exception);
                }
            }

            await trustedOrganizerStore.SetAsync(
                tracked.Id,
                trustedOrganizerId,
                displayName,
                enabled,
                adminUserId,
                cancellationToken);
        }

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tracked.ZaloConnectionId && item.AdminUserId == adminUserId, cancellationToken);
        if (connection is not null)
            await listenerCoordinator.EnsureConnectionAsync(connection.Id, cancellationToken);
        var sessionCount = await CountSessionsAsync(tracked, cancellationToken);
        var response = ToResponse(tracked, connection, sessionCount);
        var runtime = await v2Store.GetRuntimeAsync(cancellationToken);
        return ServiceResult<ZaloAutoSessionGroupResponse>.Success(
            await WithOperationalDataAsync(
                adminUserId,
                response,
                connection,
                runtime.GlobalEnabled,
                cancellationToken));
    }

    internal static bool TryParseStartTime(string? value, out int minutes)
    {
        minutes = 0;
        var normalized = value?.Trim() ?? string.Empty;
        if (!DateTime.TryParseExact(
                normalized,
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            return false;
        minutes = parsed.Hour * 60 + parsed.Minute;
        return true;
    }

    private async Task<ZaloAutoSessionGroupResponse> WithOperationalDataAsync(
        string adminUserId,
        ZaloAutoSessionGroupResponse response,
        ZaloConnection? connection,
        bool globalEnabled,
        CancellationToken cancellationToken)
    {
        var activity = await observability.GetActivityAsync(adminUserId, response.Id, 10, cancellationToken);
        var rollout = await v2Store.GetRolloutModeAsync(response.Id, cancellationToken);
        var health = await v2Store.GetHealthAsync(response.Id, cancellationToken);
        var learning = await v2Store.GetLearningSignalsAsync(response.Id, 100, cancellationToken);
        var visibleLearning = learning.Take(20).Select(ToLearningResponse).ToList();
        await trustedOrganizerStore.EnsureAsync(cancellationToken);
        var trustedOrganizers = await trustedOrganizerStore.GetAsync(response.Id, cancellationToken);
        var organizerCandidates = await GetOrganizerCandidatesAsync(
            response,
            connection,
            trustedOrganizers,
            cancellationToken);
        return response with
        {
            Activity = activity.IsSuccess ? activity.Value : response.Activity,
            GlobalEnabled = globalEnabled,
            RolloutMode = rollout.ToString(),
            Health = new ZaloAutoSessionHealthResponse(
                connection?.Status.ToString() ?? "Missing",
                health.LastPollEventAt,
                health.LastReconcileAt,
                health.LastSuccessAt,
                health.LastErrorAt,
                health.LastError,
                health.ConsecutiveFailures,
                health.NextRetryAt),
            LearningSignals = visibleLearning,
            PendingLearningCount = learning.Count(item => item.Status == ZaloAutoSessionLearningStatus.Pending),
            OrganizerCandidates = organizerCandidates
        };
    }

    private static ZaloAutoSessionLearningSignalResponse ToLearningResponse(ZaloAutoSessionLearningSignalData item) => new(
        item.Id,
        item.PollId,
        item.SignalType,
        item.DayKey,
        item.OriginalStartTime,
        item.ActualStartTime,
        item.SuggestedRuleType,
        item.SuggestedMinutes,
        item.Status.ToString(),
        item.ReviewNote,
        item.CreatedAt,
        item.UpdatedAt);

    private async Task<IReadOnlyList<ZaloAutoSessionOrganizerCandidateResponse>> GetOrganizerCandidatesAsync(
        ZaloAutoSessionGroupResponse response,
        ZaloConnection? connection,
        IReadOnlyList<ZaloAutoSessionTrustedOrganizerData> trusted,
        CancellationToken cancellationToken)
    {
        var trustedById = trusted
            .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var current = new Dictionary<string, (string Name, string Role, bool DefaultFallback)>(StringComparer.Ordinal);

        if (connection is not null && connection.Status == ZaloConnectionStatus.Connected)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    credentialProtector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var roles = await bridge.GetGroupRolesAsync(credentials, response.GroupId);
                var creatorId = NormalizeId(roles.CreatorId);
                var ids = new[] { creatorId }
                    .Concat(roles.AdminIds.Select(NormalizeId))
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var members = ids.Count == 0 ? [] : await bridge.GetMembersAsync(credentials, ids);
                var names = members
                    .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().DisplayName,
                        StringComparer.Ordinal);

                foreach (var id in ids)
                {
                    trustedById.TryGetValue(id, out var saved);
                    var name = names.GetValueOrDefault(id);
                    if (string.IsNullOrWhiteSpace(name)) name = saved?.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = id;
                    current[id] = (
                        name,
                        string.Equals(id, creatorId, StringComparison.Ordinal) ? "Creator" : "Admin",
                        string.Equals(id, creatorId, StringComparison.Ordinal));
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Keep Operations usable even when Zalo role lookup is temporarily unavailable.
            }
        }

        var result = current.Select(item =>
        {
            trustedById.TryGetValue(item.Key, out var saved);
            return new ZaloAutoSessionOrganizerCandidateResponse(
                item.Key,
                item.Value.Name,
                item.Value.Role,
                true,
                !item.Value.DefaultFallback && saved?.Enabled == true,
                item.Value.DefaultFallback);
        }).ToList();

        foreach (var saved in trusted.Where(item => item.Enabled))
        {
            var id = NormalizeId(saved.ZaloUserId);
            if (id.Length == 0 || current.ContainsKey(id)) continue;
            result.Add(new ZaloAutoSessionOrganizerCandidateResponse(
                id,
                string.IsNullOrWhiteSpace(saved.DisplayName) ? id : saved.DisplayName,
                "NoLongerAdmin",
                false,
                true,
                false));
        }

        return result
            .OrderByDescending(item => item.IsFallbackByDefault)
            .ThenByDescending(item => item.IsCurrentOrganizer)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<int> CountSessionsAsync(
        ZaloTrackedGroupData tracked,
        CancellationToken cancellationToken)
    {
        return await db.MatchSessions.CountAsync(session =>
            session.AdminUserId == tracked.AdminUserId &&
            session.ZaloConnectionId == tracked.ZaloConnectionId &&
            session.ZaloGroupId == tracked.GroupId,
            cancellationToken);
    }

    private static ZaloAutoSessionGroupResponse ToResponse(
        ZaloTrackedGroupData tracked,
        ZaloConnection? connection,
        int sessionCount) => new(
        tracked.Id,
        tracked.AdminUserId,
        tracked.ZaloConnectionId,
        connection?.DisplayName ?? "Zalo",
        connection?.AccountZaloId ?? string.Empty,
        tracked.GroupId,
        tracked.GroupName,
        tracked.AutoSessionEnabled,
        tracked.RequireOrganizerApproval,
        3,
        tracked.DefaultTeamSize,
        3 * tracked.DefaultTeamSize,
        tracked.DefaultTotalSets,
        $"{tracked.DefaultStartMinutes / 60:00}:{tracked.DefaultStartMinutes % 60:00}",
        tracked.AssumePmForHourUnder12,
        tracked.DefaultLocation,
        tracked.BotEnabledForCreatedSessions,
        sessionCount,
        tracked.CreatedAt,
        tracked.UpdatedAt);

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static ServiceResult<T> Unauthorized<T>() =>
        ServiceResult<T>.Failure(StatusCodes.Status401Unauthorized, "Phiên đăng nhập admin không hợp lệ.");
    private static ServiceResult<T> NotFound<T>(string message) =>
        ServiceResult<T>.Failure(StatusCodes.Status404NotFound, message);
    private static ServiceResult<T> BadRequest<T>(string message) =>
        ServiceResult<T>.Failure(StatusCodes.Status400BadRequest, message);
    private static ServiceResult<T> BridgeFailure<T>(Exception exception) =>
        ServiceResult<T>.Failure(StatusCodes.Status502BadGateway, $"Không thể đọc dữ liệu Zalo: {exception.Message}");
}
