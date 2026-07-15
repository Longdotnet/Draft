using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class SessionDraftService(VolleyDraftDbContext db)
{
    private static readonly string[] TeamNames = ["Team A", "Team B", "Team C"];
    private static readonly string[] RoundLabels =
    [
        "Cầu thủ mạnh",
        "Nhóm ổn định",
        "Nhóm cân bằng",
        "Nhóm tiềm năng",
        "Nhóm bất ngờ"
    ];

    public async Task<ServiceResult<SessionResponse>> CreateSessionAsync(
        string adminUserId,
        CreateSessionRequest request)
    {
        if (!await db.Users.AnyAsync(user => user.Id == adminUserId))
        {
            return ServiceResult<SessionResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Admin token is no longer valid. Please logout and login again.");
        }

        if (request.TeamCount != 3 || request.TeamSize < 2)
        {
            return ServiceResult<SessionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "MVP hiện hỗ trợ 3 team; số slot mỗi team sẽ tự tính theo số người có mặt.");
        }

        var session = new MatchSession
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"Volley Draft {DateTimeOffset.Now:yyyy-MM-dd}"
                : request.Name.Trim(),
            AdminUserId = adminUserId,
            TeamCount = request.TeamCount,
            TeamSize = request.TeamSize,
            TotalSets = request.TotalSets
        };

        foreach (var teamName in TeamNames)
        {
            session.Teams.Add(new Team
            {
                SessionId = session.Id,
                Name = teamName
            });
        }

        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();

        return ServiceResult<SessionResponse>.Created(ToSessionResponse(session));
    }

    public async Task<ServiceResult<SessionResponse>> GetSessionAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .SingleOrDefaultAsync();

        return session is null
            ? NotFound<SessionResponse>("Không tìm thấy session.")
            : ServiceResult<SessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<ServiceResult<PagedResponse<AdminSessionSummaryResponse>>> GetSessionsAsync(
        string adminUserId,
        int page,
        int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? 5 : Math.Clamp(pageSize, 1, 20);

        var query = db.MatchSessions
            .AsNoTracking()
            .Where(session => session.AdminUserId == adminUserId)
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.CreatedAt);

        var totalItems = await query.CountAsync();
        var sessions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var sessionIds = sessions.Select(session => session.Id).ToList();
        var playerCounts = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent)
            .GroupBy(player => player.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.SessionId, group => group.Count);
        var totalPages = totalItems == 0
            ? 0
            : (int)Math.Ceiling(totalItems / (double)pageSize);

        var items = sessions
            .Select(session => ToAdminSessionSummaryResponse(
                session,
                playerCounts.GetValueOrDefault(session.Id)))
            .ToList();

        return ServiceResult<PagedResponse<AdminSessionSummaryResponse>>.Success(
            new PagedResponse<AdminSessionSummaryResponse>(items, page, pageSize, totalItems, totalPages));
    }

    public async Task<ServiceResult<SessionResponse>> UpdateSessionAsync(
        string adminUserId,
        string sessionId,
        UpdateSessionRequest request)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<SessionResponse>("Session not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest<SessionResponse>("Session name is required.");
        }

        session.Name = request.Name.Trim();
        session.TotalSets = Math.Max(1, request.TotalSets);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<SessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<ServiceResult<DeleteResponse>> DeleteSessionAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DeleteResponse>("Session not found.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var slotIds = await db.DraftSlots
            .Where(slot => slot.SessionId == sessionId)
            .Select(slot => slot.Id)
            .ToListAsync();
        var preferenceGroupIds = await db.TeamPreferenceGroups
            .Where(group => group.SessionId == sessionId)
            .Select(group => group.Id)
            .ToListAsync();

        await db.DraftTurns.Where(turn => turn.SessionId == sessionId).ExecuteDeleteAsync();
        await db.BlindBags.Where(bag => bag.SessionId == sessionId).ExecuteDeleteAsync();
        await db.DraftRounds.Where(round => round.SessionId == sessionId).ExecuteDeleteAsync();
        await db.DraftSlotPlayers
            .Where(slotPlayer => slotIds.Contains(slotPlayer.DraftSlotId))
            .ExecuteDeleteAsync();
        await db.DraftSlots.Where(slot => slot.SessionId == sessionId).ExecuteDeleteAsync();
        await db.TeamPreferenceGroupPlayers
            .Where(groupPlayer => preferenceGroupIds.Contains(groupPlayer.TeamPreferenceGroupId))
            .ExecuteDeleteAsync();
        await db.TeamPreferenceGroups.Where(group => group.SessionId == sessionId).ExecuteDeleteAsync();
        await db.Teams.Where(team => team.SessionId == sessionId).ExecuteDeleteAsync();
        await db.SessionPlayers.Where(player => player.SessionId == sessionId).ExecuteDeleteAsync();
        await db.MatchSessions.Where(item => item.Id == sessionId).ExecuteDeleteAsync();
        await transaction.CommitAsync();

        return ServiceResult<DeleteResponse>.Success(new DeleteResponse("Session deleted."));
    }

    public async Task<ServiceResult<PagedResponse<PublicSessionSummaryResponse>>> GetPublicSessionsAsync(
        int page,
        int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? 3 : Math.Clamp(pageSize, 1, 3);

        var allSessions = await db.MatchSessions
            .AsNoTracking()
            .ToListAsync();
        var totalItems = allSessions.Count;
        var sessions = allSessions
            .OrderByDescending(session => session.CreatedAt)
            .ThenByDescending(session => session.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var sessionIds = sessions.Select(session => session.Id).ToList();
        var playerCounts = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent)
            .GroupBy(player => player.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.SessionId, group => group.Count);

        var items = sessions
            .Select(session => ToPublicSessionSummaryResponse(
                session,
                playerCounts.GetValueOrDefault(session.Id)))
            .ToList();
        var totalPages = totalItems == 0
            ? 0
            : (int)Math.Ceiling(totalItems / (double)pageSize);

        return ServiceResult<PagedResponse<PublicSessionSummaryResponse>>.Success(
            new PagedResponse<PublicSessionSummaryResponse>(items, page, pageSize, totalItems, totalPages));
    }

    public async Task<ServiceResult<PagedResponse<SessionPlayerResponse>>> GetPublicPlayersAsync(
        string sessionId,
        int page,
        int pageSize)
    {
        if (!await db.MatchSessions.AnyAsync(session => session.Id == sessionId))
        {
            return NotFound<PagedResponse<SessionPlayerResponse>>("Không tìm thấy session.");
        }

        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? 6 : Math.Clamp(pageSize, 1, 12);

        var query = db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .OrderBy(player => player.DisplayName);
        var totalItems = await query.CountAsync();
        var players = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var totalPages = totalItems == 0
            ? 0
            : (int)Math.Ceiling(totalItems / (double)pageSize);

        return ServiceResult<PagedResponse<SessionPlayerResponse>>.Success(
            new PagedResponse<SessionPlayerResponse>(
                players.Select(ToPlayerResponse).ToList(),
                page,
                pageSize,
                totalItems,
                totalPages));
    }

    public async Task<ServiceResult<CaptainsResponse>> GetPublicCaptainsAsync(string sessionId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<CaptainsResponse>("Không tìm thấy session.")
            : await GetCaptainsAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<CaptainsResponse>> AutoSelectPublicCaptainsAsync(string sessionId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<CaptainsResponse>("Không tìm thấy session.")
            : await AutoSelectCaptainsAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<DraftStateResponse>> StartPublicDraftAsync(string sessionId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<DraftStateResponse>("Không tìm thấy session.")
            : await StartDraftAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<DraftStateResponse>> GetPublicDraftStateAsync(string sessionId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<DraftStateResponse>("Không tìm thấy session.")
            : await GetDraftStateAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<PrepareRevealResponse>> PreparePublicBagRevealAsync(
        string sessionId,
        string bagId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<PrepareRevealResponse>("Không tìm thấy session.")
            : await PrepareBagRevealAsync(adminUserId, sessionId, bagId);
    }

    public async Task<ServiceResult<OpenBagResponse>> OpenPublicBagAsync(
        string sessionId,
        string bagId)
    {
        var adminUserId = await GetSessionAdminUserId(sessionId);
        return adminUserId is null
            ? NotFound<OpenBagResponse>("Không tìm thấy session.")
            : await OpenBagAsync(adminUserId, sessionId, bagId);
    }

    public async Task<ServiceResult<SessionPlayerResponse>> AddPlayerAsync(
        string adminUserId,
        string sessionId,
        AddPlayerRequest request)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<SessionPlayerResponse>("Không tìm thấy session.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<SessionPlayerResponse>("Không thể thêm người chơi sau khi draft đã bắt đầu.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return BadRequest<SessionPlayerResponse>("Tên người chơi là bắt buộc.");
        }

        var player = new SessionPlayer
        {
            SessionId = session.Id,
            DisplayName = request.DisplayName.Trim(),
            Role = request.Role,
            Level = request.Level,
            Gender = request.Gender,
            Score = CalculateScore(request.Role, request.Level),
            IsPresent = request.IsPresent,
            IsCaptainEligible = request.IsCaptainEligible
        };

        db.SessionPlayers.Add(player);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<SessionPlayerResponse>.Created(ToPlayerResponse(player));
    }

    public async Task<ServiceResult<IReadOnlyList<IncompletePlayerProfile>>> GetIncompletePlayerProfilesAsync(
        string adminUserId,
        string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
            return NotFound<IReadOnlyList<IncompletePlayerProfile>>("Không tìm thấy session.");
        var players = await db.SessionPlayers.AsNoTracking()
            .Where(player => player.SessionId == sessionId && player.IsPresent &&
                             (player.Gender == PlayerGender.Unknown ||
                              (player.PlayerProfile != null &&
                               (player.PlayerProfile.Gender == null ||
                                player.PlayerProfile.Gender == PlayerGender.Unknown ||
                                player.PlayerProfile.DefaultRole == null ||
                                player.PlayerProfile.DefaultLevel == null))))
            .OrderBy(player => player.DisplayName)
            .Select(player => new IncompletePlayerProfile(
                player.Id,
                player.DisplayName,
                player.Gender,
                player.Role,
                player.Level,
                player.Gender == PlayerGender.Unknown ||
                (player.PlayerProfile != null &&
                 (player.PlayerProfile.Gender == null || player.PlayerProfile.Gender == PlayerGender.Unknown)),
                player.PlayerProfile != null && player.PlayerProfile.DefaultRole == null,
                player.PlayerProfile != null && player.PlayerProfile.DefaultLevel == null))
            .ToListAsync();
        return ServiceResult<IReadOnlyList<IncompletePlayerProfile>>.Success(players);
    }

    public async Task<ServiceResult<SessionPlayerResponse>> UpdatePlayerProfileFromBotAsync(
        string adminUserId,
        string sessionId,
        string playerReference,
        PlayerGender? gender,
        PlayerRole? role,
        PlayerLevel? level)
    {
        if (gender is null && role is null && level is null)
            return BadRequest<SessionPlayerResponse>("Cần cung cấp ít nhất giới tính, vị trí hoặc trình độ.");
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null) return NotFound<SessionPlayerResponse>("Không tìm thấy session.");
        if (session.Status == SessionStatus.Drafting)
            return BadRequest<SessionPlayerResponse>("Không cập nhật hồ sơ khi draft đang chạy.");

        var players = await db.SessionPlayers
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .ToListAsync();
        var resolved = ResolveSessionPlayer(players, playerReference);
        if (resolved.Player is null) return BadRequest<SessionPlayerResponse>(resolved.Error!);
        var player = resolved.Player;
        if (gender is not null) player.Gender = gender.Value;
        if (role is not null) player.Role = role.Value;
        if (level is not null) player.Level = level.Value;
        player.Score = CalculateScore(player.Role, player.Level);

        if (player.PlayerProfile is not null)
        {
            if (gender is not null)
            {
                player.PlayerProfile.Gender = gender.Value;
                player.PlayerProfile.DefaultRole ??= player.Role;
                player.PlayerProfile.DefaultLevel ??= player.Level;
                player.PlayerProfile.GenderUpdatedAt = DateTimeOffset.UtcNow;
                player.PlayerProfile.GenderUpdatedByUserId = adminUserId;
            }
            if (role is not null) player.PlayerProfile.DefaultRole = role.Value;
            if (level is not null) player.PlayerProfile.DefaultLevel = level.Value;
            player.PlayerProfile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var affectedSlots = await db.DraftSlots
            .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
            .ThenInclude(link => link.SessionPlayer)
            .Where(slot => slot.SessionId == sessionId && slot.Players.Any(link => link.SessionPlayerId == player.Id))
            .ToListAsync();
        var affectedTeamIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in affectedSlots)
        {
            var slotPlayers = slot.Players.Select(link => link.SessionPlayer).ToList();
            slot.DisplayName = string.Join(" / ", slotPlayers.Select(item => item.DisplayName));
            slot.AverageScore = slotPlayers.Average(item => item.Score);
            slot.Gender = CombinedGender(slotPlayers);
            if (slot.Type == DraftSlotType.Single)
                slot.Role = player.Role;
            if (slot.AssignedTeamId is not null) affectedTeamIds.Add(slot.AssignedTeamId);
        }
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        foreach (var teamId in affectedTeamIds) await RecalculateTeamScore(teamId);
        return ServiceResult<SessionPlayerResponse>.Success(ToPlayerResponse(player));
    }

    public async Task<ServiceResult<GuestPlayerAddResult>> AddGuestPlayerFromBotAsync(
        string adminUserId,
        string sessionId,
        string displayName)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null) return NotFound<GuestPlayerAddResult>("Không tìm thấy session.");
        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
            return BadRequest<GuestPlayerAddResult>("Chỉ có thể +1 khách trước khi draft bắt đầu.");
        var cleanName = displayName.Trim();
        if (cleanName.Length is < 2 or > 160)
            return BadRequest<GuestPlayerAddResult>("Tên hoặc mô tả khách không hợp lệ.");
        var existingNames = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .Select(player => player.DisplayName)
            .ToListAsync();
        if (existingNames.Any(name => ZaloBotIntelligence.Normalize(name) == ZaloBotIntelligence.Normalize(cleanName)))
            return ServiceResult<GuestPlayerAddResult>.Failure(StatusCodes.Status409Conflict, $"{cleanName} đã có trong danh sách.");

        var player = new SessionPlayer
        {
            SessionId = sessionId,
            DisplayName = cleanName,
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            Score = CalculateScore(PlayerRole.New, PlayerLevel.New),
            IsPresent = true,
            IsCaptainEligible = false
        };
        db.SessionPlayers.Add(player);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var count = await db.SessionPlayers.CountAsync(item => item.SessionId == sessionId && item.IsPresent);
        return ServiceResult<GuestPlayerAddResult>.Created(new(ToPlayerResponse(player), count, session.TeamCount));
    }

    public async Task<ServiceResult<PreDraftSharedSlotResult>> SharePreDraftSlotAsync(
        string adminUserId,
        string sessionId,
        string anchorPlayerReference,
        IReadOnlyList<ShareSlotParticipantInput> partnerInputs)
    {
        var inputs = partnerInputs
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .Select(item => item with
            {
                DisplayName = item.DisplayName.Trim().TrimStart('@'),
                ZaloUserId = string.IsNullOrWhiteSpace(item.ZaloUserId)
                    ? null
                    : NormalizeZaloId(item.ZaloUserId)
            })
            .DistinctBy(item => item.ZaloUserId ?? ZaloBotIntelligence.Normalize(item.DisplayName), StringComparer.Ordinal)
            .ToList();
        if (inputs.Count is < 1 or > 2)
            return BadRequest<PreDraftSharedSlotResult>("Share slot chỉ nhận +1 hoặc +2 người; +2 phải có đúng hai tên khác nhau.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null) return NotFound<PreDraftSharedSlotResult>("Không tìm thấy session.");
        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
            return BadRequest<PreDraftSharedSlotResult>("Chỉ ghép share slot kiểu này trước khi draft bắt đầu.");

        var sessionPlayers = await db.SessionPlayers
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId)
            .ToListAsync();
        var anchorResolution = ResolveSessionPlayer(sessionPlayers.Where(player => player.IsPresent).ToList(), anchorPlayerReference);
        if (anchorResolution.Player is null)
            return BadRequest<PreDraftSharedSlotResult>(anchorResolution.Error!);
        var anchor = anchorResolution.Player;

        var currentSlot = await db.DraftSlots
            .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
            .ThenInclude(link => link.SessionPlayer)
            .SingleOrDefaultAsync(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                slot.Players.Any(link => link.SessionPlayerId == anchor.Id));
        if (currentSlot is null && anchor.IsInsideSharedSlot)
            return ServiceResult<PreDraftSharedSlotResult>.Failure(StatusCodes.Status409Conflict, "Dữ liệu share slot của người chơi đang không đồng bộ.");
        if ((currentSlot?.Players.Count ?? 1) + inputs.Count > 3)
            return BadRequest<PreDraftSharedSlotResult>("Một share slot chỉ hỗ trợ người chính và tối đa 2 người chơi chung.");

        var addedPlayers = new List<SessionPlayer>();
        var newlyAddedNames = new List<string>();
        foreach (var input in inputs)
        {
            SessionPlayer? partner = null;
            if (input.ZaloUserId is not null)
            {
                partner = sessionPlayers.FirstOrDefault(player =>
                    NormalizeZaloId(player.PlayerProfile?.ZaloUserId) == input.ZaloUserId);
            }
            partner ??= sessionPlayers.FirstOrDefault(player =>
                ZaloBotIntelligence.Normalize(player.DisplayName) == ZaloBotIntelligence.Normalize(input.DisplayName));

            if (partner is null)
            {
                PlayerProfile? profile = null;
                if (input.ZaloUserId is not null)
                {
                    profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.ZaloUserId == input.ZaloUserId);
                    if (profile is null)
                    {
                        profile = new PlayerProfile
                        {
                            ZaloUserId = input.ZaloUserId,
                            DisplayName = input.DisplayName,
                            AvatarUrl = input.AvatarUrl,
                            DefaultRole = PlayerRole.New,
                            DefaultLevel = PlayerLevel.New
                        };
                        db.PlayerProfiles.Add(profile);
                    }
                    else
                    {
                        profile.DisplayName = input.DisplayName;
                        profile.AvatarUrl ??= input.AvatarUrl;
                        profile.DefaultRole ??= PlayerRole.New;
                        profile.DefaultLevel ??= PlayerLevel.New;
                        profile.LastSyncedAt = DateTimeOffset.UtcNow;
                        profile.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }

                partner = new SessionPlayer
                {
                    SessionId = sessionId,
                    PlayerProfileId = profile?.Id,
                    PlayerProfile = profile,
                    DisplayName = input.DisplayName,
                    AvatarUrl = input.AvatarUrl ?? profile?.AvatarUrl,
                    Gender = profile?.Gender is PlayerGender.Male or PlayerGender.Female
                        ? profile.Gender.Value
                        : PlayerGender.Unknown,
                    Role = profile?.DefaultRole ?? PlayerRole.New,
                    Level = profile?.DefaultLevel ?? PlayerLevel.New,
                    Score = CalculateScore(profile?.DefaultRole ?? PlayerRole.New, profile?.DefaultLevel ?? PlayerLevel.New),
                    IsPresent = true,
                    IsCaptainEligible = false
                };
                db.SessionPlayers.Add(partner);
                sessionPlayers.Add(partner);
                newlyAddedNames.Add(partner.DisplayName);
            }
            else
            {
                partner.IsPresent = true;
            }

            if (partner.Id == anchor.Id)
                return BadRequest<PreDraftSharedSlotResult>("Người chính không thể tự share slot với chính mình.");
            if (addedPlayers.Any(item => item.Id == partner.Id))
                return BadRequest<PreDraftSharedSlotResult>("Danh sách +2 phải gồm hai người khác nhau.");
            if (partner.IsInsideSharedSlot && currentSlot?.Players.All(link => link.SessionPlayerId != partner.Id) != false)
                return BadRequest<PreDraftSharedSlotResult>($"{partner.DisplayName} đang nằm trong một share slot khác.");
            if (currentSlot?.Players.Any(link => link.SessionPlayerId == partner.Id) == true)
                return ServiceResult<PreDraftSharedSlotResult>.Failure(StatusCodes.Status409Conflict, $"{partner.DisplayName} đã share slot với {anchor.DisplayName}.");
            addedPlayers.Add(partner);
        }

        var slot = currentSlot ?? new DraftSlot
        {
            SessionId = sessionId,
            Type = DraftSlotType.Shared,
            Role = anchor.Role
        };
        if (currentSlot is null)
        {
            slot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = slot.Id,
                SessionPlayerId = anchor.Id,
                SessionPlayer = anchor,
                RotationOrder = 1
            });
            anchor.IsInsideSharedSlot = true;
            db.DraftSlots.Add(slot);
        }
        var nextOrder = slot.Players.Count + 1;
        foreach (var partner in addedPlayers)
        {
            slot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = slot.Id,
                SessionPlayerId = partner.Id,
                SessionPlayer = partner,
                RotationOrder = nextOrder++
            });
            partner.IsInsideSharedSlot = true;
        }
        var allSlotPlayers = slot.Players.OrderBy(link => link.RotationOrder).Select(link => link.SessionPlayer).ToList();
        slot.DisplayName = string.Join(" / ", allSlotPlayers.Select(player => player.DisplayName));
        slot.AverageScore = allSlotPlayers.Average(player => player.Score);
        slot.Gender = CombinedGender(allSlotPlayers);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var presentPlayers = await db.SessionPlayers.CountAsync(player => player.SessionId == sessionId && player.IsPresent);
        var sharedPlayerCount = await db.DraftSlotPlayers
            .CountAsync(link => link.DraftSlot.SessionId == sessionId && link.DraftSlot.Type == DraftSlotType.Shared && link.SessionPlayer.IsPresent);
        var sharedSlotCount = await db.DraftSlots
            .CountAsync(item => item.SessionId == sessionId && item.Type == DraftSlotType.Shared && item.Players.Any(link => link.SessionPlayer.IsPresent));
        var effectiveSlotCount = presentPlayers - sharedPlayerCount + sharedSlotCount;
        var incompleteNames = allSlotPlayers
            .Where(player => player.Gender == PlayerGender.Unknown ||
                             (player.PlayerProfile is not null &&
                              (player.PlayerProfile.Gender is null or PlayerGender.Unknown ||
                               player.PlayerProfile.DefaultRole is null ||
                               player.PlayerProfile.DefaultLevel is null)))
            .Select(player => player.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await transaction.CommitAsync();
        return ServiceResult<PreDraftSharedSlotResult>.Created(new(
            anchor.DisplayName,
            addedPlayers.Select(player => player.DisplayName).ToList(),
            newlyAddedNames,
            slot.DisplayName,
            presentPlayers,
            effectiveSlotCount,
            incompleteNames));
    }

    public async Task<ServiceResult<IReadOnlyList<SessionPlayerResponse>>> GetPlayersAsync(
        string adminUserId,
        string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
        {
            return NotFound<IReadOnlyList<SessionPlayerResponse>>("Không tìm thấy session.");
        }

        var players = await db.SessionPlayers
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == sessionId)
            .OrderBy(player => player.DisplayName)
            .ToListAsync();

        return ServiceResult<IReadOnlyList<SessionPlayerResponse>>.Success(
            players.Select(ToPlayerResponse).ToList());
    }

    public async Task<ServiceResult<SessionPlayerResponse>> UpdatePlayerAsync(
        string adminUserId,
        string sessionId,
        string playerId,
        UpdatePlayerRequest request)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<SessionPlayerResponse>("Session not found.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<SessionPlayerResponse>("Cannot edit players after draft has started.");
        }

        var player = await db.SessionPlayers
            .Include(item => item.PlayerProfile)
            .SingleOrDefaultAsync(item => item.Id == playerId && item.SessionId == sessionId);
        if (player is null)
        {
            return NotFound<SessionPlayerResponse>("Player not found.");
        }

        if (player.IsInsideSharedSlot)
        {
            return BadRequest<SessionPlayerResponse>("Remove the shared slot before editing this player.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return BadRequest<SessionPlayerResponse>("Player name is required.");
        }

        var score = CalculateScore(request.Role, request.Level);
        var captainTeam = session.Teams.SingleOrDefault(team => team.CaptainSessionPlayerId == player.Id);
        var shouldRemoveCaptain = captainTeam is not null && (!request.IsPresent || !request.IsCaptainEligible);

        player.DisplayName = request.DisplayName.Trim();
        player.Role = request.Role;
        player.Level = request.Level;
        player.Gender = request.Gender;
        player.Score = score;
        player.IsPresent = request.IsPresent;
        player.IsCaptainEligible = request.IsCaptainEligible;

        if (player.PlayerProfile is not null)
        {
            player.PlayerProfile.DisplayName = player.DisplayName;
            player.PlayerProfile.Gender = request.Gender;
            player.PlayerProfile.DefaultRole = request.Role;
            player.PlayerProfile.DefaultLevel = request.Level;
            player.PlayerProfile.GenderUpdatedAt = DateTimeOffset.UtcNow;
            player.PlayerProfile.GenderUpdatedByUserId = adminUserId;
            player.PlayerProfile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (shouldRemoveCaptain && captainTeam is not null)
        {
            captainTeam.CaptainSessionPlayerId = null;
            await RemoveCaptainSlotForPlayer(sessionId, player.Id);
            session.Status = SessionStatus.Setup;
        }
        else if (captainTeam is not null)
        {
            var captainSlot = await db.DraftSlots
                .SingleOrDefaultAsync(slot =>
                    slot.SessionId == sessionId &&
                    slot.IsCaptainSlot &&
                    slot.Players.Any(slotPlayer => slotPlayer.SessionPlayerId == player.Id));
            if (captainSlot is not null)
            {
                captainSlot.DisplayName = player.DisplayName;
                captainSlot.Role = player.Role;
                captainSlot.Gender = player.Gender;
                captainSlot.AverageScore = player.Score;
            }
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (captainTeam is not null)
        {
            await RecalculateTeamScore(captainTeam.Id);
        }

        return ServiceResult<SessionPlayerResponse>.Success(ToPlayerResponse(player));
    }

    public async Task<ServiceResult<DeleteResponse>> DeletePlayerAsync(
        string adminUserId,
        string sessionId,
        string playerId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DeleteResponse>("Session not found.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<DeleteResponse>("Cannot delete players after draft has started.");
        }

        var player = await db.SessionPlayers
            .SingleOrDefaultAsync(item => item.Id == playerId && item.SessionId == sessionId);
        if (player is null)
        {
            return NotFound<DeleteResponse>("Player not found.");
        }

        if (player.IsInsideSharedSlot)
        {
            return BadRequest<DeleteResponse>("Remove the shared slot before deleting this player.");
        }

        if (await db.TeamPreferenceGroupPlayers.AnyAsync(groupPlayer => groupPlayer.SessionPlayerId == player.Id))
        {
            return BadRequest<DeleteResponse>("Xóa nhóm muốn chung team trước khi xóa player này.");
        }

        var captainTeam = session.Teams.SingleOrDefault(team => team.CaptainSessionPlayerId == player.Id);
        if (captainTeam is not null)
        {
            captainTeam.CaptainSessionPlayerId = null;
            await RemoveCaptainSlotForPlayer(sessionId, player.Id);
            session.Status = SessionStatus.Setup;
        }

        db.SessionPlayers.Remove(player);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (captainTeam is not null)
        {
            await RecalculateTeamScore(captainTeam.Id);
        }

        return ServiceResult<DeleteResponse>.Success(new DeleteResponse("Player deleted."));
    }

    public async Task<ServiceResult<SharedSlotResponse>> CreateSharedSlotAsync(
        string adminUserId,
        string sessionId,
        CreateSharedSlotRequest request)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<SharedSlotResponse>("Không tìm thấy session.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<SharedSlotResponse>("Không thể tạo slot thay phiên sau khi draft đã bắt đầu.");
        }

        var playerIds = request.SessionPlayerIds.Distinct().ToList();
        if (playerIds.Count < 2)
        {
            return BadRequest<SharedSlotResponse>("Slot thay phiên cần ít nhất 2 người chơi.");
        }

        var players = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && playerIds.Contains(player.Id))
            .ToListAsync();

        if (players.Count != playerIds.Count)
        {
            return BadRequest<SharedSlotResponse>("Một hoặc nhiều người chơi không thuộc session này.");
        }

        if (players.Any(player => !player.IsPresent || player.IsInsideSharedSlot))
        {
            return BadRequest<SharedSlotResponse>("Người chơi phải có mặt và chưa nằm trong slot thay phiên khác.");
        }

        var slot = new DraftSlot
        {
            SessionId = sessionId,
            Type = DraftSlotType.Shared,
            DisplayName = string.Join(" / ", players.Select(player => player.DisplayName)),
            Role = request.Role,
            Gender = players.Any(player => player.Gender == PlayerGender.Female)
                ? PlayerGender.Female
                : players.Any(player => player.Gender == PlayerGender.Unknown)
                    ? PlayerGender.Unknown
                    : PlayerGender.Male,
            AverageScore = players.Average(player => player.Score)
        };

        for (var index = 0; index < players.Count; index += 1)
        {
            slot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = slot.Id,
                SessionPlayerId = players[index].Id,
                SessionPlayer = players[index],
                RotationOrder = index + 1
            });
            players[index].IsInsideSharedSlot = true;
        }

        db.DraftSlots.Add(slot);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<SharedSlotResponse>.Created(ToSharedSlotResponse(slot));
    }

    public async Task<ServiceResult<IReadOnlyList<SharedSlotResponse>>> GetSharedSlotsAsync(
        string adminUserId,
        string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
        {
            return NotFound<IReadOnlyList<SharedSlotResponse>>("Không tìm thấy session.");
        }

        var sharedSlots = await db.DraftSlots
            .Include(slot => slot.Players.OrderBy(player => player.RotationOrder))
            .ThenInclude(slotPlayer => slotPlayer.SessionPlayer)
            .Where(slot => slot.SessionId == sessionId && slot.Type == DraftSlotType.Shared)
            .OrderBy(slot => slot.DisplayName)
            .ToListAsync();

        return ServiceResult<IReadOnlyList<SharedSlotResponse>>.Success(
            sharedSlots.Select(ToSharedSlotResponse).ToList());
    }

    public async Task<ServiceResult<DeleteResponse>> DeleteSharedSlotAsync(
        string adminUserId,
        string sessionId,
        string slotId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DeleteResponse>("Session not found.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<DeleteResponse>("Cannot delete shared slots after draft has started.");
        }

        var slot = await db.DraftSlots
            .Include(item => item.Players)
            .ThenInclude(slotPlayer => slotPlayer.SessionPlayer)
            .SingleOrDefaultAsync(item =>
                item.Id == slotId &&
                item.SessionId == sessionId &&
                item.Type == DraftSlotType.Shared);
        if (slot is null)
        {
            return NotFound<DeleteResponse>("Shared slot not found.");
        }

        if (slot.AssignedTeamId is not null)
        {
            return BadRequest<DeleteResponse>("Cannot delete an assigned shared slot.");
        }

        foreach (var slotPlayer in slot.Players)
        {
            slotPlayer.SessionPlayer.IsInsideSharedSlot = false;
            slotPlayer.SessionPlayer.IsCaptainEligible = true;
        }

        db.DraftSlotPlayers.RemoveRange(slot.Players);
        db.DraftSlots.Remove(slot);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<DeleteResponse>.Success(new DeleteResponse("Shared slot deleted."));
    }

    public async Task<ServiceResult<IReadOnlyList<TeamPreferenceGroupResponse>>> GetTeamPreferenceGroupsAsync(
        string adminUserId,
        string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
        {
            return NotFound<IReadOnlyList<TeamPreferenceGroupResponse>>("Không tìm thấy session.");
        }

        var groups = await db.TeamPreferenceGroups
            .Include(group => group.Players.OrderBy(player => player.RotationOrder))
            .ThenInclude(groupPlayer => groupPlayer.SessionPlayer)
            .Where(group => group.SessionId == sessionId)
            .OrderBy(group => group.CreatedAt)
            .ToListAsync();

        return ServiceResult<IReadOnlyList<TeamPreferenceGroupResponse>>.Success(
            groups.Select(ToTeamPreferenceGroupResponse).ToList());
    }

    public async Task<ServiceResult<TeamPreferenceGroupResponse>> CreateTeamPreferenceGroupAsync(
        string adminUserId,
        string sessionId,
        CreateTeamPreferenceGroupRequest request)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<TeamPreferenceGroupResponse>("Không tìm thấy session.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<TeamPreferenceGroupResponse>("Không thể tạo nhóm muốn chung team sau khi draft đã bắt đầu.");
        }

        var playerIds = request.SessionPlayerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();
        if (playerIds.Count < 2)
        {
            return BadRequest<TeamPreferenceGroupResponse>("Cần chọn ít nhất 2 người muốn chung team.");
        }

        if (playerIds.Count >= session.TeamSize)
        {
            return BadRequest<TeamPreferenceGroupResponse>("Nhóm muốn chung team phải nhỏ hơn số slot của một team.");
        }

        var players = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && playerIds.Contains(player.Id))
            .ToListAsync();

        if (players.Count != playerIds.Count)
        {
            return BadRequest<TeamPreferenceGroupResponse>("Một hoặc nhiều player không thuộc session này.");
        }

        if (players.Any(player => !player.IsPresent))
        {
            return BadRequest<TeamPreferenceGroupResponse>("Player phải có mặt.");
        }

        if (await db.TeamPreferenceGroupPlayers.AnyAsync(groupPlayer => playerIds.Contains(groupPlayer.SessionPlayerId)))
        {
            return BadRequest<TeamPreferenceGroupResponse>("Một player chỉ được nằm trong một nhóm muốn chung team.");
        }

        var group = new TeamPreferenceGroup
        {
            SessionId = sessionId
        };

        for (var index = 0; index < playerIds.Count; index += 1)
        {
            group.Players.Add(new TeamPreferenceGroupPlayer
            {
                TeamPreferenceGroupId = group.Id,
                SessionPlayerId = playerIds[index],
                SessionPlayer = players.Single(player => player.Id == playerIds[index]),
                RotationOrder = index + 1
            });
        }

        db.TeamPreferenceGroups.Add(group);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<TeamPreferenceGroupResponse>.Created(ToTeamPreferenceGroupResponse(group));
    }

    public async Task<ServiceResult<DeleteResponse>> DeleteTeamPreferenceGroupAsync(
        string adminUserId,
        string sessionId,
        string groupId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DeleteResponse>("Không tìm thấy session.");
        }

        if (session.Status is SessionStatus.Drafting or SessionStatus.Finished)
        {
            return BadRequest<DeleteResponse>("Không thể xóa nhóm muốn chung team sau khi draft đã bắt đầu.");
        }

        var group = await db.TeamPreferenceGroups
            .Include(item => item.Players)
            .SingleOrDefaultAsync(item => item.Id == groupId && item.SessionId == sessionId);
        if (group is null)
        {
            return NotFound<DeleteResponse>("Không tìm thấy nhóm muốn chung team.");
        }

        db.TeamPreferenceGroupPlayers.RemoveRange(group.Players);
        db.TeamPreferenceGroups.Remove(group);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return ServiceResult<DeleteResponse>.Success(new DeleteResponse("Đã xóa nhóm muốn chung team."));
    }

    public async Task<ServiceResult<CaptainsResponse>> AutoSelectCaptainsAsync(
        string adminUserId,
        string sessionId)
    {
        var eligiblePlayers = await GetCaptainEligiblePlayers(adminUserId, sessionId);
        if (eligiblePlayers is null)
        {
            return NotFound<CaptainsResponse>("Không tìm thấy session.");
        }

        if (eligiblePlayers.Count < 3)
        {
            return BadRequest<CaptainsResponse>("Cần ít nhất 3 người chơi hợp lệ để chọn đại diện.");
        }

        var captains = PickBalancedCaptains(eligiblePlayers);
        return await ApplyCaptainsAsync(adminUserId, sessionId, captains.Select(player => player.Id).ToList());
    }

    public async Task<ServiceResult<CaptainsResponse>> SetManualCaptainsAsync(
        string adminUserId,
        string sessionId,
        ManualCaptainsRequest request)
    {
        return await ApplyCaptainsAsync(adminUserId, sessionId, request.CaptainSessionPlayerIds.ToList());
    }

    public async Task<ServiceResult<CaptainsResponse>> GetCaptainsAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .ThenInclude(team => team.CaptainSessionPlayer)
            .SingleOrDefaultAsync();

        if (session is null)
        {
            return NotFound<CaptainsResponse>("Không tìm thấy session.");
        }

        return ServiceResult<CaptainsResponse>.Success(ToCaptainsResponse(session.Teams));
    }

    public async Task<ServiceResult<DraftStateResponse>> StartDraftAsync(
        string adminUserId,
        string sessionId)
    {
        return await StartDraftRunAsync(adminUserId, sessionId, allowRestart: false);
    }

    public async Task<ServiceResult<DraftStateResponse>> ResetDraftAsync(
        string adminUserId,
        string sessionId)
    {
        return await StartDraftRunAsync(adminUserId, sessionId, allowRestart: true);
    }

    public async Task<ServiceResult<DraftStateResponse>> AutoRunDraftAsync(
        string adminUserId,
        string sessionId,
        bool restart = false)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync();
        if (currentLease is null)
            return NotFound<DraftStateResponse>("Không tìm thấy session.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return ServiceResult<DraftStateResponse>.Failure(StatusCodes.Status409Conflict, "Đang có một thao tác draft khác chạy cho buổi này.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "AutoDraft")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(5)));
        if (claimed == 0)
            return ServiceResult<DraftStateResponse>.Failure(StatusCodes.Status409Conflict, "Đang có một thao tác draft khác chạy cho buổi này.");

        try
        {
            var session = await db.MatchSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            if (session.Status == SessionStatus.Cancelled)
                return BadRequest<DraftStateResponse>("Session đã bị huỷ.");
            var incompleteNames = await db.SessionPlayers.AsNoTracking()
                .Where(player => player.SessionId == sessionId && player.IsPresent &&
                                 (player.Gender == PlayerGender.Unknown ||
                                  (player.PlayerProfile != null &&
                                   (player.PlayerProfile.Gender == null ||
                                    player.PlayerProfile.Gender == PlayerGender.Unknown ||
                                    player.PlayerProfile.DefaultRole == null ||
                                    player.PlayerProfile.DefaultLevel == null))))
                .OrderBy(player => player.DisplayName)
                .Select(player => player.DisplayName)
                .Take(10)
                .ToListAsync();
            if (incompleteNames.Count > 0)
                return BadRequest<DraftStateResponse>(
                    $"Chưa thể draft vì chưa xác nhận hồ sơ: {string.Join(", ", incompleteNames)}. Cần cập nhật ít nhất giới tính; vị trí/trình độ có thể giữ Người mới/Mới.");

            if (restart)
            {
                if (session.Status is not (SessionStatus.Drafting or SessionStatus.Finished))
                    return BadRequest<DraftStateResponse>("Chỉ có thể draft lại khi draft đang chạy hoặc đã hoàn tất.");
                var reset = await ResetDraftAsync(adminUserId, sessionId);
                if (!reset.IsSuccess) return reset;
            }
            else if (session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection)
            {
                var captains = await GetCaptainsAsync(adminUserId, sessionId);
                if (!captains.IsSuccess || captains.Value is null || captains.Value.Captains.Count != session.TeamCount)
                {
                    var selected = await AutoSelectCaptainsAsync(adminUserId, sessionId);
                    if (!selected.IsSuccess)
                        return ServiceResult<DraftStateResponse>.Failure(selected.StatusCode, selected.Error!);
                }
                var started = await StartDraftAsync(adminUserId, sessionId);
                if (!started.IsSuccess) return started;
            }

            for (var pick = 0; pick < 200; pick += 1)
            {
                var state = await GetDraftStateAsync(adminUserId, sessionId);
                if (!state.IsSuccess || state.Value is null) return state;
                if (state.Value.SessionStatus == SessionStatus.Finished) return state;
                if (state.Value.SessionStatus != SessionStatus.Drafting)
                    return BadRequest<DraftStateResponse>("Session chưa sẵn sàng để tự draft.");
                var availableBags = state.Value.Bags.Where(bag => !bag.IsOpened).ToList();
                if (availableBags.Count == 0)
                    return ServiceResult<DraftStateResponse>.Failure(StatusCodes.Status409Conflict, "Không tìm thấy túi hợp lệ cho lượt draft hiện tại.");
                var bag = availableBags[Random.Shared.Next(availableBags.Count)];
                var opened = await OpenBagAsync(adminUserId, sessionId, bag.Id);
                if (!opened.IsSuccess)
                    return ServiceResult<DraftStateResponse>.Failure(opened.StatusCode, opened.Error!);
                // OpenBagAsync uses ExecuteUpdate for optimistic concurrency. Clear tracked
                // bag/slot entities so the next automatic pick cannot reuse stale IsOpened or
                // AssignedTeamId values from this DbContext.
                db.ChangeTracker.Clear();
            }
            return ServiceResult<DraftStateResponse>.Failure(StatusCodes.Status409Conflict, "Auto draft vượt quá giới hạn 200 lượt, đã dừng để bảo vệ dữ liệu.");
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

    public async Task<ServiceResult<SwapDraftPlayersResult>> SwapDraftPlayersAsync(
        string adminUserId,
        string sessionId,
        string firstPlayerReference,
        string secondPlayerReference)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync();
        if (currentLease is null)
            return NotFound<SwapDraftPlayersResult>("Không tìm thấy session.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return ServiceResult<SwapDraftPlayersResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "SwapDraftPlayers")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)));
        if (claimed == 0)
            return ServiceResult<SwapDraftPlayersResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");

        try
        {
            var session = await db.MatchSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            if (session.Status != SessionStatus.Finished)
                return BadRequest<SwapDraftPlayersResult>("Chỉ đổi vị trí thành viên sau khi draft đã hoàn tất.");

            var slots = await db.DraftSlots
                .Include(slot => slot.AssignedTeam)
                .Include(slot => slot.Players)
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => slot.SessionId == sessionId && slot.AssignedTeamId != null)
                .ToListAsync();
            var first = ResolveDraftPlayerSlot(slots, firstPlayerReference);
            if (first.Match is null)
                return BadRequest<SwapDraftPlayersResult>(first.Error!);
            var second = ResolveDraftPlayerSlot(slots, secondPlayerReference);
            if (second.Match is null)
                return BadRequest<SwapDraftPlayersResult>(second.Error!);

            if (first.Match.Slot.Id == second.Match.Slot.Id)
                return BadRequest<SwapDraftPlayersResult>("Hai người đang ở cùng một slot nên không cần đổi.");
            if (first.Match.Slot.AssignedTeamId == second.Match.Slot.AssignedTeamId)
                return BadRequest<SwapDraftPlayersResult>("Hai người đang ở cùng một team nên không cần đổi.");
            if (first.Match.Slot.IsCaptainSlot || second.Match.Slot.IsCaptainSlot)
                return BadRequest<SwapDraftPlayersResult>("Bot không tự đổi captain. Hãy draft lại hoặc chỉnh captain trên web.");
            if (first.Match.Slot.Type == DraftSlotType.Shared || second.Match.Slot.Type == DraftSlotType.Shared)
                return BadRequest<SwapDraftPlayersResult>("Một trong hai người thuộc slot ghép/thay phiên; bot không tách slot để tránh làm sai đội hình.");

            var firstTeamId = first.Match.Slot.AssignedTeamId!;
            var secondTeamId = second.Match.Slot.AssignedTeamId!;
            var firstTeamName = first.Match.Slot.AssignedTeam!.Name;
            var secondTeamName = second.Match.Slot.AssignedTeam!.Name;
            first.Match.Slot.AssignedTeamId = secondTeamId;
            second.Match.Slot.AssignedTeamId = firstTeamId;
            await db.SaveChangesAsync();
            await RecalculateTeamScore(firstTeamId);
            await RecalculateTeamScore(secondTeamId);

            var state = await GetDraftStateAsync(adminUserId, sessionId);
            if (!state.IsSuccess || state.Value is null)
                return ServiceResult<SwapDraftPlayersResult>.Failure(state.StatusCode, state.Error!);
            return ServiceResult<SwapDraftPlayersResult>.Success(new(
                first.Match.PlayerName,
                firstTeamName,
                second.Match.PlayerName,
                secondTeamName,
                state.Value));
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

    public async Task<ServiceResult<PostDraftSharedSlotResult>> SharePostDraftSlotAsync(
        string adminUserId,
        string sessionId,
        string anchorPlayerReference,
        string partnerReference)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync();
        if (currentLease is null) return NotFound<PostDraftSharedSlotResult>("Không tìm thấy session.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return ServiceResult<PostDraftSharedSlotResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "SharePostDraftSlot")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)));
        if (claimed == 0)
            return ServiceResult<PostDraftSharedSlotResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");

        try
        {
            var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId);
            if (session.Status != SessionStatus.Finished)
                return BadRequest<PostDraftSharedSlotResult>("Chỉ ghép share slot sau khi draft đã hoàn tất.");
            var slots = await db.DraftSlots
                .Include(slot => slot.AssignedTeam)
                .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => slot.SessionId == sessionId && slot.AssignedTeamId != null)
                .ToListAsync();
            var anchor = ResolveDraftPlayerSlot(slots, anchorPlayerReference);
            if (anchor.Match is null) return BadRequest<PostDraftSharedSlotResult>(anchor.Error!);

            var sessionPlayers = await db.SessionPlayers
                .Where(player => player.SessionId == sessionId && player.IsPresent)
                .ToListAsync();
            var normalizedPartnerReference = ZaloBotIntelligence.Normalize(partnerReference);
            var exactPartner = sessionPlayers
                .Where(player => ZaloBotIntelligence.Normalize(player.DisplayName) == normalizedPartnerReference)
                .ToList();
            var isExternalGuestLabel = normalizedPartnerReference.StartsWith("ban cua ", StringComparison.Ordinal) ||
                                       normalizedPartnerReference.StartsWith("ban share cung ", StringComparison.Ordinal);
            var partnerResolution = exactPartner.Count == 1
                ? new SessionPlayerResolution(exactPartner[0], null, false)
                : isExternalGuestLabel
                    ? new SessionPlayerResolution(null, $"Không tìm thấy '{partnerReference}' trong danh sách.", true)
                    : ResolveSessionPlayer(sessionPlayers, partnerReference);
            SessionPlayer partner;
            var partnerWasAdded = false;
            DraftSlot? previousPartnerSlot = null;
            if (partnerResolution.Player is null)
            {
                if (!partnerResolution.CanCreateNew)
                    return BadRequest<PostDraftSharedSlotResult>(partnerResolution.Error!);
                partner = new SessionPlayer
                {
                    SessionId = sessionId,
                    DisplayName = partnerReference.Trim(),
                    Gender = PlayerGender.Unknown,
                    Role = PlayerRole.New,
                    Level = PlayerLevel.New,
                    Score = CalculateScore(PlayerRole.New, PlayerLevel.New),
                    IsPresent = true,
                    IsCaptainEligible = false,
                    IsInsideSharedSlot = true
                };
                db.SessionPlayers.Add(partner);
                partnerWasAdded = true;
            }
            else
            {
                partner = partnerResolution.Player;
                if (partner.Id == anchor.Match.PlayerId)
                    return BadRequest<PostDraftSharedSlotResult>("Không thể share slot một người với chính họ.");
                previousPartnerSlot = slots.SingleOrDefault(slot => slot.Players.Any(link => link.SessionPlayerId == partner.Id));
                if (previousPartnerSlot is null)
                    return BadRequest<PostDraftSharedSlotResult>($"{partner.DisplayName} chưa có slot trong đội hình đã draft.");
                if (previousPartnerSlot.Id == anchor.Match.Slot.Id)
                    return ServiceResult<PostDraftSharedSlotResult>.Success(new(
                        anchor.Match.PlayerName,
                        partner.DisplayName,
                        anchor.Match.Slot.AssignedTeam!.Name,
                        false,
                        partner.Gender == PlayerGender.Unknown));
                if (previousPartnerSlot.IsCaptainSlot)
                    return BadRequest<PostDraftSharedSlotResult>("Không thể chuyển captain vào share slot khác.");
                if (previousPartnerSlot.Type == DraftSlotType.Shared || partner.IsInsideSharedSlot)
                    return BadRequest<PostDraftSharedSlotResult>($"{partner.DisplayName} đang thuộc một share slot khác.");
            }

            var anchorSlot = anchor.Match.Slot;
            var previousPartnerTeamId = previousPartnerSlot?.AssignedTeamId;
            if (previousPartnerSlot is not null)
            {
                var oldLinks = previousPartnerSlot.Players.Where(link => link.SessionPlayerId == partner.Id).ToList();
                db.DraftSlotPlayers.RemoveRange(oldLinks);
                previousPartnerSlot.AssignedTeamId = null;
            }

            var nextRotation = anchorSlot.Players.Count == 0 ? 1 : anchorSlot.Players.Max(link => link.RotationOrder) + 1;
            anchorSlot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = anchorSlot.Id,
                SessionPlayerId = partner.Id,
                SessionPlayer = partner,
                RotationOrder = nextRotation
            });
            anchorSlot.Type = DraftSlotType.Shared;
            partner.IsInsideSharedSlot = true;
            foreach (var link in anchorSlot.Players) link.SessionPlayer.IsInsideSharedSlot = true;
            var combinedPlayers = anchorSlot.Players.OrderBy(link => link.RotationOrder).Select(link => link.SessionPlayer).ToList();
            anchorSlot.DisplayName = string.Join(" / ", combinedPlayers.Select(player => player.DisplayName));
            anchorSlot.AverageScore = combinedPlayers.Average(player => player.Score);
            anchorSlot.Gender = CombinedGender(combinedPlayers);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await RecalculateTeamScore(anchorSlot.AssignedTeamId!);
            if (previousPartnerTeamId is not null && previousPartnerTeamId != anchorSlot.AssignedTeamId)
                await RecalculateTeamScore(previousPartnerTeamId);
            return ServiceResult<PostDraftSharedSlotResult>.Success(new(
                anchor.Match.PlayerName,
                partner.DisplayName,
                anchorSlot.AssignedTeam!.Name,
                partnerWasAdded,
                partner.Gender == PlayerGender.Unknown));
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

    public async Task<ServiceResult<PostDraftSlotTransferResult>> TransferPostDraftSlotAsync(
        string adminUserId,
        string sessionId,
        string fromPlayerReference,
        ShareSlotParticipantInput replacement)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync();
        if (currentLease is null) return NotFound<PostDraftSlotTransferResult>("Không tìm thấy session.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return Conflict<PostDraftSlotTransferResult>("Đang có thao tác khác cập nhật đội hình này.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "TransferPostDraftSlot")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)));
        if (claimed == 0)
            return Conflict<PostDraftSlotTransferResult>("Đang có thao tác khác cập nhật đội hình này.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId);
            if (session.Status != SessionStatus.Finished)
                return BadRequest<PostDraftSlotTransferResult>("Chỉ chuyển slot sau khi draft đã hoàn tất.");
            if (session.StartTime is not null && session.StartTime <= now)
                return BadRequest<PostDraftSlotTransferResult>("Trận đã bắt đầu hoặc đã qua giờ nên không thể chuyển slot.");

            var slots = await db.DraftSlots
                .Include(slot => slot.AssignedTeam)
                .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => slot.SessionId == sessionId && slot.AssignedTeamId != null)
                .ToListAsync();
            var source = ResolveDraftPlayerSlot(slots, fromPlayerReference);
            if (source.Match is null)
                return BadRequest<PostDraftSlotTransferResult>(source.Error!);
            if (source.Match.Slot.IsCaptainSlot)
                return BadRequest<PostDraftSlotTransferResult>("Không thể tự chuyển slot đội trưởng; hãy đổi đội trưởng trước.");

            var cleanReplacementName = replacement.DisplayName.Trim().TrimStart('@');
            if (cleanReplacementName.Length is < 2 or > 160)
                return BadRequest<PostDraftSlotTransferResult>("Tên người nhận slot không hợp lệ.");
            var replacementZaloId = string.IsNullOrWhiteSpace(replacement.ZaloUserId)
                ? null
                : NormalizeZaloId(replacement.ZaloUserId);
            var sessionPlayers = await db.SessionPlayers
                .Include(player => player.PlayerProfile)
                .Where(player => player.SessionId == sessionId)
                .ToListAsync();
            var target = replacementZaloId is null
                ? sessionPlayers.FirstOrDefault(player =>
                    ZaloBotIntelligence.Normalize(player.DisplayName) == ZaloBotIntelligence.Normalize(cleanReplacementName))
                : sessionPlayers.FirstOrDefault(player =>
                    NormalizeZaloId(player.PlayerProfile?.ZaloUserId) == replacementZaloId);
            target ??= sessionPlayers.FirstOrDefault(player =>
                ZaloBotIntelligence.Normalize(player.DisplayName) == ZaloBotIntelligence.Normalize(cleanReplacementName));
            if (target is not null && target.Id == source.Match.PlayerId)
                return BadRequest<PostDraftSlotTransferResult>("Người nhường và người nhận slot không thể là cùng một người.");

            var targetSlot = target is null
                ? null
                : slots.FirstOrDefault(slot => slot.Players.Any(link => link.SessionPlayerId == target.Id));
            if (targetSlot is not null)
                return BadRequest<PostDraftSlotTransferResult>($"{target!.DisplayName} đã có slot trong đội hình nên không thể nhận thêm slot.");

            var targetWasAdded = target is null;
            if (target is null)
            {
                PlayerProfile? profile = null;
                if (replacementZaloId is not null)
                {
                    profile = await db.PlayerProfiles.SingleOrDefaultAsync(item => item.ZaloUserId == replacementZaloId);
                    if (profile is null)
                    {
                        profile = new PlayerProfile
                        {
                            ZaloUserId = replacementZaloId,
                            DisplayName = cleanReplacementName,
                            AvatarUrl = replacement.AvatarUrl,
                            DefaultRole = PlayerRole.New,
                            DefaultLevel = PlayerLevel.New,
                            CreatedAt = now,
                            UpdatedAt = now,
                            LastSyncedAt = now
                        };
                        db.PlayerProfiles.Add(profile);
                    }
                    else
                    {
                        profile.DisplayName = cleanReplacementName;
                        profile.AvatarUrl ??= replacement.AvatarUrl;
                        profile.DefaultRole ??= PlayerRole.New;
                        profile.DefaultLevel ??= PlayerLevel.New;
                        profile.UpdatedAt = now;
                        profile.LastSyncedAt = now;
                    }
                }
                target = new SessionPlayer
                {
                    SessionId = sessionId,
                    PlayerProfile = profile,
                    PlayerProfileId = profile?.Id,
                    DisplayName = cleanReplacementName,
                    AvatarUrl = replacement.AvatarUrl ?? profile?.AvatarUrl,
                    Gender = profile?.Gender is PlayerGender.Male or PlayerGender.Female ? profile.Gender.Value : PlayerGender.Unknown,
                    Role = profile?.DefaultRole ?? PlayerRole.New,
                    Level = profile?.DefaultLevel ?? PlayerLevel.New,
                    Score = CalculateScore(profile?.DefaultRole ?? PlayerRole.New, profile?.DefaultLevel ?? PlayerLevel.New),
                    IsPresent = true,
                    IsCaptainEligible = false
                };
                db.SessionPlayers.Add(target);
            }

            var sourceSlot = source.Match.Slot;
            var sourceLink = sourceSlot.Players.Single(link => link.SessionPlayerId == source.Match!.PlayerId);
            sourceSlot.Players.Remove(sourceLink);
            db.DraftSlotPlayers.Remove(sourceLink);
            sourceLink.SessionPlayer.IsPresent = false;
            sourceLink.SessionPlayer.IsInsideSharedSlot = false;
            target.IsPresent = true;
            sourceSlot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = sourceSlot.Id,
                SessionPlayerId = target.Id,
                SessionPlayer = target,
                RotationOrder = sourceLink.RotationOrder
            });
            foreach (var link in sourceSlot.Players)
                link.SessionPlayer.IsInsideSharedSlot = sourceSlot.Players.Count > 1;
            RefreshDraftSlotSummary(sourceSlot);

            session.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (replacementZaloId is not null)
            {
                var waitlistEntry = await db.SessionWaitlistEntries.SingleOrDefaultAsync(item =>
                    item.SessionId == sessionId && item.ZaloUserId == replacementZaloId &&
                    (item.Status == SessionWaitlistStatus.Waiting || item.Status == SessionWaitlistStatus.Invited));
                if (waitlistEntry is not null)
                {
                    waitlistEntry.Status = SessionWaitlistStatus.Accepted;
                    waitlistEntry.SessionPlayerId = target.Id;
                    waitlistEntry.AcceptedAt = now;
                    waitlistEntry.InviteExpiresAt = null;
                    waitlistEntry.UpdatedAt = now;
                    waitlistEntry.Version += 1;
                }
            }
            await db.SaveChangesAsync();
            await RecalculateTeamScore(sourceSlot.AssignedTeamId!);
            await transaction.CommitAsync();

            return ServiceResult<PostDraftSlotTransferResult>.Success(new(
                source.Match.PlayerName,
                target.DisplayName,
                sourceSlot.AssignedTeam!.Name,
                targetWasAdded,
                target.Gender == PlayerGender.Unknown));
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

    public async Task<ServiceResult<PostDraftShareRepairResult>> RepairPostDraftSharedSlotAsync(
        string adminUserId,
        string sessionId,
        string wrongAnchorPlayerReference,
        string partnerReference,
        string correctAnchorPlayerReference)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var currentLease = await db.MatchSessions.AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.BotActionLeaseToken, session.BotActionLeaseUntil })
            .SingleOrDefaultAsync();
        if (currentLease is null) return NotFound<PostDraftShareRepairResult>("Không tìm thấy session.");
        if (currentLease.BotActionLeaseUntil is not null && currentLease.BotActionLeaseUntil >= now)
            return ServiceResult<PostDraftShareRepairResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");
        var claimed = await db.MatchSessions
            .Where(session => session.Id == sessionId &&
                              session.AdminUserId == adminUserId &&
                              session.BotActionLeaseToken == currentLease.BotActionLeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(session => session.BotActionLeaseToken, leaseToken)
                .SetProperty(session => session.BotActionLeaseName, "RepairPostDraftShareSlot")
                .SetProperty(session => session.BotActionLeaseUntil, now.AddMinutes(2)));
        if (claimed == 0)
            return ServiceResult<PostDraftShareRepairResult>.Failure(StatusCodes.Status409Conflict, "Đang có thao tác khác cập nhật đội hình này.");

        try
        {
            var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId);
            if (session.Status != SessionStatus.Finished)
                return BadRequest<PostDraftShareRepairResult>("Chỉ dùng sửa share slot này sau khi draft đã hoàn tất.");

            var slots = await db.DraftSlots
                .Include(slot => slot.AssignedTeam)
                .Include(slot => slot.Players.OrderBy(link => link.RotationOrder))
                .ThenInclude(link => link.SessionPlayer)
                .Where(slot => slot.SessionId == sessionId && slot.AssignedTeamId != null)
                .ToListAsync();
            var wrongAnchor = ResolveDraftPlayerSlot(slots, wrongAnchorPlayerReference);
            if (wrongAnchor.Match is null)
                return BadRequest<PostDraftShareRepairResult>(wrongAnchor.Error!);
            var correctAnchor = ResolveDraftPlayerSlot(slots, correctAnchorPlayerReference);
            if (correctAnchor.Match is null)
                return BadRequest<PostDraftShareRepairResult>(correctAnchor.Error!);
            if (wrongAnchor.Match.Slot.Id == correctAnchor.Match.Slot.Id)
                return BadRequest<PostDraftShareRepairResult>("Slot cũ và slot mới đang là cùng một slot.");

            var sourceSlot = wrongAnchor.Match.Slot;
            var targetSlot = correctAnchor.Match.Slot;
            if (sourceSlot.Type != DraftSlotType.Shared)
                return BadRequest<PostDraftShareRepairResult>($"{wrongAnchorPlayerReference} hiện không nằm trong shared slot.");
            if (targetSlot.Type == DraftSlotType.Shared)
                return BadRequest<PostDraftShareRepairResult>($"Slot của {correctAnchorPlayerReference} đã là shared slot; bot không tự ghép chồng thêm.");

            var partnerResolution = ResolveSessionPlayer(
                slots.SelectMany(slot => slot.Players.Select(link => link.SessionPlayer)).DistinctBy(player => player.Id).ToList(),
                partnerReference);
            if (partnerResolution.Player is null)
                return BadRequest<PostDraftShareRepairResult>(partnerResolution.Error!);
            var partner = partnerResolution.Player;
            var sourceLink = sourceSlot.Players.SingleOrDefault(link => link.SessionPlayerId == partner.Id);
            if (sourceLink is null)
                return BadRequest<PostDraftShareRepairResult>($"Không tìm thấy {partner.DisplayName} trong shared slot của {wrongAnchorPlayerReference}.");
            if (targetSlot.Players.Any(link => link.SessionPlayerId == partner.Id))
                return BadRequest<PostDraftShareRepairResult>($"{partner.DisplayName} đã nằm trong slot của {correctAnchorPlayerReference}.");

            var fromTeamName = sourceSlot.AssignedTeam!.Name;
            var toTeamName = targetSlot.AssignedTeam!.Name;
            sourceSlot.Players.Remove(sourceLink);
            db.DraftSlotPlayers.Remove(sourceLink);
            partner.IsInsideSharedSlot = false;
            RefreshDraftSlotSummary(sourceSlot);

            targetSlot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = targetSlot.Id,
                SessionPlayerId = partner.Id,
                SessionPlayer = partner,
                RotationOrder = targetSlot.Players.Count == 0 ? 1 : targetSlot.Players.Max(link => link.RotationOrder) + 1
            });
            targetSlot.Type = DraftSlotType.Shared;
            partner.IsInsideSharedSlot = true;
            RefreshDraftSlotSummary(targetSlot);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await RecalculateTeamScore(sourceSlot.AssignedTeamId!);
            await RecalculateTeamScore(targetSlot.AssignedTeamId!);

            return ServiceResult<PostDraftShareRepairResult>.Success(new(
                partner.DisplayName,
                wrongAnchor.Match.PlayerName,
                fromTeamName,
                correctAnchor.Match.PlayerName,
                toTeamName,
                targetSlot.DisplayName));
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

    private static void RefreshDraftSlotSummary(DraftSlot slot)
    {
        var players = slot.Players
            .OrderBy(link => link.RotationOrder)
            .Select(link => link.SessionPlayer)
            .ToList();
        if (players.Count == 0) return;
        slot.Type = players.Count > 1 ? DraftSlotType.Shared : DraftSlotType.Single;
        slot.DisplayName = string.Join(" / ", players.Select(player => player.DisplayName));
        slot.AverageScore = players.Average(player => player.Score);
        slot.Gender = CombinedGender(players);
    }

    private async Task<ServiceResult<DraftStateResponse>> StartDraftRunAsync(
        string adminUserId,
        string sessionId,
        bool allowRestart)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .ThenInclude(team => team.CaptainSessionPlayer)
            .SingleOrDefaultAsync();

        if (session is null)
        {
            return NotFound<DraftStateResponse>("Không tìm thấy session.");
        }

        if (session.Status == SessionStatus.Drafting && !allowRestart)
        {
            return BadRequest<DraftStateResponse>("Draft đang chạy.");
        }

        if (session.Teams.Count != session.TeamCount || session.Teams.Any(team => team.CaptainSessionPlayer is null))
        {
            return BadRequest<DraftStateResponse>("Cần chọn đủ 3 đại diện trước khi bắt đầu draft.");
        }

        var captainIds = session.Teams
            .Select(team => team.CaptainSessionPlayerId!)
            .ToHashSet();

        if (session.Teams.Any(team => !IsValidCaptain(team.CaptainSessionPlayer!)))
        {
            return BadRequest<DraftStateResponse>("Đại diện phải có mặt và hợp lệ.");
        }

        var teamIdByCaptainId = session.Teams.ToDictionary(
            team => team.CaptainSessionPlayerId!,
            team => team.Id);
        var preferenceError = await ValidateCaptainPreferenceGroupsAsync(sessionId, teamIdByCaptainId);
        if (preferenceError is not null)
        {
            return BadRequest<DraftStateResponse>(preferenceError);
        }
        var sharedSlotError = await ValidateCaptainSharedSlotsAsync(sessionId, teamIdByCaptainId);
        if (sharedSlotError is not null)
        {
            return BadRequest<DraftStateResponse>(sharedSlotError);
        }

        var players = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .ToListAsync();
        var sharedPlayerCount = await db.DraftSlotPlayers.CountAsync(link =>
            link.DraftSlot.SessionId == sessionId &&
            link.DraftSlot.Type == DraftSlotType.Shared &&
            link.SessionPlayer.IsPresent);
        var sharedSlotCount = await db.DraftSlots.CountAsync(slot =>
            slot.SessionId == sessionId &&
            slot.Type == DraftSlotType.Shared &&
            slot.Players.Any(link => link.SessionPlayer.IsPresent));
        var effectiveSlotCount = players.Count - sharedPlayerCount + sharedSlotCount;
        if (!IsValidRosterSize(effectiveSlotCount, session.TeamCount))
        {
            return BadRequest<DraftStateResponse>(
                $"Cần ít nhất {session.TeamCount * 2} slot và tổng số slot sau khi tính người share ({effectiveSlotCount}) phải chia hết cho {session.TeamCount} team.");
        }

        await ClearDraftRunArtifacts(sessionId);
        await EnsureCaptainSlots(session);
        await AttachCaptainSharedSlots(sessionId, teamIdByCaptainId);

        var singlePlayers = players
            .Where(player => !player.IsInsideSharedSlot && !captainIds.Contains(player.Id))
            .ToList();

        foreach (var player in singlePlayers)
        {
            var slot = new DraftSlot
            {
                SessionId = sessionId,
                Type = DraftSlotType.Single,
                DisplayName = player.DisplayName,
                Role = player.Role,
                Gender = player.Gender,
                AverageScore = player.Score
            };
            slot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = slot.Id,
                SessionPlayerId = player.Id,
                RotationOrder = 1
            });
            db.DraftSlots.Add(slot);
        }

        await db.SaveChangesAsync();

        var draftPool = await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
                !slot.IsCaptainSlot &&
                slot.AssignedTeamId == null)
            .ToListAsync();

        var totalDraftSlotCount = draftPool.Count + session.TeamCount;
        if (!IsValidRosterSize(totalDraftSlotCount, session.TeamCount))
        {
            return BadRequest<DraftStateResponse>(
                $"Sau khi tính slot thay phiên, tổng số slot ({totalDraftSlotCount}) phải chia hết cho {session.TeamCount}.");
        }

        session.TeamSize = totalDraftSlotCount / session.TeamCount;
        var expectedSlots = session.TeamCount * (session.TeamSize - 1);

        if (draftPool.Count != expectedSlots)
        {
            return BadRequest<DraftStateResponse>(
                $"Cần đúng {expectedSlots} slot còn lại để draft, hiện có {draftPool.Count} slot.");
        }

        var balancedRandomPool = BuildGenderBalancedRandomPool(draftPool, session.TeamCount);
        var rounds = balancedRandomPool
            .Chunk(session.TeamCount)
            .Select((slots, index) =>
            {
                var round = new DraftRound
                {
                    SessionId = sessionId,
                    RoundNumber = index + 1,
                    Label = RoundLabels.ElementAtOrDefault(index) ?? $"Vòng {index + 1}",
                    Status = index == 0 ? DraftRoundStatus.Active : DraftRoundStatus.Waiting
                };

                var shuffledSlots = Shuffle(slots).ToList();
                for (var bagIndex = 0; bagIndex < shuffledSlots.Count; bagIndex += 1)
                {
                    round.BlindBags.Add(new BlindBag
                    {
                        SessionId = sessionId,
                        RoundId = round.Id,
                        DraftSlotId = shuffledSlots[bagIndex].Id,
                        BagNumber = bagIndex + 1
                    });
                }

                return round;
            })
            .ToList();

        db.DraftRounds.AddRange(rounds);
        await db.SaveChangesAsync();

        var orderedTeams = session.Teams.OrderBy(team => team.Name).ToList();
        var turnOrder = 1;
        foreach (var round in rounds)
        {
            foreach (var team in orderedTeams)
            {
                db.DraftTurns.Add(new DraftTurn
                {
                    SessionId = sessionId,
                    RoundId = round.Id,
                    TeamId = team.Id,
                    CaptainSessionPlayerId = team.CaptainSessionPlayerId!,
                    TurnOrder = turnOrder,
                    Status = turnOrder == 1 ? DraftTurnStatus.Active : DraftTurnStatus.Waiting
                });
                turnOrder += 1;
            }
        }

        var firstTeam = orderedTeams[0];
        session.Status = SessionStatus.Drafting;
        session.CurrentRoundNumber = 1;
        session.CurrentTurnTeamId = firstTeam.Id;
        session.CurrentTurnCaptainSessionPlayerId = firstTeam.CaptainSessionPlayerId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return await GetDraftStateAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<DraftStateResponse>> UndoLastDraftPickAsync(
        string adminUserId,
        string sessionId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DraftStateResponse>("Không tìm thấy session.");
        }

        if (session.Status is not (SessionStatus.Drafting or SessionStatus.Finished))
        {
            return BadRequest<DraftStateResponse>("Chỉ có thể hoàn tác khi draft đang chạy hoặc vừa hoàn tất.");
        }

        var lastCompletedTurn = await db.DraftTurns
            .Include(turn => turn.Round)
            .Include(turn => turn.Team)
            .Include(turn => turn.CaptainSessionPlayer)
            .Include(turn => turn.OpenedBag)
            .Where(turn =>
                turn.SessionId == sessionId &&
                turn.Status == DraftTurnStatus.Completed &&
                turn.OpenedBagId != null)
            .OrderByDescending(turn => turn.CompletedAt)
            .ThenByDescending(turn => turn.TurnOrder)
            .FirstOrDefaultAsync();

        if (lastCompletedTurn is null || lastCompletedTurn.OpenedBag is null)
        {
            return BadRequest<DraftStateResponse>("Chưa có lượt bốc nào để hoàn tác.");
        }

        var openedBag = lastCompletedTurn.OpenedBag;
        var assignedSlotId = openedBag.PreparedDraftSlotId ?? openedBag.DraftSlotId;
        var now = DateTimeOffset.UtcNow;

        await db.DraftTurns
            .Where(turn =>
                turn.SessionId == sessionId &&
                turn.TurnOrder > lastCompletedTurn.TurnOrder &&
                turn.Status != DraftTurnStatus.Completed)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(turn => turn.Status, DraftTurnStatus.Waiting)
                .SetProperty(turn => turn.OpenedBagId, (string?)null)
                .SetProperty(turn => turn.CompletedAt, (DateTimeOffset?)null));

        var bagRows = await db.BlindBags
            .Where(bag => bag.Id == openedBag.Id && bag.IsOpened)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(bag => bag.IsOpened, false)
                .SetProperty(bag => bag.OpenedByUserId, (string?)null)
                .SetProperty(bag => bag.OpenedForTeamId, (string?)null)
                .SetProperty(bag => bag.OpenedAt, (DateTimeOffset?)null)
                .SetProperty(bag => bag.PreparedDraftSlotId, (string?)null));

        var slotRows = await db.DraftSlots
            .Where(slot => slot.Id == assignedSlotId && slot.AssignedTeamId == lastCompletedTurn.TeamId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(slot => slot.AssignedTeamId, (string?)null));

        var turnRows = await db.DraftTurns
            .Where(turn => turn.Id == lastCompletedTurn.Id && turn.Status == DraftTurnStatus.Completed)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(turn => turn.Status, DraftTurnStatus.Active)
                .SetProperty(turn => turn.OpenedBagId, (string?)null)
                .SetProperty(turn => turn.CompletedAt, (DateTimeOffset?)null));

        if (bagRows != 1 || slotRows != 1 || turnRows != 1)
        {
            await transaction.RollbackAsync();
            return Conflict<DraftStateResponse>("Không thể hoàn tác lượt bốc này. Vui lòng tải lại trạng thái draft.");
        }

        await db.DraftRounds
            .Where(round => round.SessionId == sessionId && round.RoundNumber < lastCompletedTurn.Round.RoundNumber)
            .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Completed));
        await db.DraftRounds
            .Where(round => round.Id == lastCompletedTurn.RoundId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Active));
        await db.DraftRounds
            .Where(round => round.SessionId == sessionId && round.RoundNumber > lastCompletedTurn.Round.RoundNumber)
            .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Waiting));

        session.Status = SessionStatus.Drafting;
        session.CurrentRoundNumber = lastCompletedTurn.Round.RoundNumber;
        session.CurrentTurnTeamId = lastCompletedTurn.TeamId;
        session.CurrentTurnCaptainSessionPlayerId = lastCompletedTurn.CaptainSessionPlayerId;
        session.UpdatedAt = now;

        await RecalculateTeamScore(lastCompletedTurn.TeamId);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return await GetDraftStateAsync(adminUserId, sessionId);
    }

    public async Task<ServiceResult<DraftStateResponse>> GetDraftStateAsync(
        string adminUserId,
        string sessionId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<DraftStateResponse>("Không tìm thấy session.");
        }

        var totalRounds = await db.DraftRounds.CountAsync(round => round.SessionId == sessionId);
        var activeTurn = await db.DraftTurns
            .Include(turn => turn.Team)
            .Include(turn => turn.Round)
            .Include(turn => turn.CaptainSessionPlayer)
            .Where(turn => turn.SessionId == sessionId && turn.Status == DraftTurnStatus.Active)
            .SingleOrDefaultAsync();

        var bags = activeTurn is null
            ? []
            : await db.BlindBags
                .Include(bag => bag.DraftSlot)
                .Where(bag => bag.RoundId == activeTurn.RoundId)
                .OrderBy(bag => bag.BagNumber)
                .Select(bag => new BlindBagStateResponse(
                    bag.Id,
                    $"Túi {bag.BagNumber}",
                    bag.IsOpened,
                    bag.IsOpened
                        ? new RevealedSlotResponse(
                            bag.DraftSlot.Id,
                            bag.DraftSlot.DisplayName,
                            bag.DraftSlot.Type,
                            bag.DraftSlot.Role,
                            bag.DraftSlot.Gender,
                            bag.DraftSlot.AverageScore)
                        : null))
                .ToListAsync();

        if (activeTurn is not null)
        {
            var preparedBagRows = await db.BlindBags
                .Include(bag => bag.DraftSlot)
                .Include(bag => bag.PreparedDraftSlot)
                .Where(bag => bag.RoundId == activeTurn.RoundId)
                .OrderBy(bag => bag.BagNumber)
                .ToListAsync();

            bags = preparedBagRows
                .Select(bag =>
                {
                    var revealedSlot = bag.PreparedDraftSlot ?? bag.DraftSlot;
                    return new BlindBagStateResponse(
                        bag.Id,
                        $"Túi {bag.BagNumber}",
                        bag.IsOpened,
                        bag.IsOpened ? ToRevealedSlot(revealedSlot) : null);
                })
                .ToList();
        }

        var teamPreview = await BuildTeamPreview(sessionId);
        var lastOpened = await db.BlindBags
            .Include(bag => bag.Round)
            .Include(bag => bag.DraftSlot)
            .Include(bag => bag.PreparedDraftSlot)
            .Include(bag => bag.OpenedForTeam)
            .Where(bag => bag.SessionId == sessionId && bag.IsOpened)
            .OrderByDescending(bag => bag.Round.RoundNumber)
            .ThenByDescending(bag => bag.BagNumber)
            .FirstOrDefaultAsync();

        var lastOpenedResponse = lastOpened is null || lastOpened.OpenedForTeam is null
            ? null
            : new OpenedBagResultResponse(
                $"{GetCaptainNameForTeam(teamPreview, lastOpened.OpenedForTeamId)} đã khui túi và bốc được {lastOpened.DraftSlot.DisplayName} cho {lastOpened.OpenedForTeam.Name}.",
                ToRevealedSlot(lastOpened.DraftSlot),
                new TeamSummary(lastOpened.OpenedForTeam.Id, lastOpened.OpenedForTeam.Name));

        if (lastOpened is not null && lastOpened.OpenedForTeam is not null)
        {
            var lastOpenedSlot = lastOpened.PreparedDraftSlot ?? lastOpened.DraftSlot;
            lastOpenedResponse = new OpenedBagResultResponse(
                $"{GetCaptainNameForTeam(teamPreview, lastOpened.OpenedForTeamId)} đã khui túi và bốc được {lastOpenedSlot.DisplayName} cho {lastOpened.OpenedForTeam.Name}.",
                ToRevealedSlot(lastOpenedSlot),
                new TeamSummary(lastOpened.OpenedForTeam.Id, lastOpened.OpenedForTeam.Name));
        }

        var canOpen = session.Status == SessionStatus.Drafting && activeTurn is not null;
        var message = activeTurn is null
            ? session.Status == SessionStatus.Finished
                ? "Draft đã hoàn tất."
                : "Chưa có lượt draft đang hoạt động."
            : $"Đưa điện thoại cho {activeTurn.CaptainSessionPlayer.DisplayName} khui túi cho {activeTurn.Team.Name}.";

        var response = new DraftStateResponse(
            session.Status,
            activeTurn?.Round.RoundNumber,
            totalRounds,
            activeTurn is null ? null : new TeamSummary(activeTurn.Team.Id, activeTurn.Team.Name),
            activeTurn is null ? null : new CaptainSummary(activeTurn.CaptainSessionPlayer.Id, activeTurn.CaptainSessionPlayer.DisplayName),
            new DraftViewerResponse(adminUserId, "Admin", canOpen, "OneDeviceAdmin"),
            bags,
            teamPreview,
            message,
            lastOpenedResponse);

        return ServiceResult<DraftStateResponse>.Success(response);
    }

    public async Task<ServiceResult<OpenBagResponse>> OpenBagAsync(
        string adminUserId,
        string sessionId,
        string bagId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<OpenBagResponse>("Không tìm thấy session.");
        }

        if (session.Status != SessionStatus.Drafting)
        {
            return BadRequest<OpenBagResponse>("Session chưa ở trạng thái Drafting.");
        }

        var activeTurns = await db.DraftTurns
            .Include(turn => turn.Team)
            .Include(turn => turn.Round)
            .Include(turn => turn.CaptainSessionPlayer)
            .Where(turn => turn.SessionId == sessionId && turn.Status == DraftTurnStatus.Active)
            .ToListAsync();

        if (activeTurns.Count != 1)
        {
            return ServiceResult<OpenBagResponse>.Failure(
                StatusCodes.Status409Conflict,
                "Trạng thái lượt bốc không hợp lệ. Vui lòng tải lại.");
        }

        var activeTurn = activeTurns[0];
        var bag = await db.BlindBags
            .Include(item => item.DraftSlot)
            .Include(item => item.PreparedDraftSlot)
            .SingleOrDefaultAsync(item => item.Id == bagId && item.SessionId == sessionId);

        if (bag is null)
        {
            return NotFound<OpenBagResponse>("Không tìm thấy túi.");
        }

        if (bag.RoundId != activeTurn.RoundId)
        {
            return BadRequest<OpenBagResponse>("Túi này không thuộc vòng bốc hiện tại.");
        }

        if (bag.IsOpened)
        {
            return Conflict<OpenBagResponse>("Túi đã được mở. Vui lòng tải lại trạng thái draft.");
        }

        if (false && bag.DraftSlot.AssignedTeamId is not null)
        {
            return Conflict<OpenBagResponse>("Slot trong túi đã được xếp vào team khác.");
        }

        var preparedSlot = bag.PreparedDraftSlot;
        if (preparedSlot is null || preparedSlot.AssignedTeamId is not null)
        {
            preparedSlot = await PrepareSlotForBagAsync(session, activeTurn.TeamId, bag);
        }

        if (preparedSlot is null)
        {
            return Conflict<OpenBagResponse>("Không còn slot hợp lệ để khui cho team hiện tại.");
        }

        var now = DateTimeOffset.UtcNow;
        var bagRows = await db.BlindBags
            .Where(item =>
                item.Id == bag.Id &&
                item.RoundId == activeTurn.RoundId &&
                !item.IsOpened)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.IsOpened, true)
                .SetProperty(item => item.OpenedByUserId, adminUserId)
                .SetProperty(item => item.OpenedForTeamId, activeTurn.TeamId)
                .SetProperty(item => item.OpenedAt, now));

        var slotRows = await db.DraftSlots
            .Where(slot => slot.Id == preparedSlot.Id && slot.AssignedTeamId == null)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(slot => slot.AssignedTeamId, activeTurn.TeamId));

        var turnRows = await db.DraftTurns
            .Where(turn => turn.Id == activeTurn.Id && turn.Status == DraftTurnStatus.Active)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(turn => turn.Status, DraftTurnStatus.Completed)
                .SetProperty(turn => turn.OpenedBagId, bag.Id)
                .SetProperty(turn => turn.CompletedAt, now));

        if (bagRows != 1 || slotRows != 1 || turnRows != 1)
        {
            await transaction.RollbackAsync();
            return Conflict<OpenBagResponse>("Túi đã được mở hoặc lượt bốc đã thay đổi. Vui lòng tải lại.");
        }

        await RecalculateTeamScore(activeTurn.TeamId);

        var nextTurn = await ActivateNextAvailableTurn(session, sessionId, now);

        session.UpdatedAt = now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        var assignedTeam = new TeamSummary(activeTurn.Team.Id, activeTurn.Team.Name);
        var revealedSlot = ToRevealedSlot(preparedSlot);
        var groupedNames = new List<string>();
        var groupedText = string.Empty;
        var response = new OpenBagResponse(
            $"{activeTurn.CaptainSessionPlayer.DisplayName} đã khui túi và bốc được {bag.DraftSlot.DisplayName} cho {activeTurn.Team.Name}.",
            revealedSlot,
            assignedTeam,
            nextTurn is null
                ? null
                : new NextTurnResponse(
                    nextTurn.Team.Name,
                    nextTurn.CaptainSessionPlayer.DisplayName,
                    nextTurn.Round.RoundNumber));

        response = response with
        {
            Message = $"{activeTurn.CaptainSessionPlayer.DisplayName} đã khui túi và bốc được {preparedSlot.DisplayName} cho {activeTurn.Team.Name}."
        };

        if (groupedNames.Count > 0)
        {
            response = response with { Message = $"{response.Message}{groupedText}" };
        }

        return ServiceResult<OpenBagResponse>.Success(response);
    }

    public async Task<ServiceResult<PrepareRevealResponse>> PrepareBagRevealAsync(
        string adminUserId,
        string sessionId,
        string bagId)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId).SingleOrDefaultAsync();
        if (session is null)
        {
            return NotFound<PrepareRevealResponse>("Không tìm thấy session.");
        }

        if (session.Status != SessionStatus.Drafting)
        {
            return BadRequest<PrepareRevealResponse>("Session chưa ở trạng thái Drafting.");
        }

        var activeTurns = await db.DraftTurns
            .Include(turn => turn.Team)
            .Include(turn => turn.Round)
            .Include(turn => turn.CaptainSessionPlayer)
            .Where(turn => turn.SessionId == sessionId && turn.Status == DraftTurnStatus.Active)
            .ToListAsync();

        if (activeTurns.Count != 1)
        {
            return ServiceResult<PrepareRevealResponse>.Failure(
                StatusCodes.Status409Conflict,
                "Trạng thái lượt bốc không hợp lệ. Vui lòng tải lại.");
        }

        var activeTurn = activeTurns[0];
        var bag = await db.BlindBags
            .Include(item => item.DraftSlot)
            .Include(item => item.PreparedDraftSlot)
            .SingleOrDefaultAsync(item => item.Id == bagId && item.SessionId == sessionId);

        if (bag is null)
        {
            return NotFound<PrepareRevealResponse>("Không tìm thấy túi.");
        }

        if (bag.RoundId != activeTurn.RoundId)
        {
            return BadRequest<PrepareRevealResponse>("Túi này không thuộc vòng bốc hiện tại.");
        }

        if (bag.IsOpened)
        {
            return Conflict<PrepareRevealResponse>("Túi đã được mở. Vui lòng tải lại trạng thái draft.");
        }

        if (false && bag.DraftSlot.AssignedTeamId is not null)
        {
            return Conflict<PrepareRevealResponse>("Slot trong túi đã được xếp vào team khác.");
        }

        var preparedSlot = bag.PreparedDraftSlot;
        if (preparedSlot is null || preparedSlot.AssignedTeamId is not null)
        {
            preparedSlot = await PrepareSlotForBagAsync(session, activeTurn.TeamId, bag);
        }

        if (preparedSlot is null)
        {
            return Conflict<PrepareRevealResponse>("Không còn slot hợp lệ để khui cho team hiện tại.");
        }

        var response = new PrepareRevealResponse(
            ToRevealedSlot(preparedSlot),
            new TeamSummary(activeTurn.Team.Id, activeTurn.Team.Name),
            new CaptainSummary(activeTurn.CaptainSessionPlayer.Id, activeTurn.CaptainSessionPlayer.DisplayName));

        return ServiceResult<PrepareRevealResponse>.Success(response);
    }

    private async Task<ServiceResult<CaptainsResponse>> ApplyCaptainsAsync(
        string adminUserId,
        string sessionId,
        IReadOnlyList<string> captainIds)
    {
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .SingleOrDefaultAsync();

        if (session is null)
        {
            return NotFound<CaptainsResponse>("Không tìm thấy session.");
        }

        if (session.Status == SessionStatus.Drafting)
        {
            return BadRequest<CaptainsResponse>("Không thể đổi đại diện khi draft đang chạy.");
        }

        var ids = captainIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count != session.TeamCount || captainIds.Count != session.TeamCount)
        {
            return BadRequest<CaptainsResponse>("Cần chọn đúng 3 đại diện và không được trùng.");
        }

        var players = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && ids.Contains(player.Id))
            .ToListAsync();

        if (players.Count != session.TeamCount)
        {
            return BadRequest<CaptainsResponse>("Một hoặc nhiều đại diện không thuộc session này.");
        }

        if (players.Any(player => !IsValidCaptain(player)))
        {
            return BadRequest<CaptainsResponse>("Đại diện phải có mặt và hợp lệ.");
        }

        var orderedTeams = session.Teams.OrderBy(team => team.Name).ToList();
        if (orderedTeams.Count == 0)
        {
            foreach (var teamName in TeamNames)
            {
                var team = new Team { SessionId = sessionId, Name = teamName };
                session.Teams.Add(team);
                orderedTeams.Add(team);
            }
        }

        var teamIdByCaptainId = ids
            .Select((id, index) => new { CaptainId = id, TeamId = orderedTeams[index].Id })
            .ToDictionary(item => item.CaptainId, item => item.TeamId);
        var preferenceError = await ValidateCaptainPreferenceGroupsAsync(sessionId, teamIdByCaptainId);
        if (preferenceError is not null)
        {
            return BadRequest<CaptainsResponse>(preferenceError);
        }
        var sharedSlotError = await ValidateCaptainSharedSlotsAsync(sessionId, teamIdByCaptainId);
        if (sharedSlotError is not null)
        {
            return BadRequest<CaptainsResponse>(sharedSlotError);
        }

        await ClearDraftRunArtifacts(sessionId);
        await RemoveCaptainSlots(sessionId);

        for (var index = 0; index < ids.Count; index += 1)
        {
            var team = orderedTeams[index];
            var captain = players.Single(player => player.Id == ids[index]);
            team.CaptainSessionPlayerId = captain.Id;
            var captainSlot = new DraftSlot
            {
                SessionId = sessionId,
                Type = DraftSlotType.Single,
                DisplayName = captain.DisplayName,
                Role = captain.Role,
                Gender = captain.Gender,
                AverageScore = captain.Score,
                AssignedTeamId = team.Id,
                IsCaptainSlot = true
            };
            captainSlot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = captainSlot.Id,
                SessionPlayerId = captain.Id,
                RotationOrder = 1
            });
            db.DraftSlots.Add(captainSlot);
        }

        session.Status = SessionStatus.CaptainSelection;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await RecalculateAllTeamScores(sessionId);

        var savedTeams = await db.Teams
            .Include(team => team.CaptainSessionPlayer)
            .Where(team => team.SessionId == sessionId)
            .OrderBy(team => team.Name)
            .ToListAsync();

        return ServiceResult<CaptainsResponse>.Success(ToCaptainsResponse(savedTeams));
    }

    private IQueryable<MatchSession> LoadSessionForAdmin(string adminUserId, string sessionId)
    {
        return db.MatchSessions.Where(session => session.Id == sessionId && session.AdminUserId == adminUserId);
    }

    private async Task<string?> GetSessionAdminUserId(string sessionId)
    {
        return await db.MatchSessions
            .Where(session => session.Id == sessionId)
            .Select(session => session.AdminUserId)
            .SingleOrDefaultAsync();
    }

    private async Task<bool> IsSessionAdmin(string adminUserId, string sessionId)
    {
        return await db.MatchSessions.AnyAsync(session => session.Id == sessionId && session.AdminUserId == adminUserId);
    }

    private async Task<List<SessionPlayer>?> GetCaptainEligiblePlayers(string adminUserId, string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
        {
            return null;
        }

        return await db.SessionPlayers
            .Where(player =>
                player.SessionId == sessionId &&
                player.IsPresent &&
                (player.IsCaptainEligible || player.IsInsideSharedSlot))
            .ToListAsync();
    }

    private async Task<string?> ValidateCaptainPreferenceGroupsAsync(
        string sessionId,
        IReadOnlyDictionary<string, string> teamIdByCaptainId)
    {
        var groups = await db.TeamPreferenceGroups
            .Include(group => group.Players)
            .Where(group => group.SessionId == sessionId)
            .ToListAsync();

        foreach (var group in groups)
        {
            var captainTeamIds = group.Players
                .Select(groupPlayer => groupPlayer.SessionPlayerId)
                .Where(teamIdByCaptainId.ContainsKey)
                .Select(playerId => teamIdByCaptainId[playerId])
                .Distinct()
                .ToList();

            if (captainTeamIds.Count > 1)
            {
                return "Một nhóm muốn chung team đang có nhiều captain ở các team khác nhau. Hãy chỉ để tối đa một captain trong nhóm đó.";
            }
        }

        return null;
    }

    private async Task<string?> ValidateCaptainSharedSlotsAsync(
        string sessionId,
        IReadOnlyDictionary<string, string> teamIdByCaptainId)
    {
        var captainIds = teamIdByCaptainId.Keys.ToHashSet();
        var sharedSlots = await db.DraftSlots
            .Include(slot => slot.Players)
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                slot.Players.Any(slotPlayer => captainIds.Contains(slotPlayer.SessionPlayerId)))
            .ToListAsync();

        foreach (var slot in sharedSlots)
        {
            var captainTeamIds = slot.Players
                .Select(slotPlayer => slotPlayer.SessionPlayerId)
                .Where(teamIdByCaptainId.ContainsKey)
                .Select(playerId => teamIdByCaptainId[playerId])
                .Distinct()
                .ToList();

            if (captainTeamIds.Count > 1)
            {
                return "Một slot thay phiên đang có nhiều captain ở các team khác nhau. Hãy chỉ để tối đa một captain trong slot đó.";
            }
        }

        return null;
    }

    private async Task AttachCaptainSharedSlots(
        string sessionId,
        IReadOnlyDictionary<string, string> teamIdByCaptainId)
    {
        var captainIds = teamIdByCaptainId.Keys.ToHashSet();
        var sharedSlots = await db.DraftSlots
            .Include(slot => slot.Players)
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                !slot.IsCaptainSlot &&
                slot.Players.Any(slotPlayer => captainIds.Contains(slotPlayer.SessionPlayerId)))
            .ToListAsync();

        foreach (var slot in sharedSlots)
        {
            var captainId = slot.Players
                .Select(slotPlayer => slotPlayer.SessionPlayerId)
                .First(captainIds.Contains);
            slot.AssignedTeamId = teamIdByCaptainId[captainId];
            slot.IsCaptainSlot = true;

            await RemoveSingleCaptainSlotForPlayer(sessionId, captainId);
        }

        await db.SaveChangesAsync();
    }

    private async Task ClearDraftRunArtifacts(string sessionId)
    {
        var turns = db.DraftTurns.Where(turn => turn.SessionId == sessionId);
        var bags = db.BlindBags.Where(bag => bag.SessionId == sessionId);
        var rounds = db.DraftRounds.Where(round => round.SessionId == sessionId);
        var generatedSingleSlots = db.DraftSlots.Where(slot =>
            slot.SessionId == sessionId &&
            slot.Type == DraftSlotType.Single &&
            !slot.IsCaptainSlot);

        db.DraftTurns.RemoveRange(turns);
        db.BlindBags.RemoveRange(bags);
        db.DraftRounds.RemoveRange(rounds);
        db.DraftSlots.RemoveRange(generatedSingleSlots);
        await db.SaveChangesAsync();

        await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(slot => slot.AssignedTeamId, (string?)null)
                .SetProperty(slot => slot.IsCaptainSlot, false));
    }

    private async Task RemoveCaptainSlots(string sessionId)
    {
        await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Shared &&
                slot.IsCaptainSlot)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(slot => slot.AssignedTeamId, (string?)null)
                .SetProperty(slot => slot.IsCaptainSlot, false));

        var captainSlots = db.DraftSlots.Where(slot =>
            slot.SessionId == sessionId &&
            slot.Type == DraftSlotType.Single &&
            slot.IsCaptainSlot);
        db.DraftSlots.RemoveRange(captainSlots);
        await db.SaveChangesAsync();
    }

    private async Task RemoveCaptainSlotForPlayer(string sessionId, string playerId)
    {
        var captainSlotIds = await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Single &&
                slot.IsCaptainSlot &&
                slot.Players.Any(slotPlayer => slotPlayer.SessionPlayerId == playerId))
            .Select(slot => slot.Id)
            .ToListAsync();

        if (captainSlotIds.Count == 0)
        {
            return;
        }

        await db.DraftSlotPlayers
            .Where(slotPlayer => captainSlotIds.Contains(slotPlayer.DraftSlotId))
            .ExecuteDeleteAsync();
        await db.DraftSlots
            .Where(slot => captainSlotIds.Contains(slot.Id))
            .ExecuteDeleteAsync();
    }

    private async Task RemoveSingleCaptainSlotForPlayer(string sessionId, string playerId)
    {
        var captainSlotIds = await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
                slot.Type == DraftSlotType.Single &&
                slot.IsCaptainSlot &&
                slot.Players.Any(slotPlayer => slotPlayer.SessionPlayerId == playerId))
            .Select(slot => slot.Id)
            .ToListAsync();

        if (captainSlotIds.Count == 0)
        {
            return;
        }

        await db.DraftSlotPlayers
            .Where(slotPlayer => captainSlotIds.Contains(slotPlayer.DraftSlotId))
            .ExecuteDeleteAsync();
        await db.DraftSlots
            .Where(slot => captainSlotIds.Contains(slot.Id))
            .ExecuteDeleteAsync();
    }

    private async Task EnsureCaptainSlots(MatchSession session)
    {
        var existingCaptainSlotCount = await db.DraftSlots.CountAsync(slot =>
            slot.SessionId == session.Id &&
            slot.IsCaptainSlot);

        if (existingCaptainSlotCount == session.TeamCount)
        {
            return;
        }

        await RemoveCaptainSlots(session.Id);
        foreach (var team in session.Teams)
        {
            var captain = team.CaptainSessionPlayer!;
            var slot = new DraftSlot
            {
                SessionId = session.Id,
                Type = DraftSlotType.Single,
                DisplayName = captain.DisplayName,
                Role = captain.Role,
                Gender = captain.Gender,
                AverageScore = captain.Score,
                AssignedTeamId = team.Id,
                IsCaptainSlot = true
            };
            slot.Players.Add(new DraftSlotPlayer
            {
                DraftSlotId = slot.Id,
                SessionPlayerId = captain.Id,
                RotationOrder = 1
            });
            db.DraftSlots.Add(slot);
        }

        await db.SaveChangesAsync();
    }

    private async Task RecalculateAllTeamScores(string sessionId)
    {
        var teamIds = await db.Teams
            .Where(team => team.SessionId == sessionId)
            .Select(team => team.Id)
            .ToListAsync();

        foreach (var teamId in teamIds)
        {
            await RecalculateTeamScore(teamId);
        }
    }

    private async Task RecalculateTeamScore(string teamId)
    {
        var total = await db.DraftSlots
            .Where(slot => slot.AssignedTeamId == teamId)
            .SumAsync(slot => slot.AverageScore);

        await db.Teams
            .Where(team => team.Id == teamId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(team => team.TotalAverageScore, total));
    }

    private static DraftPlayerSlotResolution ResolveDraftPlayerSlot(
        IReadOnlyList<DraftSlot> slots,
        string playerReference)
    {
        var reference = ZaloBotIntelligence.Normalize(playerReference);
        var all = slots
            .SelectMany(slot => slot.Players.Select(link => new DraftPlayerSlotMatch(
                slot,
                link.SessionPlayer.Id,
                link.SessionPlayer.DisplayName,
                ZaloBotIntelligence.Normalize(link.SessionPlayer.DisplayName))))
            .ToList();
        var exact = all.Where(item => item.NormalizedPlayerName == reference).ToList();
        if (exact.Count == 1) return new(exact[0], null);
        var partial = all.Where(item =>
                reference.Contains(item.NormalizedPlayerName, StringComparison.Ordinal) ||
                item.NormalizedPlayerName.Contains(reference, StringComparison.Ordinal))
            .ToList();
        if (partial.Count == 1) return new(partial[0], null);
        var possible = (exact.Count > 1 ? exact : partial)
            .Select(item => item.PlayerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        return possible.Count > 0
            ? new(null, $"Tên '{playerReference}' chưa đủ rõ. Các tên phù hợp: {string.Join(", ", possible)}.")
            : new(null, $"Không tìm thấy '{playerReference}' trong đội hình đã draft.");
    }

    private static SessionPlayerResolution ResolveSessionPlayer(
        IReadOnlyList<SessionPlayer> players,
        string playerReference)
    {
        var reference = ZaloBotIntelligence.Normalize(playerReference);
        var exact = players.Where(player => ZaloBotIntelligence.Normalize(player.DisplayName) == reference).ToList();
        if (exact.Count == 1) return new(exact[0], null, false);
        var partial = players.Where(player =>
                reference.Contains(ZaloBotIntelligence.Normalize(player.DisplayName), StringComparison.Ordinal) ||
                ZaloBotIntelligence.Normalize(player.DisplayName).Contains(reference, StringComparison.Ordinal))
            .ToList();
        if (partial.Count == 1) return new(partial[0], null, false);
        var possible = (exact.Count > 1 ? exact : partial)
            .Select(player => player.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        return possible.Count > 0
            ? new(null, $"Tên '{playerReference}' chưa đủ rõ. Các tên phù hợp: {string.Join(", ", possible)}.", false)
            : new(null, $"Không tìm thấy '{playerReference}' trong danh sách.", true);
    }

    private static PlayerGender CombinedGender(IReadOnlyList<SessionPlayer> players) =>
        players.Any(player => player.Gender == PlayerGender.Female)
            ? PlayerGender.Female
            : players.Any(player => player.Gender == PlayerGender.Unknown)
                ? PlayerGender.Unknown
                : PlayerGender.Male;

    private async Task<DraftSlot?> PrepareSlotForBagAsync(
        MatchSession session,
        string teamId,
        BlindBag bag)
    {
        var slot = await PickPreparedSlotForTeamAsync(session, teamId);
        if (slot is null)
        {
            return null;
        }

        bag.PreparedDraftSlotId = slot.Id;
        bag.PreparedDraftSlot = slot;
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<DraftSlot?> PickPreparedSlotForTeamAsync(MatchSession session, string teamId)
    {
        var assignedSlotCount = await db.DraftSlots.CountAsync(slot => slot.AssignedTeamId == teamId);
        var remainingCapacity = session.TeamSize - assignedSlotCount;
        if (remainingCapacity <= 0)
        {
            return null;
        }

        var allSlots = await db.DraftSlots
            .Include(slot => slot.Players)
            .Where(slot => slot.SessionId == session.Id)
            .ToListAsync();
        var unassignedSlots = allSlots
            .Where(slot => !slot.IsCaptainSlot && slot.AssignedTeamId is null)
            .ToList();

        if (unassignedSlots.Count == 0)
        {
            return null;
        }

        var groupPlayers = await db.TeamPreferenceGroupPlayers
            .Join(
                db.TeamPreferenceGroups.Where(group => group.SessionId == session.Id),
                groupPlayer => groupPlayer.TeamPreferenceGroupId,
                group => group.Id,
                (groupPlayer, _) => new
                {
                    groupPlayer.TeamPreferenceGroupId,
                    groupPlayer.SessionPlayerId
                })
            .ToListAsync();

        if (groupPlayers.Count == 0)
        {
            return await PickBalancedSlotForTeamAsync(session, teamId, unassignedSlots);
        }

        var groupIdByPlayerId = groupPlayers.ToDictionary(
            item => item.SessionPlayerId,
            item => item.TeamPreferenceGroupId);
        var playerIdsByGroupId = groupPlayers
            .GroupBy(item => item.TeamPreferenceGroupId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.SessionPlayerId).ToHashSet());

        HashSet<string> GetGroupIdsForSlot(DraftSlot slot)
        {
            return slot.Players
                .Select(slotPlayer => slotPlayer.SessionPlayerId)
                .Where(groupIdByPlayerId.ContainsKey)
                .Select(playerId => groupIdByPlayerId[playerId])
                .ToHashSet();
        }

        List<DraftSlot> GetSlotsForGroup(string groupId)
        {
            var playerIds = playerIdsByGroupId[groupId];
            return allSlots
                .Where(slot => slot.Players.Any(slotPlayer => playerIds.Contains(slotPlayer.SessionPlayerId)))
                .ToList();
        }

        var groupTeamAssignments = playerIdsByGroupId.Keys.ToDictionary(
            groupId => groupId,
            groupId => GetSlotsForGroup(groupId)
                .Where(slot => slot.AssignedTeamId is not null)
                .Select(slot => slot.AssignedTeamId!)
                .Distinct()
                .ToList());

        var groupsReservedForCurrentTeam = groupTeamAssignments
            .Where(item => item.Value.Contains(teamId))
            .Select(item => item.Key)
            .ToHashSet();

        var requiredGroupSlots = unassignedSlots
            .Where(slot => GetGroupIdsForSlot(slot).Any(groupsReservedForCurrentTeam.Contains))
            .ToList();
        if (requiredGroupSlots.Count > 0)
        {
            return await PickBalancedSlotForTeamAsync(session, teamId, requiredGroupSlots);
        }

        var validSlots = unassignedSlots
            .Where(slot =>
            {
                var slotGroupIds = GetGroupIdsForSlot(slot);
                if (slotGroupIds.Count == 0)
                {
                    return true;
                }

                foreach (var groupId in slotGroupIds)
                {
                    var assignedTeams = groupTeamAssignments[groupId];
                    if (assignedTeams.Any(assignedTeamId => assignedTeamId != teamId))
                    {
                        return false;
                    }

                    if (assignedTeams.Count == 0)
                    {
                        var unassignedGroupSlotCount = GetSlotsForGroup(groupId)
                            .Count(groupSlot => groupSlot.AssignedTeamId is null);
                        if (unassignedGroupSlotCount > remainingCapacity)
                        {
                            return false;
                        }
                    }
                }

                return true;
            })
            .ToList();

        return validSlots.Count == 0
            ? null
            : await PickBalancedSlotForTeamAsync(session, teamId, validSlots);
    }

    private async Task<TeamPreferenceAssignmentResult> ApplyTeamPreferenceGroupAsync(
        MatchSession session,
        string openedDraftSlotId,
        string teamId,
        string adminUserId,
        DateTimeOffset now)
    {
        var openedPlayerIds = await db.DraftSlotPlayers
            .Where(slotPlayer => slotPlayer.DraftSlotId == openedDraftSlotId)
            .Select(slotPlayer => slotPlayer.SessionPlayerId)
            .ToListAsync();

        if (openedPlayerIds.Count == 0)
        {
            return TeamPreferenceAssignmentResult.Success([]);
        }

        var groupIds = await db.TeamPreferenceGroupPlayers
            .Where(groupPlayer => openedPlayerIds.Contains(groupPlayer.SessionPlayerId))
            .Select(groupPlayer => groupPlayer.TeamPreferenceGroupId)
            .Distinct()
            .ToListAsync();

        if (groupIds.Count == 0)
        {
            return TeamPreferenceAssignmentResult.Success([]);
        }

        var groupedPlayerIds = await db.TeamPreferenceGroupPlayers
            .Where(groupPlayer => groupIds.Contains(groupPlayer.TeamPreferenceGroupId))
            .Select(groupPlayer => groupPlayer.SessionPlayerId)
            .Distinct()
            .ToListAsync();

        var linkedSlots = await db.DraftSlots
            .Include(slot => slot.Players)
            .Where(slot =>
                slot.SessionId == session.Id &&
                slot.Id != openedDraftSlotId &&
                !slot.IsCaptainSlot &&
                slot.AssignedTeamId == null &&
                slot.Players.Any(slotPlayer => groupedPlayerIds.Contains(slotPlayer.SessionPlayerId)))
            .ToListAsync();

        if (linkedSlots.Count == 0)
        {
            return TeamPreferenceAssignmentResult.Success([]);
        }

        var assignedSlotCount = await db.DraftSlots.CountAsync(slot => slot.AssignedTeamId == teamId);
        var openCapacity = session.TeamSize - assignedSlotCount;
        if (linkedSlots.Count > openCapacity)
        {
            return TeamPreferenceAssignmentResult.Failure("Team này không còn đủ slot để kéo cả nhóm muốn chung team.");
        }

        foreach (var slot in linkedSlots)
        {
            slot.AssignedTeamId = teamId;
        }

        var linkedSlotIds = linkedSlots.Select(slot => slot.Id).ToList();
        await db.BlindBags
            .Where(bag => linkedSlotIds.Contains(bag.DraftSlotId) && !bag.IsOpened)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(bag => bag.IsOpened, true)
                .SetProperty(bag => bag.OpenedByUserId, adminUserId)
                .SetProperty(bag => bag.OpenedForTeamId, teamId)
                .SetProperty(bag => bag.OpenedAt, now));

        await db.SaveChangesAsync();
        return TeamPreferenceAssignmentResult.Success(linkedSlots);
    }

    private async Task<DraftTurn?> ActivateNextAvailableTurn(
        MatchSession session,
        string sessionId,
        DateTimeOffset now)
    {
        while (true)
        {
            var nextTurn = await db.DraftTurns
                .Include(turn => turn.Team)
                .Include(turn => turn.Round)
                .Include(turn => turn.CaptainSessionPlayer)
                .Where(turn => turn.SessionId == sessionId && turn.Status == DraftTurnStatus.Waiting)
                .OrderBy(turn => turn.TurnOrder)
                .FirstOrDefaultAsync();

            if (nextTurn is null)
            {
                await FinishDraft(session, sessionId);
                return null;
            }

            var teamSlotCount = await db.DraftSlots.CountAsync(slot => slot.AssignedTeamId == nextTurn.TeamId);
            var roundHasUnopenedBags = await db.BlindBags.AnyAsync(bag =>
                bag.RoundId == nextTurn.RoundId &&
                !bag.IsOpened);

            if (teamSlotCount >= session.TeamSize || !roundHasUnopenedBags)
            {
                await db.DraftTurns
                    .Where(turn => turn.Id == nextTurn.Id && turn.Status == DraftTurnStatus.Waiting)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(turn => turn.Status, DraftTurnStatus.Skipped)
                        .SetProperty(turn => turn.CompletedAt, now));
                continue;
            }

            await db.DraftTurns
                .Where(turn => turn.Id == nextTurn.Id && turn.Status == DraftTurnStatus.Waiting)
                .ExecuteUpdateAsync(updates => updates.SetProperty(turn => turn.Status, DraftTurnStatus.Active));
            await db.DraftRounds
                .Where(round => round.SessionId == sessionId && round.RoundNumber < nextTurn.Round.RoundNumber)
                .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Completed));
            await db.DraftRounds
                .Where(round => round.Id == nextTurn.RoundId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Active));

            session.CurrentRoundNumber = nextTurn.Round.RoundNumber;
            session.CurrentTurnTeamId = nextTurn.TeamId;
            session.CurrentTurnCaptainSessionPlayerId = nextTurn.CaptainSessionPlayerId;
            return nextTurn;
        }
    }

    private async Task FinishDraft(MatchSession session, string sessionId)
    {
        await db.DraftRounds
            .Where(round => round.SessionId == sessionId && round.Status != DraftRoundStatus.Completed)
            .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Completed));
        session.Status = SessionStatus.Finished;
        session.CurrentRoundNumber = null;
        session.CurrentTurnTeamId = null;
        session.CurrentTurnCaptainSessionPlayerId = null;
    }

    private async Task<IReadOnlyList<TeamPreviewResponse>> BuildTeamPreview(string sessionId)
    {
        var teams = await db.Teams
            .Include(team => team.CaptainSessionPlayer)
            .Where(team => team.SessionId == sessionId)
            .OrderBy(team => team.Name)
            .ToListAsync();

        var teamIds = teams.Select(team => team.Id).ToList();
        var slots = await db.DraftSlots
            .Where(slot => slot.AssignedTeamId != null && teamIds.Contains(slot.AssignedTeamId))
            .OrderByDescending(slot => slot.IsCaptainSlot)
            .ThenBy(slot => slot.DisplayName)
            .ToListAsync();

        return teams
            .Select(team => new TeamPreviewResponse(
                team.Id,
                team.Name,
                team.CaptainSessionPlayer?.DisplayName,
                slots
                    .Where(slot => slot.AssignedTeamId == team.Id)
                    .Select(slot => new TeamSlotPreviewResponse(
                        slot.Id,
                        slot.DisplayName,
                        slot.Type,
                        slot.Gender,
                        slot.IsCaptainSlot,
                        slot.AverageScore))
                    .ToList()))
            .ToList();
    }

    private static CaptainsResponse ToCaptainsResponse(IEnumerable<Team> teams)
    {
        var captainTeams = teams
            .Where(team => team.CaptainSessionPlayer is not null)
            .OrderBy(team => team.Name)
            .Select(team => new CaptainTeamResponse(
                team.Id,
                team.Name,
                team.CaptainSessionPlayerId!,
                team.CaptainSessionPlayer!.DisplayName,
                team.CaptainSessionPlayer.Score))
            .ToList();

        return new CaptainsResponse(captainTeams, EvaluateCaptainBalance(captainTeams.Select(team => team.Score)));
    }

    private static CaptainBalanceResponse EvaluateCaptainBalance(IEnumerable<double> scores)
    {
        var scoreList = scores.ToList();
        if (scoreList.Count < 3)
        {
            return new CaptainBalanceResponse(0, "Missing", "Cần chọn đủ 3 đại diện.");
        }

        var difference = scoreList.Max() - scoreList.Min();
        return difference switch
        {
            <= 0.5 => new CaptainBalanceResponse(difference, "Balanced", null),
            <= 1 => new CaptainBalanceResponse(difference, "SlightlyUnbalanced", "Đại diện hơi lệch nhẹ nhưng vẫn có thể tiếp tục."),
            _ => new CaptainBalanceResponse(difference, "StronglyUnbalanced", "3 đại diện đang lệch trình. Admin nên chọn lại.")
        };
    }

    private static List<SessionPlayer> PickBalancedCaptains(IReadOnlyList<SessionPlayer> players)
    {
        var combos = players
            .SelectMany((first, firstIndex) => players
                .Skip(firstIndex + 1)
                .SelectMany((second, secondIndex) => players
                    .Skip(firstIndex + secondIndex + 2)
                    .Select(third => new[] { first, second, third }.ToList())))
            .ToList();

        var minSpread = combos.Min(GetScoreSpread);
        var acceptableSpread = minSpread switch
        {
            <= 0.5 => 0.5,
            <= 1 => 1,
            _ => minSpread
        };
        var eligibleFemaleCount = players.Count(player => player.Gender == PlayerGender.Female);
        var targetFemaleCaptains = Math.Min(3, eligibleFemaleCount);
        var candidates = combos
            .Where(group => GetScoreSpread(group) <= acceptableSpread)
            .OrderBy(group => Math.Abs(group.Count(player => player.Gender == PlayerGender.Female) - targetFemaleCaptains))
            .ThenBy(_ => Guid.NewGuid())
            .Take(Math.Min(12, combos.Count))
            .ToList();

        var best = candidates[Random.Shared.Next(candidates.Count)];

        return Shuffle(best).ToList();
    }

    private static IReadOnlyList<DraftSlot> BuildGenderBalancedRandomPool(
        IReadOnlyList<DraftSlot> draftPool,
        int teamCount)
    {
        var femaleSlots = Shuffle(draftPool.Where(slot => slot.Gender == PlayerGender.Female)).ToList();
        var otherSlots = Shuffle(draftPool.Where(slot => slot.Gender != PlayerGender.Female)).ToList();
        var result = new List<DraftSlot>(draftPool.Count);

        while (femaleSlots.Count >= teamCount)
        {
            result.AddRange(TakeSlots(femaleSlots, teamCount));
        }

        if (femaleSlots.Count > 0)
        {
            var mixedRound = TakeSlots(femaleSlots, femaleSlots.Count);
            mixedRound.AddRange(TakeSlots(otherSlots, teamCount - mixedRound.Count));
            result.AddRange(Shuffle(mixedRound));
        }

        while (otherSlots.Count > 0)
        {
            result.AddRange(Shuffle(TakeSlots(otherSlots, Math.Min(teamCount, otherSlots.Count))));
        }

        return result;
    }

    private static List<T> TakeSlots<T>(List<T> source, int count)
    {
        var taken = source.Take(count).ToList();
        source.RemoveRange(0, taken.Count);
        return taken;
    }

    private static IEnumerable<T> Shuffle<T>(IEnumerable<T> items)
    {
        return items.OrderBy(_ => Random.Shared.Next());
    }

    private static double GetScoreSpread(IEnumerable<SessionPlayer> players)
    {
        var scores = players.Select(player => player.Score).ToList();
        return scores.Max() - scores.Min();
    }

    private async Task<DraftSlot?> PickBalancedSlotForTeamAsync(
        MatchSession session,
        string teamId,
        IReadOnlyList<DraftSlot> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var sessionSlots = await db.DraftSlots
            .AsNoTracking()
            .Where(slot => slot.SessionId == session.Id)
            .ToListAsync();
        var assignedSlots = sessionSlots
            .Where(slot => slot.AssignedTeamId is not null)
            .ToList();
        var currentTeamSlots = assignedSlots
            .Where(slot => slot.AssignedTeamId == teamId)
            .ToList();

        var targetTeamScore = sessionSlots.Count == 0
            ? 0
            : sessionSlots.Sum(slot => slot.AverageScore) / session.TeamCount;
        var targetFemaleSlots = sessionSlots.Count(slot => slot.Gender == PlayerGender.Female) / (double)session.TeamCount;
        var currentTeamScore = currentTeamSlots.Sum(slot => slot.AverageScore);
        var currentFemaleSlots = currentTeamSlots.Count(slot => slot.Gender == PlayerGender.Female);

        var scoredCandidates = candidates
            .Select(slot =>
            {
                var scoreDistance = Math.Abs(currentTeamScore + slot.AverageScore - targetTeamScore);
                var femaleDistance = Math.Abs(
                    currentFemaleSlots + (slot.Gender == PlayerGender.Female ? 1 : 0) - targetFemaleSlots);
                var randomNoise = Random.Shared.NextDouble() * 0.12;

                return new
                {
                    Slot = slot,
                    BalanceScore = scoreDistance + femaleDistance * 0.9 + randomNoise
                };
            })
            .OrderBy(candidate => candidate.BalanceScore)
            .ToList();

        var topCount = Math.Min(
            scoredCandidates.Count,
            Math.Max(1, Math.Min(5, (int)Math.Ceiling(scoredCandidates.Count / 3.0))));

        return Shuffle(scoredCandidates.Take(topCount))
            .Select(candidate => candidate.Slot)
            .First();
    }

    private static bool IsValidCaptain(SessionPlayer player)
    {
        return player.IsPresent && (player.IsCaptainEligible || player.IsInsideSharedSlot);
    }

    private static bool IsValidRosterSize(int slotCount, int teamCount)
    {
        return teamCount > 0 && slotCount >= teamCount * 2 && slotCount % teamCount == 0;
    }

    private static string NormalizeZaloId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static int GetNextValidRosterSize(int playerCount, int teamCount)
    {
        var minimum = teamCount * 2;
        var candidate = Math.Max(minimum, playerCount);
        var remainder = candidate % teamCount;
        return remainder == 0 ? candidate : candidate + teamCount - remainder;
    }

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

    private static SessionResponse ToSessionResponse(MatchSession session)
    {
        return new SessionResponse(
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
    }

    private static AdminSessionSummaryResponse ToAdminSessionSummaryResponse(
        MatchSession session,
        int playerCount)
    {
        return new AdminSessionSummaryResponse(
            session.Id,
            session.Name,
            session.Status,
            session.TeamCount,
            session.TeamSize,
            session.TotalSets,
            playerCount,
            GetNextValidRosterSize(playerCount, session.TeamCount),
            session.CreatedAt,
            session.UpdatedAt);
    }

    private static PublicSessionSummaryResponse ToPublicSessionSummaryResponse(
        MatchSession session,
        int playerCount)
    {
        return new PublicSessionSummaryResponse(
            session.Id,
            session.Name,
            session.Status,
            session.TeamCount,
            session.TeamSize,
            session.TotalSets,
            playerCount,
            GetNextValidRosterSize(playerCount, session.TeamCount),
            session.CreatedAt,
            session.UpdatedAt);
    }

    private static SessionPlayerResponse ToPlayerResponse(SessionPlayer player)
    {
        return new SessionPlayerResponse(
            player.Id,
            player.DisplayName,
            player.UserId,
            player.PlayerProfileId,
            player.PlayerProfile?.ZaloUserId,
            player.AvatarUrl,
            player.Role,
            player.Level,
            player.Gender,
            player.Score,
            player.IsPresent,
            player.IsCaptainEligible,
            player.IsInsideSharedSlot);
    }

    private static SharedSlotResponse ToSharedSlotResponse(DraftSlot slot)
    {
        var orderedPlayers = slot.Players
            .OrderBy(player => player.RotationOrder)
            .Select(player => player.SessionPlayer)
            .ToList();

        return new SharedSlotResponse(
            slot.Id,
            slot.DisplayName,
            slot.Role,
            slot.Gender,
            slot.AverageScore,
            orderedPlayers.Select(player => player.Id).ToList(),
            orderedPlayers.Select(player => player.DisplayName).ToList());
    }

    private static TeamPreferenceGroupResponse ToTeamPreferenceGroupResponse(TeamPreferenceGroup group)
    {
        var orderedPlayers = group.Players
            .OrderBy(player => player.RotationOrder)
            .Select(player => player.SessionPlayer)
            .ToList();

        return new TeamPreferenceGroupResponse(
            group.Id,
            orderedPlayers.Select(player => player.Id).ToList(),
            orderedPlayers.Select(player => player.DisplayName).ToList(),
            orderedPlayers.Count == 0 ? 0 : orderedPlayers.Average(player => player.Score));
    }

    private static RevealedSlotResponse ToRevealedSlot(DraftSlot slot)
    {
        return new RevealedSlotResponse(
            slot.Id,
            slot.DisplayName,
            slot.Type,
            slot.Role,
            slot.Gender,
            slot.AverageScore);
    }

    private static string GetCaptainNameForTeam(
        IReadOnlyList<TeamPreviewResponse> teams,
        string? teamId)
    {
        return teams.FirstOrDefault(team => team.TeamId == teamId)?.CaptainName ?? "Đại diện";
    }

    private sealed record TeamPreferenceAssignmentResult(
        bool IsSuccess,
        string? ErrorMessage,
        IReadOnlyList<DraftSlot> AssignedSlots)
    {
        public static TeamPreferenceAssignmentResult Success(IReadOnlyList<DraftSlot> assignedSlots)
        {
            return new TeamPreferenceAssignmentResult(true, null, assignedSlots);
        }

        public static TeamPreferenceAssignmentResult Failure(string message)
        {
            return new TeamPreferenceAssignmentResult(false, message, []);
        }
    }

    private sealed record DraftPlayerSlotMatch(
        DraftSlot Slot,
        string PlayerId,
        string PlayerName,
        string NormalizedPlayerName);

    private sealed record DraftPlayerSlotResolution(
        DraftPlayerSlotMatch? Match,
        string? Error);

    private sealed record SessionPlayerResolution(
        SessionPlayer? Player,
        string? Error,
        bool CanCreateNew);

    private static ServiceResult<T> BadRequest<T>(string message)
    {
        return ServiceResult<T>.Failure(StatusCodes.Status400BadRequest, message);
    }

    private static ServiceResult<T> NotFound<T>(string message)
    {
        return ServiceResult<T>.Failure(StatusCodes.Status404NotFound, message);
    }

    private static ServiceResult<T> Conflict<T>(string message)
    {
        return ServiceResult<T>.Failure(StatusCodes.Status409Conflict, message);
    }
}

