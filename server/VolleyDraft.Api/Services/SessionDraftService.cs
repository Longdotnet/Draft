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

        if (request.TeamCount != 3 || request.TeamSize != 6)
        {
            return ServiceResult<SessionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "MVP hiện hỗ trợ đúng 3 team x 6 slot.");
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

    public async Task<ServiceResult<IReadOnlyList<SessionResponse>>> GetSessionsAsync(string adminUserId)
    {
        var sessions = await db.MatchSessions
            .Include(session => session.Teams)
            .Where(session => session.AdminUserId == adminUserId)
            .ToListAsync();

        return ServiceResult<IReadOnlyList<SessionResponse>>.Success(
            sessions
                .OrderByDescending(session => session.UpdatedAt)
                .Select(ToSessionResponse)
                .ToList());
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

        await db.DraftTurns.Where(turn => turn.SessionId == sessionId).ExecuteDeleteAsync();
        await db.BlindBags.Where(bag => bag.SessionId == sessionId).ExecuteDeleteAsync();
        await db.DraftRounds.Where(round => round.SessionId == sessionId).ExecuteDeleteAsync();
        await db.DraftSlotPlayers
            .Where(slotPlayer => slotIds.Contains(slotPlayer.DraftSlotId))
            .ExecuteDeleteAsync();
        await db.DraftSlots.Where(slot => slot.SessionId == sessionId).ExecuteDeleteAsync();
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

    public async Task<ServiceResult<IReadOnlyList<SessionPlayerResponse>>> GetPlayersAsync(
        string adminUserId,
        string sessionId)
    {
        if (!await IsSessionAdmin(adminUserId, sessionId))
        {
            return NotFound<IReadOnlyList<SessionPlayerResponse>>("Không tìm thấy session.");
        }

        var players = await db.SessionPlayers
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

        var captainIds = await db.Teams
            .Where(team => team.SessionId == sessionId && team.CaptainSessionPlayerId != null)
            .Select(team => team.CaptainSessionPlayerId!)
            .ToListAsync();

        if (players.Any(player => captainIds.Contains(player.Id)))
        {
            return BadRequest<SharedSlotResponse>("Đại diện đã chọn không thể đưa vào slot thay phiên.");
        }

        var slot = new DraftSlot
        {
            SessionId = sessionId,
            Type = DraftSlotType.Shared,
            DisplayName = string.Join(" / ", players.Select(player => player.DisplayName)),
            Role = request.Role,
            Gender = players.Any(player => player.Gender == PlayerGender.Female)
                ? PlayerGender.Female
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
            players[index].IsCaptainEligible = false;
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
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await LoadSessionForAdmin(adminUserId, sessionId)
            .Include(item => item.Teams)
            .ThenInclude(team => team.CaptainSessionPlayer)
            .SingleOrDefaultAsync();

        if (session is null)
        {
            return NotFound<DraftStateResponse>("Không tìm thấy session.");
        }

        if (session.Status == SessionStatus.Drafting)
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
            return BadRequest<DraftStateResponse>("Đại diện phải có mặt, hợp lệ và không nằm trong slot thay phiên.");
        }

        await ClearDraftRunArtifacts(sessionId);
        await EnsureCaptainSlots(session);

        var existingSharedSlots = await db.DraftSlots
            .Where(slot => slot.SessionId == sessionId && slot.Type == DraftSlotType.Shared)
            .ToListAsync();
        var players = await db.SessionPlayers
            .Where(player => player.SessionId == sessionId && player.IsPresent)
            .ToListAsync();
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

        var teamPreview = await BuildTeamPreview(sessionId);
        var lastOpened = await db.BlindBags
            .Include(bag => bag.Round)
            .Include(bag => bag.DraftSlot)
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

        if (bag.DraftSlot.AssignedTeamId is not null)
        {
            return Conflict<OpenBagResponse>("Slot trong túi đã được xếp vào team khác.");
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
            .Where(slot => slot.Id == bag.DraftSlotId && slot.AssignedTeamId == null)
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

        var nextTurn = await db.DraftTurns
            .Include(turn => turn.Team)
            .Include(turn => turn.Round)
            .Include(turn => turn.CaptainSessionPlayer)
            .Where(turn => turn.SessionId == sessionId && turn.Status == DraftTurnStatus.Waiting)
            .OrderBy(turn => turn.TurnOrder)
            .FirstOrDefaultAsync();

        if (nextTurn is null)
        {
            await db.DraftRounds
                .Where(round => round.SessionId == sessionId && round.Status != DraftRoundStatus.Completed)
                .ExecuteUpdateAsync(updates => updates.SetProperty(round => round.Status, DraftRoundStatus.Completed));
            session.Status = SessionStatus.Finished;
            session.CurrentRoundNumber = null;
            session.CurrentTurnTeamId = null;
            session.CurrentTurnCaptainSessionPlayerId = null;
        }
        else
        {
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
        }

        session.UpdatedAt = now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        var assignedTeam = new TeamSummary(activeTurn.Team.Id, activeTurn.Team.Name);
        var revealedSlot = ToRevealedSlot(bag.DraftSlot);
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
            return NotFound<PrepareRevealResponse>("Khﾃｴng tﾃｬm th蘯･y session.");
        }

        if (session.Status != SessionStatus.Drafting)
        {
            return BadRequest<PrepareRevealResponse>("Session chﾆｰa 盻・tr蘯｡ng thﾃ｡i Drafting.");
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
                "Tr蘯｡ng thﾃ｡i lﾆｰ盻｣t b盻祖 khﾃｴng h盻｣p l盻・ Vui lﾃｲng t蘯｣i l蘯｡i.");
        }

        var activeTurn = activeTurns[0];
        var bag = await db.BlindBags
            .Include(item => item.DraftSlot)
            .SingleOrDefaultAsync(item => item.Id == bagId && item.SessionId == sessionId);

        if (bag is null)
        {
            return NotFound<PrepareRevealResponse>("Khﾃｴng tﾃｬm th蘯･y tﾃｺi.");
        }

        if (bag.RoundId != activeTurn.RoundId)
        {
            return BadRequest<PrepareRevealResponse>("Tﾃｺi nﾃy khﾃｴng thu盻冂 vﾃｲng b盻祖 hi盻㌻ t蘯｡i.");
        }

        if (bag.IsOpened)
        {
            return Conflict<PrepareRevealResponse>("Tﾃｺi ﾄ妥｣ ﾄ柁ｰ盻｣c m盻・ Vui lﾃｲng t蘯｣i l蘯｡i tr蘯｡ng thﾃ｡i draft.");
        }

        if (bag.DraftSlot.AssignedTeamId is not null)
        {
            return Conflict<PrepareRevealResponse>("Slot trong tﾃｺi ﾄ妥｣ ﾄ柁ｰ盻｣c x蘯ｿp vﾃo team khﾃ｡c.");
        }

        var response = new PrepareRevealResponse(
            ToRevealedSlot(bag.DraftSlot),
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
            return BadRequest<CaptainsResponse>("Đại diện phải có mặt, hợp lệ và không nằm trong slot thay phiên.");
        }

        await ClearDraftRunArtifacts(sessionId);
        await RemoveCaptainSlots(sessionId);

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
                player.IsCaptainEligible &&
                !player.IsInsideSharedSlot)
            .ToListAsync();
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
    }

    private async Task RemoveCaptainSlots(string sessionId)
    {
        var captainSlots = db.DraftSlots.Where(slot => slot.SessionId == sessionId && slot.IsCaptainSlot);
        db.DraftSlots.RemoveRange(captainSlots);
        await db.SaveChangesAsync();
    }

    private async Task RemoveCaptainSlotForPlayer(string sessionId, string playerId)
    {
        var captainSlotIds = await db.DraftSlots
            .Where(slot =>
                slot.SessionId == sessionId &&
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

    private static bool IsValidCaptain(SessionPlayer player)
    {
        return player.IsPresent && player.IsCaptainEligible && !player.IsInsideSharedSlot;
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
            session.Teams.OrderBy(team => team.Name).Select(team => new TeamSummary(team.Id, team.Name)).ToList());
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
            session.TeamCount * session.TeamSize,
            session.CreatedAt,
            session.UpdatedAt);
    }

    private static SessionPlayerResponse ToPlayerResponse(SessionPlayer player)
    {
        return new SessionPlayerResponse(
            player.Id,
            player.DisplayName,
            player.UserId,
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
