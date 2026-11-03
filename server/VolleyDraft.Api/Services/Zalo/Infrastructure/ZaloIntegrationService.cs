using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloIntegrationService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector credentialProtector,
    ZaloQrLoginRegistry loginRegistry,
    ZaloListenerCoordinator listenerCoordinator,
    ZaloActivityBackfillCoordinator activityBackfillCoordinator,
    IConfiguration? configuration = null)
{
    public async Task<ServiceResult<StartZaloQrLoginResponse>> StartQrLoginAsync(string adminUserId)
    {
        if (!await db.Users.AnyAsync(user => user.Id == adminUserId))
        {
            return Unauthorized<StartZaloQrLoginResponse>();
        }

        try
        {
            var result = await bridge.StartQrLoginAsync();
            loginRegistry.Register(result.Id, adminUserId, result.ExpiresAt);
            return ServiceResult<StartZaloQrLoginResponse>.Success(
                new StartZaloQrLoginResponse(result.Id, result.Status, result.ExpiresAt));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<StartZaloQrLoginResponse>(exception);
        }
    }

    public async Task<ServiceResult<ZaloQrLoginStatusResponse>> GetQrLoginStatusAsync(
        string adminUserId,
        string loginId)
    {
        if (!loginRegistry.IsOwnedBy(loginId, adminUserId))
        {
            return NotFound<ZaloQrLoginStatusResponse>("Không tìm thấy phiên đăng nhập QR hoặc phiên đã hết hạn.");
        }

        try
        {
            var result = await bridge.GetQrLoginAsync(loginId);
            ZaloConnectionResponse? connectionResponse = null;

            if (string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (result.Credentials is null || string.IsNullOrWhiteSpace(result.AccountZaloId))
                {
                    return BadRequest<ZaloQrLoginStatusResponse>("Zalo Bridge hoàn tất nhưng không trả credential hợp lệ.");
                }

                var encryptedCredentials = credentialProtector.Protect(result.Credentials.Value.GetRawText());
                var connection = await db.ZaloConnections.SingleOrDefaultAsync(item =>
                    item.AdminUserId == adminUserId &&
                    item.AccountZaloId == result.AccountZaloId);

                if (connection is null)
                {
                    connection = new ZaloConnection
                    {
                        AdminUserId = adminUserId,
                        AccountZaloId = result.AccountZaloId,
                        DisplayName = result.DisplayName ?? "Zalo",
                        AvatarUrl = result.AvatarUrl,
                        EncryptedCredentials = encryptedCredentials
                    };
                    db.ZaloConnections.Add(connection);
                }
                else
                {
                    connection.DisplayName = result.DisplayName ?? connection.DisplayName;
                    connection.AvatarUrl = result.AvatarUrl ?? connection.AvatarUrl;
                    connection.EncryptedCredentials = encryptedCredentials;
                    connection.Status = ZaloConnectionStatus.Connected;
                    connection.LastValidatedAt = DateTimeOffset.UtcNow;
                    connection.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await db.SaveChangesAsync();
                loginRegistry.Complete(loginId);
                connectionResponse = ToConnectionResponse(connection);
            }

            return ServiceResult<ZaloQrLoginStatusResponse>.Success(
                new ZaloQrLoginStatusResponse(
                    result.Id,
                    result.Status,
                    result.QrImageBase64,
                    result.DisplayName,
                    result.AvatarUrl,
                    result.Error,
                    connectionResponse));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<ZaloQrLoginStatusResponse>(exception);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<ZaloConnectionResponse>>> GetConnectionsAsync(string adminUserId)
    {
        var connections = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.AdminUserId == adminUserId)
            .OrderByDescending(connection => connection.UpdatedAt)
            .ToListAsync();
        return ServiceResult<IReadOnlyList<ZaloConnectionResponse>>.Success(
            connections.Select(ToConnectionResponse).ToList());
    }

    public async Task<ServiceResult<IReadOnlyList<ZaloGroupResponse>>> GetGroupsAsync(
        string adminUserId,
        string connectionId)
    {
        var connection = await GetConnectionAsync(adminUserId, connectionId);
        if (connection is null)
        {
            return NotFound<IReadOnlyList<ZaloGroupResponse>>("Không tìm thấy kết nối Zalo.");
        }

        try
        {
            var groups = await bridge.GetGroupsAsync(ReadCredentials(connection));
            connection.LastValidatedAt = DateTimeOffset.UtcNow;
            connection.Status = ZaloConnectionStatus.Connected;
            await db.SaveChangesAsync();
            return ServiceResult<IReadOnlyList<ZaloGroupResponse>>.Success(
                groups.Select(group => new ZaloGroupResponse(
                    group.Id,
                    group.Name,
                    group.AvatarUrl,
                    group.TotalMembers)).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<IReadOnlyList<ZaloGroupResponse>>(exception);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<ZyroMappingGroupResponse>>> GetGroupsForZyroMappingAsync(
        CancellationToken cancellationToken = default)
    {
        var ownerResult = await ResolveZyroMappingAdminUserIdAsync(cancellationToken);
        if (!ownerResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingGroupResponse>>.Failure(
                ownerResult.StatusCode,
                ownerResult.Error ?? "Không xác định được tài khoản Draft được phép mapping.");
        }

        var adminUserId = ownerResult.Value!;
        var connections = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.AdminUserId == adminUserId &&
                                 connection.Status == ZaloConnectionStatus.Connected)
            .OrderByDescending(connection => connection.UpdatedAt)
            .ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingGroupResponse>>.Failure(
                StatusCodes.Status404NotFound,
                "Draft chưa có tài khoản Zalo nào đang kết nối.");
        }

        var result = new List<ZyroMappingGroupResponse>();
        foreach (var connection in connections)
        {
            try
            {
                var groups = await bridge.GetGroupsAsync(ReadCredentials(connection));
                result.AddRange(groups.Select(group => new ZyroMappingGroupResponse(
                    connection.Id,
                    connection.DisplayName,
                    connection.AccountZaloId,
                    group.Id,
                    group.Name,
                    group.AvatarUrl,
                    group.TotalMembers)));
            }
            catch (Exception exception) when (IsZyroMappingBridgeFailure(exception))
            {
                return ZyroMappingBridgeFailure<IReadOnlyList<ZyroMappingGroupResponse>>(
                    exception,
                    "Không thể tải danh sách nhóm Zalo từ Draft.");
            }
        }

        return ServiceResult<IReadOnlyList<ZyroMappingGroupResponse>>.Success(
            result.OrderBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<ServiceResult<ZyroMappingMemberDirectoryResponse>> GetMemberDirectoryForZyroMappingAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        var normalizedGroupId = NormalizeId(groupId ?? string.Empty);
        if (normalizedConnectionId.Length == 0 || normalizedGroupId.Length == 0)
        {
            return ServiceResult<ZyroMappingMemberDirectoryResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Thiếu tài khoản hoặc mã nhóm Zalo.");
        }

        var ownerResult = await ResolveZyroMappingAdminUserIdAsync(cancellationToken);
        if (!ownerResult.IsSuccess)
        {
            return ServiceResult<ZyroMappingMemberDirectoryResponse>.Failure(
                ownerResult.StatusCode,
                ownerResult.Error ?? "Không xác định được tài khoản Draft được phép mapping.");
        }

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == normalizedConnectionId &&
                item.AdminUserId == ownerResult.Value &&
                item.Status == ZaloConnectionStatus.Connected,
                cancellationToken);
        if (connection is null)
        {
            return ServiceResult<ZyroMappingMemberDirectoryResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy tài khoản Zalo đang kết nối trong Draft.");
        }

        var directoryResult = await GetZyroMappingGroupDirectoryAsync(
            connection,
            normalizedGroupId,
            cancellationToken);
        if (!directoryResult.IsSuccess)
        {
            return ServiceResult<ZyroMappingMemberDirectoryResponse>.Failure(
                directoryResult.StatusCode,
                directoryResult.Error ?? "Không thể đọc danh sách thành viên nhóm Zalo.");
        }

        var directory = directoryResult.Value!;
        return ServiceResult<ZyroMappingMemberDirectoryResponse>.Success(
            new ZyroMappingMemberDirectoryResponse(
                connection.Id,
                connection.DisplayName,
                directory.GroupId,
                directory.GroupName,
                directory.GroupCreatedAtUnixMs,
                directory.ExpectedMemberCount,
                directory.IsComplete,
                directory.Members.Select(member => new ZyroMappingMemberResponse(
                    NormalizeId(member.ZaloUserId ?? string.Empty),
                    !string.IsNullOrWhiteSpace(member.DisplayName)
                        ? member.DisplayName.Trim()
                        : member.ZaloName?.Trim() ?? string.Empty,
                    member.ZaloName,
                    member.AvatarUrl)).ToList()));
    }

    public async Task<ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>> SyncZyroMappedProfilesAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZyroMappedProfileSyncItem> requestedProfiles,
        CancellationToken cancellationToken = default)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        var normalizedGroupId = NormalizeId(groupId ?? string.Empty);
        if (normalizedConnectionId.Length == 0 || normalizedGroupId.Length == 0)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status400BadRequest,
                "Thiếu tài khoản hoặc mã nhóm Zalo.");
        }

        if (requestedProfiles is null || requestedProfiles.Count is < 1 or > 200 ||
            requestedProfiles.Any(item => item is null))
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status400BadRequest,
                "Mỗi lần đồng bộ cần có từ 1 đến 200 hồ sơ.");
        }

        var profiles = requestedProfiles.Select(item => new
        {
            ZaloUserId = NormalizeId(item?.ZaloUserId ?? string.Empty),
            Gender = item?.Gender,
            Role = item?.Role,
            Level = item?.Level
        }).ToList();

        if (profiles.Any(item =>
                item.ZaloUserId.Length == 0 ||
                (item.Gender is not null && !Enum.IsDefined(item.Gender.Value)) ||
                (item.Role is not null && !Enum.IsDefined(item.Role.Value)) ||
                (item.Level is not null && !Enum.IsDefined(item.Level.Value))) ||
            profiles.Select(item => item.ZaloUserId).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status400BadRequest,
                "Danh sách hồ sơ có UID trống/trùng hoặc thông tin giới tính, vị trí, trình độ không hợp lệ.");
        }

        var ownerResult = await ResolveZyroMappingAdminUserIdAsync(cancellationToken);
        if (!ownerResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                ownerResult.StatusCode,
                ownerResult.Error ?? "Không xác định được tài khoản Draft được phép mapping.");
        }
        var adminUserId = ownerResult.Value!;

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == normalizedConnectionId &&
                item.AdminUserId == ownerResult.Value &&
                item.Status == ZaloConnectionStatus.Connected,
                cancellationToken);
        if (connection is null)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy tài khoản Zalo đang kết nối trong Draft.");
        }

        var directoryResult = await GetZyroMappingGroupDirectoryAsync(
            connection,
            normalizedGroupId,
            cancellationToken);
        if (!directoryResult.IsSuccess)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                directoryResult.StatusCode,
                directoryResult.Error ?? "Không thể đọc danh sách thành viên nhóm Zalo.");
        }

        var directory = directoryResult.Value!;
        if (!directory.IsComplete)
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status409Conflict,
                "Danh sách thành viên Zalo chưa đầy đủ. Hãy đồng bộ lại danh sách nhóm trước khi mapping.");
        }

        var memberGroups = directory.Members
            .Where(member => !string.IsNullOrWhiteSpace(member.ZaloUserId))
            .GroupBy(member => NormalizeId(member.ZaloUserId ?? string.Empty), StringComparer.Ordinal)
            .ToList();
        if (memberGroups.Any(group => group.Count() > 1))
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status502BadGateway,
                "Danh bạ Zalo trả về UID trùng. Draft chưa thể xác minh mapping an toàn.");
        }

        var memberById = memberGroups.ToDictionary(
            group => group.Key,
            group => group.Single(),
            StringComparer.Ordinal);
        if (profiles.Any(item => !memberById.ContainsKey(item.ZaloUserId)))
        {
            return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Failure(
                StatusCodes.Status400BadRequest,
                "Có UID không thuộc nhóm Zalo đã chọn. Draft không ghi hồ sơ cho thành viên ngoài nhóm.");
        }

        var ids = profiles.Select(item => item.ZaloUserId).ToList();
        var existingProfiles = await db.PlayerProfiles
            .Where(profile => ids.Contains(profile.ZaloUserId))
            .ToDictionaryAsync(profile => profile.ZaloUserId, StringComparer.Ordinal, cancellationToken);
        var results = new List<ZyroMappingProfileSyncResult>(profiles.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in profiles)
        {
            var member = memberById[item.ZaloUserId];
            var displayName = !string.IsNullOrWhiteSpace(member.DisplayName)
                ? member.DisplayName.Trim()
                : !string.IsNullOrWhiteSpace(member.ZaloName)
                    ? member.ZaloName.Trim()
                    : $"Zalo {item.ZaloUserId}";
            var gender = item.Gender is PlayerGender.Unknown ? null : item.Gender;
            var role = item.Role is PlayerRole.New ? null : item.Role;

            if (!existingProfiles.TryGetValue(item.ZaloUserId, out var profile))
            {
                profile = new PlayerProfile
                {
                    ZaloUserId = item.ZaloUserId,
                    DisplayName = displayName,
                    AvatarUrl = member.AvatarUrl,
                    Gender = gender,
                    DefaultRole = role,
                    DefaultLevel = item.Level,
                    GenderUpdatedAt = gender is null ? null : now,
                    GenderUpdatedByUserId = gender is null ? null : adminUserId,
                    LastSyncedAt = now,
                    UpdatedAt = now
                };
                db.PlayerProfiles.Add(profile);
                results.Add(new ZyroMappingProfileSyncResult(
                    item.ZaloUserId,
                    displayName,
                    "created",
                    []));
                continue;
            }

            var preserved = new List<string>();
            var changed = false;
            if (string.IsNullOrWhiteSpace(profile.DisplayName) ||
                profile.DisplayName.StartsWith("Zalo ", StringComparison.OrdinalIgnoreCase))
            {
                if (profile.DisplayName != displayName)
                {
                    profile.DisplayName = displayName;
                    changed = true;
                }
            }
            else if (!string.Equals(profile.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                preserved.Add("displayName");
            }

            if (profile.AvatarUrl is null && !string.IsNullOrWhiteSpace(member.AvatarUrl))
            {
                profile.AvatarUrl = member.AvatarUrl;
                changed = true;
            }
            else if (!string.IsNullOrWhiteSpace(member.AvatarUrl) &&
                     !string.Equals(profile.AvatarUrl, member.AvatarUrl, StringComparison.Ordinal))
            {
                preserved.Add("avatar");
            }

            if ((profile.Gender is null or PlayerGender.Unknown) && gender is not null)
            {
                profile.Gender = gender;
                profile.GenderUpdatedAt = now;
                profile.GenderUpdatedByUserId = adminUserId;
                changed = true;
            }
            else if (gender is not null && profile.Gender != gender)
            {
                preserved.Add("gender");
            }
            if ((profile.DefaultRole is null or PlayerRole.New) && role is not null)
            {
                profile.DefaultRole = role;
                changed = true;
            }
            else if (role is not null && profile.DefaultRole != role)
            {
                preserved.Add("role");
            }
            if (profile.DefaultLevel is null && item.Level is not null)
            {
                profile.DefaultLevel = item.Level;
                changed = true;
            }
            else if (item.Level is not null && profile.DefaultLevel != item.Level)
            {
                preserved.Add("level");
            }

            profile.LastSyncedAt = now;
            profile.UpdatedAt = now;
            results.Add(new ZyroMappingProfileSyncResult(
                item.ZaloUserId,
                profile.DisplayName,
                preserved.Count > 0 ? "existing_values_preserved" : changed ? "updated_missing_fields" : "already_present",
                preserved));
        }

        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ZyroMappingProfileSyncResult>>.Success(results);
    }

    public async Task<ServiceResult<SessionResponse>> LinkGroupAsync(
        string adminUserId,
        string sessionId,
        LinkZaloGroupRequest request)
    {
        var session = await db.MatchSessions
            .Include(item => item.Teams)
            .SingleOrDefaultAsync(item => item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
        {
            return NotFound<SessionResponse>("Không tìm thấy buổi đấu.");
        }
        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<SessionResponse>("Không thể đổi nhóm Zalo sau khi draft đã bắt đầu.");
        }

        var connection = await GetConnectionAsync(adminUserId, request.ConnectionId);
        if (connection is null)
        {
            return NotFound<SessionResponse>("Không tìm thấy kết nối Zalo.");
        }

        try
        {
            var groups = await bridge.GetGroupsAsync(ReadCredentials(connection));
            var group = groups.SingleOrDefault(item => item.Id == request.GroupId);
            if (group is null)
            {
                return BadRequest<SessionResponse>("Nhóm không tồn tại hoặc tài khoản Zalo không còn trong nhóm.");
            }

            session.ZaloConnectionId = connection.Id;
            session.ZaloGroupId = group.Id;
            session.ZaloGroupName = group.Name;
            session.ZaloGroupAvatarUrl = group.AvatarUrl;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await activityBackfillCoordinator.QueueGroupAsync(
                connection.Id,
                group.Id,
                full: true);
            await listenerCoordinator.EnsureConnectionAsync(connection.Id);
            return ServiceResult<SessionResponse>.Success(ToSessionResponse(session));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<SessionResponse>(exception);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<ZaloPollResponse>>> GetPollsAsync(
        string adminUserId,
        string sessionId)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
        {
            return ServiceResult<IReadOnlyList<ZaloPollResponse>>.Failure(linked.Result.StatusCode, linked.Result.Error!);
        }

        try
        {
            var polls = await bridge.GetPollsAsync(ReadCredentials(linked.Connection!), linked.Session!.ZaloGroupId!);
            return ServiceResult<IReadOnlyList<ZaloPollResponse>>.Success(polls.Select(ToPollResponse).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<IReadOnlyList<ZaloPollResponse>>(exception);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<BridgeMember>>> ResolveMembersAsync(
        string adminUserId,
        string sessionId,
        IReadOnlyList<string> memberIds)
    {
        var ids = memberIds
            .Select(NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
            return ServiceResult<IReadOnlyList<BridgeMember>>.Success([]);

        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
        {
            return ServiceResult<IReadOnlyList<BridgeMember>>.Failure(
                linked.Result.StatusCode,
                linked.Result.Error!);
        }

        try
        {
            var members = await bridge.GetMembersAsync(ReadCredentials(linked.Connection!), ids);
            return ServiceResult<IReadOnlyList<BridgeMember>>.Success(members);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<IReadOnlyList<BridgeMember>>(exception);
        }
    }

    public async Task<ServiceResult<int>> HydrateMissingMemberAvatarsAsync(
        string adminUserId,
        string sessionId)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
            return ServiceResult<int>.Failure(linked.Result.StatusCode, linked.Result.Error!);

        var players = await db.SessionPlayers
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId && player.IsPresent && player.PlayerProfile != null)
            .ToListAsync();

        // Poster rendering used to resolve only players with a missing AvatarUrl. That meant a
        // low-resolution group-directory thumbnail could live forever even though Zalo's profile
        // endpoint exposes a better `avatar` URL. Refresh every present Zalo profile here so an
        // existing @bot 10 session can upgrade itself without re-importing the poll.
        var profileIds = players
            .Select(player => NormalizeId(player.PlayerProfile?.ZaloUserId ?? string.Empty))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var changed = 0;
        if (profileIds.Count == 0)
        {
            foreach (var player in players)
            {
                if (string.IsNullOrWhiteSpace(player.AvatarUrl) &&
                    !string.IsNullOrWhiteSpace(player.PlayerProfile?.AvatarUrl))
                {
                    player.AvatarUrl = player.PlayerProfile.AvatarUrl;
                    changed += 1;
                }
            }
            if (changed > 0) await db.SaveChangesAsync();
            return ServiceResult<int>.Success(changed);
        }

        try
        {
            var members = await bridge.GetMembersAsync(ReadCredentials(linked.Connection!), profileIds);
            var memberById = members
                .Where(member => !string.IsNullOrWhiteSpace(member.AvatarUrl))
                .GroupBy(member => NormalizeId(member.ZaloUserId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;

            foreach (var player in players)
            {
                var profile = player.PlayerProfile!;
                var zaloUserId = NormalizeId(profile.ZaloUserId ?? string.Empty);
                var playerChanged = false;

                if (memberById.TryGetValue(zaloUserId, out var member) &&
                    !string.IsNullOrWhiteSpace(member.AvatarUrl))
                {
                    var freshAvatar = member.AvatarUrl.Trim();
                    if (!string.Equals(player.AvatarUrl, freshAvatar, StringComparison.Ordinal))
                    {
                        player.AvatarUrl = freshAvatar;
                        playerChanged = true;
                    }
                    if (!string.Equals(profile.AvatarUrl, freshAvatar, StringComparison.Ordinal))
                    {
                        profile.AvatarUrl = freshAvatar;
                        playerChanged = true;
                    }
                    profile.LastSyncedAt = now;
                    profile.UpdatedAt = now;
                }
                else if (string.IsNullOrWhiteSpace(player.AvatarUrl) &&
                         !string.IsNullOrWhiteSpace(profile.AvatarUrl))
                {
                    player.AvatarUrl = profile.AvatarUrl;
                    playerChanged = true;
                }

                if (playerChanged) changed += 1;
            }

            // LastSyncedAt intentionally records that the HD-profile refresh was attempted even
            // when the URL string stayed identical; SaveChanges also persists upgraded URLs when
            // Zalo returns a different profile asset.
            if (players.Count > 0) await db.SaveChangesAsync();
            return ServiceResult<int>.Success(changed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<int>(exception);
        }
    }

    public async Task<ServiceResult<ZaloImportPreviewResponse>> CreateImportPreviewAsync(
        string adminUserId,
        string sessionId,
        CreateZaloImportPreviewRequest request)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
        {
            return ServiceResult<ZaloImportPreviewResponse>.Failure(linked.Result.StatusCode, linked.Result.Error!);
        }

        if (linked.Session!.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<ZaloImportPreviewResponse>("Không thể import người chơi sau khi draft đã bắt đầu.");
        }

        try
        {
            var credentials = ReadCredentials(linked.Connection!);
            var poll = await bridge.GetPollAsync(credentials, request.PollId);
            var selection = ValidateSelection(poll, request.SelectedOptionIds);
            if (selection.Error is not null)
            {
                return BadRequest<ZaloImportPreviewResponse>(selection.Error);
            }

            var voterIds = UniqueVoterIds(selection.Options!);
            if (voterIds.Count == 0)
            {
                return BadRequest<ZaloImportPreviewResponse>(
                    "Không đọc được danh sách voter. Poll có thể đang ẩn người vote hoặc không có ai chọn option này.");
            }

            var members = await bridge.GetMembersAsync(credentials, voterIds);
            var preview = await BuildPreviewAsync(linked.Session, poll, selection.Options!, members);
            return ServiceResult<ZaloImportPreviewResponse>.Success(preview);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<ZaloImportPreviewResponse>(exception);
        }
    }

    public async Task<ServiceResult<ZaloPollImportResultResponse>> ConfirmImportAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloPollImportRequest request,
        bool preserveMissingProfileFields = false)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
        {
            return ServiceResult<ZaloPollImportResultResponse>.Failure(linked.Result.StatusCode, linked.Result.Error!);
        }
        if (linked.Session!.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<ZaloPollImportResultResponse>("Không thể import người chơi sau khi draft đã bắt đầu.");
        }

        try
        {
            var credentials = ReadCredentials(linked.Connection!);
            var poll = await bridge.GetPollAsync(credentials, request.PollId);
            if (poll.UpdatedAtUnixMs != request.ExpectedPollUpdatedAtUnixMs)
            {
                return ServiceResult<ZaloPollImportResultResponse>.Failure(
                    StatusCodes.Status409Conflict,
                    "Poll đã thay đổi sau màn hình preview. Hãy tải preview lại trước khi import.");
            }

            var selection = ValidateSelection(poll, request.SelectedOptionIds);
            if (selection.Error is not null)
            {
                return BadRequest<ZaloPollImportResultResponse>(selection.Error);
            }

            var voterIds = UniqueVoterIds(selection.Options!);
            var voterIdSet = voterIds.ToHashSet(StringComparer.Ordinal);
            var decisions = request.Candidates
                .Where(candidate => candidate.Include && voterIdSet.Contains(NormalizeId(candidate.ZaloUserId)))
                .GroupBy(candidate => NormalizeId(candidate.ZaloUserId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            if (decisions.Count == 0)
            {
                return BadRequest<ZaloPollImportResultResponse>("Cần chọn ít nhất một người để import.");
            }

            var members = await bridge.GetMembersAsync(credentials, decisions.Keys.ToList());
            var memberById = members.ToDictionary(member => NormalizeId(member.ZaloUserId), StringComparer.Ordinal);
            var existingProfiles = await db.PlayerProfiles
                .Where(profile => decisions.Keys.Contains(profile.ZaloUserId))
                .ToDictionaryAsync(profile => profile.ZaloUserId, StringComparer.Ordinal);

            await using var transaction = await db.Database.BeginTransactionAsync();
            var now = DateTimeOffset.UtcNow;
            var addedCount = 0;
            var skippedCount = 0;
            var updatedProfileCount = 0;

            foreach (var (zaloUserId, decision) in decisions)
            {
                memberById.TryGetValue(zaloUserId, out var member);
                var existingGender = (PlayerGender?)null;
                var existingRole = (PlayerRole?)null;
                var existingLevel = (PlayerLevel?)null;
                if (!existingProfiles.TryGetValue(zaloUserId, out var profile))
                {
                    profile = new PlayerProfile
                    {
                        ZaloUserId = zaloUserId,
                        DisplayName = member?.DisplayName ?? $"Zalo {zaloUserId}",
                        AvatarUrl = member?.AvatarUrl,
                        CreatedAt = now
                    };
                    db.PlayerProfiles.Add(profile);
                    existingProfiles[zaloUserId] = profile;
                }
                else
                {
                    existingGender = profile.Gender is PlayerGender.Male or PlayerGender.Female ? profile.Gender : null;
                    existingRole = profile.DefaultRole;
                    existingLevel = profile.DefaultLevel;
                }

                profile.DisplayName = member?.DisplayName ?? profile.DisplayName;
                profile.AvatarUrl = member?.AvatarUrl ?? profile.AvatarUrl;
                profile.Gender = preserveMissingProfileFields ? existingGender : decision.Gender;
                profile.DefaultRole = preserveMissingProfileFields ? existingRole : decision.Role;
                profile.DefaultLevel = preserveMissingProfileFields ? existingLevel : decision.Level;
                if (!preserveMissingProfileFields)
                {
                    profile.GenderUpdatedAt = now;
                    profile.GenderUpdatedByUserId = adminUserId;
                }
                profile.LastSyncedAt = now;
                profile.UpdatedAt = now;
                updatedProfileCount += 1;

                var existingSessionPlayer = await db.SessionPlayers.SingleOrDefaultAsync(player =>
                    player.SessionId == sessionId && player.PlayerProfileId == profile.Id);
                var optionIds = selection.Options!
                    .Where(option => option.VoterIds.Select(NormalizeId).Contains(zaloUserId))
                    .Select(option => option.Id)
                    .Distinct()
                    .ToList();

                if (existingSessionPlayer is not null)
                {
                    existingSessionPlayer.DisplayName = profile.DisplayName;
                    existingSessionPlayer.AvatarUrl = profile.AvatarUrl;
                    existingSessionPlayer.Gender = decision.Gender;
                    existingSessionPlayer.Role = decision.Role;
                    existingSessionPlayer.Level = decision.Level;
                    existingSessionPlayer.Score = CalculateScore(decision.Role, decision.Level);
                    existingSessionPlayer.SourcePollId = poll.Id;
                    existingSessionPlayer.SourceOptionIdsJson = JsonSerializer.Serialize(optionIds);
                    existingSessionPlayer.IsPresent = true;
                    skippedCount += 1;
                    continue;
                }

                db.SessionPlayers.Add(new SessionPlayer
                {
                    SessionId = sessionId,
                    PlayerProfileId = profile.Id,
                    DisplayName = profile.DisplayName,
                    AvatarUrl = profile.AvatarUrl,
                    Gender = decision.Gender,
                    Role = decision.Role,
                    Level = decision.Level,
                    Score = CalculateScore(decision.Role, decision.Level),
                    IsPresent = true,
                    IsCaptainEligible = true,
                    SourcePollId = poll.Id,
                    SourceOptionIdsJson = JsonSerializer.Serialize(optionIds),
                    CreatedAt = now
                });
                addedCount += 1;
            }

            db.PollImports.Add(new PollImport
            {
                SessionId = sessionId,
                ImportedByUserId = adminUserId,
                ZaloGroupId = linked.Session.ZaloGroupId!,
                PollId = poll.Id,
                PollQuestion = poll.Question,
                SelectedOptionIdsJson = JsonSerializer.Serialize(selection.Options!.Select(option => option.Id)),
                ImportedPlayerCount = addedCount,
                ImportedAt = now
            });
            linked.Session.UpdatedAt = now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            var sessionPlayerCount = await db.SessionPlayers.CountAsync(player =>
                player.SessionId == sessionId && player.IsPresent);
            var warning = sessionPlayerCount >= linked.Session.TeamCount * 2 &&
                          sessionPlayerCount % linked.Session.TeamCount == 0
                ? string.Empty
                : $" Hiện có {sessionPlayerCount} người, cần tổng số chia hết cho {linked.Session.TeamCount} trước khi draft.";

            return ServiceResult<ZaloPollImportResultResponse>.Success(
                new ZaloPollImportResultResponse(
                    addedCount,
                    updatedProfileCount,
                    skippedCount,
                    sessionPlayerCount,
                    $"Đã import {addedCount} người và bỏ qua {skippedCount} người đã có trong buổi.{warning}"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<ZaloPollImportResultResponse>(exception);
        }
    }

    public async Task<ServiceResult<ZaloPollImportResultResponse>> SyncLatestPollAsync(
        string adminUserId,
        string sessionId,
        string? optionReference = null)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
            return ServiceResult<ZaloPollImportResultResponse>.Failure(linked.Result.StatusCode, linked.Result.Error!);
        if (linked.Session!.Status is SessionStatus.Drafting or SessionStatus.Finished)
            return BadRequest<ZaloPollImportResultResponse>("Không thể đồng bộ vote sau khi draft đã bắt đầu.");

        var leaseToken = Guid.NewGuid().ToString("n");
        var leaseNow = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleAsync();
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= leaseNow)
            return ServiceResult<ZaloPollImportResultResponse>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật buổi này. Hãy thử lại sau ít phút.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "SyncPoll")
                .SetProperty(session => session.BotActionLeaseUntil, leaseNow.AddMinutes(2)));
        if (claimed == 0)
            return ServiceResult<ZaloPollImportResultResponse>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật buổi này. Hãy thử lại sau ít phút.");

        try
        {
            var polls = (await bridge.GetPollsAsync(ReadCredentials(linked.Connection!), linked.Session.ZaloGroupId!))
                .Where(poll => !poll.IsAnonymous)
                .OrderByDescending(poll => poll.UpdatedAtUnixMs)
                .ToList();
            if (polls.Count == 0)
                return BadRequest<ZaloPollImportResultResponse>("Nhóm chưa có poll không ẩn danh để đồng bộ.");

            var previousImports = await db.PollImports.AsNoTracking()
                .Where(item => item.SessionId == sessionId)
                .ToListAsync();
            var previousImport = previousImports
                .OrderByDescending(item => item.ImportedAt)
                .FirstOrDefault();
            var poll = previousImport is null
                ? polls[0]
                : polls.FirstOrDefault(item => item.Id == previousImport.PollId) ?? polls[0];
            var selectedOptionIds = previousImport is not null && previousImport.PollId == poll.Id
                ? ParseStringList(previousImport.SelectedOptionIdsJson)
                : InferPollOptionIds(linked.Session, poll, optionReference);
            selectedOptionIds = selectedOptionIds
                .Where(id => poll.Options.Any(option => option.Id == id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (selectedOptionIds.Count == 0)
            {
                return ServiceResult<ZaloPollImportResultResponse>.Failure(
                    StatusCodes.Status409Conflict,
                    $"Bot chưa xác định được option nào thuộc buổi này. Hãy hỏi lại và thêm đúng tên option. Poll “{poll.Question}” có: {string.Join(", ", poll.Options.Select(option => option.Content))}.");
            }

            var previewResult = await CreateImportPreviewAsync(
                adminUserId,
                sessionId,
                new CreateZaloImportPreviewRequest(poll.Id, selectedOptionIds));
            if (!previewResult.IsSuccess || previewResult.Value is null)
                return ServiceResult<ZaloPollImportResultResponse>.Failure(previewResult.StatusCode, previewResult.Error!);
            var preview = previewResult.Value;
            var decisions = preview.Candidates.Select(candidate => new ZaloImportCandidateDecision(
                candidate.ZaloUserId,
                true,
                candidate.Gender ?? PlayerGender.Unknown,
                candidate.Role,
                candidate.Level)).ToList();
            var importResult = await ConfirmImportAsync(
                adminUserId,
                sessionId,
                new ConfirmZaloPollImportRequest(
                    preview.PollId,
                    selectedOptionIds,
                    preview.PollUpdatedAtUnixMs,
                    decisions),
                preserveMissingProfileFields: true);
            if (!importResult.IsSuccess || importResult.Value is null) return importResult;

            var activeZaloIds = preview.Candidates.Select(candidate => NormalizeId(candidate.ZaloUserId)).ToHashSet(StringComparer.Ordinal);
            var previouslyImportedPlayers = await db.SessionPlayers
                .Include(player => player.PlayerProfile)
                .Where(player => player.SessionId == sessionId && player.SourcePollId == poll.Id)
                .ToListAsync();
            var removedCount = 0;
            var removedPlayerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var player in previouslyImportedPlayers)
            {
                var zaloId = player.PlayerProfile?.ZaloUserId;
                if (string.IsNullOrWhiteSpace(zaloId) || activeZaloIds.Contains(NormalizeId(zaloId))) continue;
                if (player.IsPresent) removedCount += 1;
                player.IsPresent = false;
                player.IsCaptainEligible = false;
                removedPlayerIds.Add(player.Id);
            }
            await ReconcileSharedSlotsAfterPollRemovalAsync(sessionId, removedPlayerIds);
            await CleanupTeamPreferenceGroupsAsync(sessionId);
            await db.SaveChangesAsync();
            var presentCount = await db.SessionPlayers.CountAsync(player => player.SessionId == sessionId && player.IsPresent);
            return ServiceResult<ZaloPollImportResultResponse>.Success(importResult.Value with
            {
                SessionPlayerCount = presentCount,
                Message = $"Đã đồng bộ poll “{preview.PollQuestion}”: {presentCount} người đang có mặt, thêm {importResult.Value.AddedCount}, cập nhật {importResult.Value.SkippedExistingCount}, bỏ {removedCount} người đã rút vote."
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<ZaloPollImportResultResponse>(exception);
        }
        finally
        {
            await db.MatchSessions
                .Where(session => session.Id == sessionId && session.BotActionLeaseToken == leaseToken)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(session => session.BotActionLeaseToken, (string?)null)
                    .SetProperty(session => session.BotActionLeaseName, (string?)null)
                    .SetProperty(session => session.BotActionLeaseUntil, (DateTimeOffset?)null));
        }
    }

    private async Task CleanupTeamPreferenceGroupsAsync(string sessionId)
    {
        var groups = await db.TeamPreferenceGroups
            .Include(group => group.Players)
            .ThenInclude(link => link.SessionPlayer)
            .Where(group => group.SessionId == sessionId)
            .ToListAsync();
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

    private async Task ReconcileSharedSlotsAfterPollRemovalAsync(
        string sessionId,
        IReadOnlySet<string> removedPlayerIds)
    {
        if (removedPlayerIds.Count == 0) return;

        var slots = await db.DraftSlots
            .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
            .ThenInclude(link => link.SessionPlayer)
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                slot.Players.Any(link => removedPlayerIds.Contains(link.SessionPlayerId)))
            .ToListAsync();
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

    public async Task<ServiceResult<ZaloGroupRoleAuthorization>> GetGroupRoleAuthorizationAsync(
        string adminUserId,
        string sessionId,
        string zaloUserId)
    {
        var linked = await GetLinkedSessionAsync(adminUserId, sessionId);
        if (linked.Result is not null)
        {
            return ServiceResult<ZaloGroupRoleAuthorization>.Failure(
                linked.Result.StatusCode,
                linked.Result.Error!);
        }

        try
        {
            var roles = await bridge.GetGroupRolesAsync(
                ReadCredentials(linked.Connection!),
                linked.Session!.ZaloGroupId!);
            var normalizedUserId = NormalizeId(zaloUserId);
            return ServiceResult<ZaloGroupRoleAuthorization>.Success(new(
                normalizedUserId == NormalizeId(roles.CreatorId),
                roles.AdminIds.Select(NormalizeId).Contains(normalizedUserId, StringComparer.Ordinal)));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return BridgeFailure<ZaloGroupRoleAuthorization>(exception);
        }
    }

    private static List<string> InferPollOptionIds(MatchSession session, BridgePoll poll, string? optionReference)
    {
        if (poll.Options.Count == 1) return [poll.Options[0].Id];
        var sessionText = $"{session.Name} {optionReference}";
        if (session.StartTime is not null)
        {
            var local = session.StartTime.Value.ToOffset(TimeSpan.FromHours(7));
            sessionText += $" {local:dd/M} {DayAlias(local.DayOfWeek)}";
        }
        var scored = poll.Options
            .Select(option => new { option.Id, Score = ZaloBotIntelligence.TokenSimilarity(sessionText, option.Content) })
            .OrderByDescending(item => item.Score)
            .ToList();
        if (scored.Count == 0 || scored[0].Score < .5 || (scored.Count > 1 && Math.Abs(scored[0].Score - scored[1].Score) < .05)) return [];
        return [scored[0].Id];
    }

    private static List<string> ParseStringList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private static string DayAlias(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "T2",
        DayOfWeek.Tuesday => "T3",
        DayOfWeek.Wednesday => "T4",
        DayOfWeek.Thursday => "T5",
        DayOfWeek.Friday => "T6",
        DayOfWeek.Saturday => "T7",
        _ => "CN"
    };

    private async Task<ZaloImportPreviewResponse> BuildPreviewAsync(
        MatchSession session,
        BridgePoll poll,
        IReadOnlyList<BridgePollOption> selectedOptions,
        IReadOnlyList<BridgeMember> members)
    {
        var voterIds = UniqueVoterIds(selectedOptions);
        var profiles = await db.PlayerProfiles
            .AsNoTracking()
            .Where(profile => voterIds.Contains(profile.ZaloUserId))
            .ToDictionaryAsync(profile => profile.ZaloUserId, StringComparer.Ordinal);
        var profileIds = profiles.Values.Select(profile => profile.Id).ToList();
        var existingProfileIds = await db.SessionPlayers
            .Where(player => player.SessionId == session.Id &&
                             player.PlayerProfileId != null &&
                             profileIds.Contains(player.PlayerProfileId))
            .Select(player => player.PlayerProfileId!)
            .ToHashSetAsync();
        var memberById = members.ToDictionary(member => NormalizeId(member.ZaloUserId), StringComparer.Ordinal);

        var candidates = voterIds.Select(zaloUserId =>
        {
            profiles.TryGetValue(zaloUserId, out var profile);
            memberById.TryGetValue(zaloUserId, out var member);
            var options = selectedOptions
                .Where(option => option.VoterIds.Select(NormalizeId).Contains(zaloUserId))
                .ToList();
            return new ZaloImportCandidateResponse(
                zaloUserId,
                member?.DisplayName ?? profile?.DisplayName ?? $"Zalo {zaloUserId}",
                member?.AvatarUrl ?? profile?.AvatarUrl,
                profile?.Gender,
                profile?.Gender is null,
                profile?.DefaultRole ?? PlayerRole.New,
                profile?.DefaultLevel ?? PlayerLevel.New,
                profile is not null && existingProfileIds.Contains(profile.Id),
                options.Select(option => option.Id).ToList(),
                options.Select(option => option.Content).ToList());
        }).OrderBy(candidate => candidate.DisplayName).ToList();

        var canDivide = candidates.Count >= session.TeamCount * 2 && candidates.Count % session.TeamCount == 0;
        return new ZaloImportPreviewResponse(
            poll.Id,
            poll.Question,
            selectedOptions.Select(ToOptionResponse).ToList(),
            candidates,
            candidates.Count,
            canDivide,
            canDivide ? candidates.Count / session.TeamCount : null,
            poll.UpdatedAtUnixMs);
    }

    private async Task<(MatchSession? Session, ZaloConnection? Connection, ServiceError? Result)> GetLinkedSessionAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await db.MatchSessions.SingleOrDefaultAsync(item =>
            item.Id == sessionId && item.AdminUserId == adminUserId);
        if (session is null)
        {
            return (null, null, new ServiceError(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu."));
        }
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId))
        {
            return (session, null, new ServiceError(StatusCodes.Status400BadRequest, "Buổi đấu chưa liên kết với nhóm Zalo."));
        }

        var connection = await GetConnectionAsync(adminUserId, session.ZaloConnectionId);
        return connection is null
            ? (session, null, new ServiceError(StatusCodes.Status404NotFound, "Kết nối Zalo không còn tồn tại."))
            : (session, connection, null);
    }

    private async Task<ZaloConnection?> GetConnectionAsync(string adminUserId, string connectionId)
    {
        return await db.ZaloConnections.SingleOrDefaultAsync(connection =>
            connection.Id == connectionId && connection.AdminUserId == adminUserId);
    }

    private JsonElement ReadCredentials(ZaloConnection connection)
    {
        using var document = JsonDocument.Parse(credentialProtector.Unprotect(connection.EncryptedCredentials));
        return document.RootElement.Clone();
    }

    private static (IReadOnlyList<BridgePollOption>? Options, string? Error) ValidateSelection(
        BridgePoll poll,
        IReadOnlyList<string> optionIds)
    {
        if (poll.IsAnonymous)
        {
            return (null, "Poll ẩn danh không thể dùng để import người chơi.");
        }
        var selectedIds = optionIds.Select(NormalizeId).Where(id => id.Length > 0).Distinct().ToHashSet();
        var options = poll.Options.Where(option => selectedIds.Contains(option.Id)).ToList();
        if (options.Count == 0)
        {
            return (null, "Cần chọn ít nhất một option hợp lệ.");
        }
        if (options.Count != selectedIds.Count)
        {
            return (null, "Một hoặc nhiều option không còn tồn tại trong poll.");
        }
        return (options, null);
    }

    private static List<string> UniqueVoterIds(IReadOnlyList<BridgePollOption> options)
    {
        return options
            .SelectMany(option => option.VoterIds)
            .Select(NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeId(string value) => value.Trim().EndsWith("_0", StringComparison.Ordinal)
        ? value.Trim()[..^2]
        : value.Trim();

    private static ZaloConnectionResponse ToConnectionResponse(ZaloConnection connection) => new(
        connection.Id,
        connection.AccountZaloId,
        connection.DisplayName,
        connection.AvatarUrl,
        connection.Status,
        connection.LastValidatedAt);

    private static ZaloPollResponse ToPollResponse(BridgePoll poll) => new(
        poll.Id,
        poll.Question,
        poll.Options.Select(ToOptionResponse).ToList(),
        poll.AllowMultipleChoices,
        poll.IsAnonymous,
        poll.IsClosed,
        poll.HideVotePreview,
        poll.UniqueVoteCount,
        poll.CreatedAtUnixMs,
        poll.UpdatedAtUnixMs,
        poll.ExpiredAtUnixMs);

    private static ZaloPollOptionResponse ToOptionResponse(BridgePollOption option) =>
        new(option.Id, option.Content, option.VoteCount);

    private static SessionResponse ToSessionResponse(MatchSession session) => new(
        session.Id,
        session.Name,
        session.Status,
        session.TeamCount,
        session.TeamSize,
        session.TotalSets,
        session.AdminUserId,
        session.ZaloConnectionId,
        session.ZaloGroupId,
        session.ZaloGroupName,
        session.ZaloGroupAvatarUrl,
        session.StartTime,
        session.Location,
        session.ParkingInstructions,
        session.LocationImageUrl,
        session.PaymentInstructions,
        session.PaymentQrImageUrl,
        session.BotEnabled,
        session.BotCustomInstructions,
        session.ReminderEnabled,
        session.ReminderLeadHours,
        session.ReminderIntervalHours,
        session.LastReminderAt,
        session.NextReminderAt,
        session.ReminderRepeats,
        session.Teams.OrderBy(team => team.Name).Select(team => new TeamSummary(team.Id, team.Name)).ToList());

    private static double CalculateScore(PlayerRole role, PlayerLevel level)
    {
        var baseScore = level switch
        {
            PlayerLevel.Good => 3,
            PlayerLevel.Average => 2,
            PlayerLevel.New => 1,
            _ => 2
        };
        return role == PlayerRole.FullStack ? baseScore + 0.5 : baseScore;
    }

    private async Task<ServiceResult<string>> ResolveZyroMappingAdminUserIdAsync(
        CancellationToken cancellationToken)
    {
        var configuredAdminUserId = configuration?["ZyroMapping:AdminUserId"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredAdminUserId))
        {
            var hasConnectedAccount = await db.ZaloConnections
                .AsNoTracking()
                .AnyAsync(connection =>
                    connection.AdminUserId == configuredAdminUserId &&
                    connection.Status == ZaloConnectionStatus.Connected,
                    cancellationToken);
            return hasConnectedAccount
                ? ServiceResult<string>.Success(configuredAdminUserId)
                : ServiceResult<string>.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "ZyroMapping:AdminUserId chưa sở hữu tài khoản Zalo nào đang kết nối trong Draft.");
        }

        var ownerIds = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.Status == ZaloConnectionStatus.Connected)
            .Select(connection => connection.AdminUserId)
            .Distinct()
            .Take(2)
            .ToListAsync(cancellationToken);

        return ownerIds.Count switch
        {
            0 => ServiceResult<string>.Failure(
                StatusCodes.Status404NotFound,
                "Draft chưa có tài khoản Zalo nào đang kết nối."),
            1 => ServiceResult<string>.Success(ownerIds[0]),
            _ => ServiceResult<string>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "Draft có nhiều tài khoản quản trị Zalo. Hãy cấu hình ZyroMapping:AdminUserId để giới hạn tài khoản được chia sẻ với Form.")
        };
    }

    private async Task<ServiceResult<BridgeGroupMemberDirectory>> GetZyroMappingGroupDirectoryAsync(
        ZaloConnection connection,
        string groupId,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = ReadCredentials(connection);
            var groups = await bridge.GetGroupsAsync(credentials);
            if (!groups.Any(group => NormalizeId(group.Id ?? string.Empty) == groupId))
            {
                return ServiceResult<BridgeGroupMemberDirectory>.Failure(
                    StatusCodes.Status404NotFound,
                    "Tài khoản Draft không còn trong nhóm Zalo đã chọn.");
            }

            var directory = await bridge.GetGroupMemberDirectoryAsync(
                credentials,
                groupId,
                cancellationToken);
            if (!string.Equals(NormalizeId(directory.GroupId ?? string.Empty), groupId, StringComparison.Ordinal))
            {
                return ServiceResult<BridgeGroupMemberDirectory>.Failure(
                    StatusCodes.Status502BadGateway,
                    "Zalo Bridge trả về danh bạ của nhóm khác với nhóm đã yêu cầu.");
            }

            return ServiceResult<BridgeGroupMemberDirectory>.Success(directory);
        }
        catch (Exception exception) when (IsZyroMappingBridgeFailure(exception))
        {
            return ZyroMappingBridgeFailure<BridgeGroupMemberDirectory>(
                exception,
                "Không thể đọc danh sách thành viên nhóm Zalo từ Draft Bridge.");
        }
    }

    private static bool IsZyroMappingBridgeFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or
            CryptographicException or FormatException or JsonException;

    private static ServiceResult<T> ZyroMappingBridgeFailure<T>(Exception exception, string fallbackMessage)
    {
        if (exception is ZaloBridgeRequestException bridgeException)
        {
            var statusCode = bridgeException.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status502BadGateway;
            return ServiceResult<T>.Failure(statusCode, bridgeException.Message);
        }

        return ServiceResult<T>.Failure(StatusCodes.Status502BadGateway, fallbackMessage);
    }

    private static ServiceResult<T> Unauthorized<T>() =>
        ServiceResult<T>.Failure(StatusCodes.Status401Unauthorized, "Phiên đăng nhập admin không hợp lệ.");
    private static ServiceResult<T> NotFound<T>(string message) =>
        ServiceResult<T>.Failure(StatusCodes.Status404NotFound, message);
    private static ServiceResult<T> BadRequest<T>(string message) =>
        ServiceResult<T>.Failure(StatusCodes.Status400BadRequest, message);
    private static ServiceResult<T> BridgeFailure<T>(Exception exception) =>
        ServiceResult<T>.Failure(StatusCodes.Status502BadGateway, $"Không thể đọc dữ liệu Zalo: {exception.Message}");

    private sealed record ServiceError(int StatusCode, string Error);
}

public sealed record ZaloGroupRoleAuthorization(bool IsOwner, bool IsDeputy)
{
    public bool CanOperateBot => IsOwner || IsDeputy;
}