public sealed record SwapDraftPlayersResult(
    string FirstPlayerName,
    string FirstPreviousTeamName,
    string SecondPlayerName,
    string SecondPreviousTeamName,
    DraftStateResponse State);

public sealed record IncompletePlayerProfile(
    string SessionPlayerId,
    string DisplayName,
    PlayerGender Gender,
    PlayerRole Role,
    PlayerLevel Level,
    bool MissingGender,
    bool MissingRole,
    bool MissingLevel);

public sealed record GuestPlayerAddResult(
    SessionPlayerResponse Player,
    int PresentPlayerCount,
    int TeamCount);

public sealed record ShareSlotParticipantInput(
    string DisplayName,
    string? ZaloUserId = null,
    string? AvatarUrl = null);

public sealed record PreDraftSharedSlotResult(
    string AnchorPlayerName,
    IReadOnlyList<string> PartnerPlayerNames,
    IReadOnlyList<string> NewlyAddedPlayerNames,
    string SlotDisplayName,
    int PresentPlayerCount,
    int EffectiveSlotCount,
    IReadOnlyList<string> NeedsProfileUpdateNames);

public sealed record PostDraftSharedSlotResult(
    string AnchorPlayerName,
    string PartnerPlayerName,
    string TeamName,
    bool PartnerWasAdded,
    bool NeedsProfileUpdate);

public sealed record PostDraftSlotTransferResult(
    string FromPlayerName,
    string ToPlayerName,
    string TeamName,
    bool ToPlayerWasAdded,
    bool NeedsProfileUpdate);

public sealed record PostDraftShareRepairResult(
    string PartnerPlayerName,
    string FromAnchorPlayerName,
    string FromTeamName,
    string ToAnchorPlayerName,
    string ToTeamName,
    string NewSlotDisplayName);
