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

    public async Task<ServiceResult<IReadOnlyList<ZaloAutoSessionGroupResponse>>> GetGroupsAsync(
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Users.AnyAsync(user => user.Id == adminUserId, cancellationToken))
            return Unauthorized<IReadOnlyList<ZaloAutoSessionGroupResponse>>();

        await bootstrapStore.SeedFromExistingSessionsAsync(cancellationToken);
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

            await listenerCoordinator.EnsureConnectionAsync(connection.Id, cancellationToken);
            var sessionCount = await CountSessionsAsync(tracked, cancellationToken);
            return ServiceResult<ZaloAutoSessionGroupResponse>.Created(
                ToResponse(tracked, connection, sessionCount));
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

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tracked.ZaloConnectionId && item.AdminUserId == adminUserId, cancellationToken);
        if (connection is not null)
            await listenerCoordinator.EnsureConnectionAsync(connection.Id, cancellationToken);
        var sessionCount = await CountSessionsAsync(tracked, cancellationToken);
        return ServiceResult<ZaloAutoSessionGroupResponse>.Success(
            ToResponse(tracked, connection, sessionCount));
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
