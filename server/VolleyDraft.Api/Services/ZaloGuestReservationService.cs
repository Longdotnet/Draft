using System.Data;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloGuestSignupMutationResult(
    string SessionId,
    string SessionName,
    int BeforeEffectiveSlots,
    int AfterEffectiveSlots,
    int Capacity,
    IReadOnlyList<ZaloGuestReservation> Added,
    IReadOnlyList<ZaloGuestReservation> Waitlisted,
    bool Idempotent = false);

internal sealed record ZaloGuestUpdateResult(
    string SessionId,
    string SessionName,
    IReadOnlyList<ZaloGuestReservation> Changed,
    bool NeedsClarification = false,
    string? Clarification = null);

internal sealed record ZaloGuestPromotion(
    string SessionId,
    string SessionName,
    string SponsorZaloUserId,
    string SponsorDisplayName,
    string DisplayName,
    int EffectiveSlots,
    int Capacity);

internal sealed class ZaloGuestReservationService(VolleyDraftDbContext db)
{
    public async Task<ZaloGuestSignupMutationResult> AddAsync(
        MatchSession session,
        string sponsorZaloUserId,
        string sponsorDisplayName,
        string sourceMessageId,
        string? recruitmentMessageId,
        ZaloRecruitmentGuestCommand command,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var existing = await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item => item.SessionId == session.Id && item.SourceMessageId == sourceMessageId)
            .OrderBy(item => item.GuestIndex)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            var readiness = await new ZaloDraftReadinessService(db).BuildAsync(session.Id, cancellationToken: cancellationToken);
            var active = existing.Where(item => item.Status == ZaloGuestReservationStatus.Active).ToList();
            var waiting = existing.Where(item => item.Status == ZaloGuestReservationStatus.Waitlisted).ToList();
            return new ZaloGuestSignupMutationResult(
                session.Id,
                session.Name,
                Math.Max(0, (readiness?.EffectiveSlotCount ?? 0) - active.Count),
                readiness?.EffectiveSlotCount ?? 0,
                readiness?.Capacity ?? session.TeamCount * session.TeamSize,
                active,
                waiting,
                Idempotent: true);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var liveSession = await db.MatchSessions
            .SingleOrDefaultAsync(item => item.Id == session.Id, cancellationToken)
            ?? throw new InvalidOperationException("Session disappeared while reserving guest slots.");
        if (liveSession.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
            throw new InvalidOperationException("Kèo không còn nhận thêm guest ở trạng thái hiện tại.");
        if (liveSession.StartTime is { } start && start <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Kèo đã tới giờ nên không nhận thêm guest nữa.");

        var readinessBefore = await new ZaloDraftReadinessService(db)
            .BuildAsync(liveSession.Id, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Không đọc được roster hiện tại.");
        var capacity = readinessBefore.Capacity;
        var available = Math.Max(0, capacity - readinessBefore.EffectiveSlotCount);
        var specs = command.Guests ?? [];
        var nextSequence = (await db.ZaloGuestReservations
            .Where(item => item.SessionId == liveSession.Id && item.SponsorZaloUserId == sponsorZaloUserId)
            .Select(item => (int?)item.SponsorSequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var occupiedNames = (await db.SessionPlayers
                .AsNoTracking()
                .Where(item => item.SessionId == liveSession.Id)
                .Select(item => item.DisplayName)
                .ToListAsync(cancellationToken))
            .Concat(await db.ZaloGuestReservations
                .AsNoTracking()
                .Where(item => item.SessionId == liveSession.Id && item.Status != ZaloGuestReservationStatus.Cancelled)
                .Select(item => item.DisplayName)
                .ToListAsync(cancellationToken))
            .Select(ZaloBotIntelligence.Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var added = new List<ZaloGuestReservation>();
        var waitlisted = new List<ZaloGuestReservation>();
        var draftService = new SessionDraftService(db);
        for (var index = 0; index < command.Quantity; index += 1)
        {
            var spec = index < specs.Count ? specs[index] : new ZaloRecruitmentGuestSpec();
            var sequence = nextSequence + index;
            var requestedName = string.IsNullOrWhiteSpace(spec.DisplayName)
                ? $"Bạn của {sponsorDisplayName} #{sequence}"
                : spec.DisplayName!.Trim();
            var displayName = MakeUniqueName(requestedName, sponsorDisplayName, occupiedNames);
            occupiedNames.Add(ZaloBotIntelligence.Normalize(displayName));

            var reservation = new ZaloGuestReservation
            {
                SessionId = liveSession.Id,
                SponsorZaloUserId = sponsorZaloUserId,
                SponsorDisplayName = sponsorDisplayName,
                DisplayName = displayName,
                GuestIndex = index + 1,
                SponsorSequence = sequence,
                Gender = spec.Gender,
                SourceMessageId = sourceMessageId,
                RecruitmentMessageId = recruitmentMessageId,
                Status = index < available
                    ? ZaloGuestReservationStatus.Active
                    : ZaloGuestReservationStatus.Waitlisted,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (reservation.Status == ZaloGuestReservationStatus.Active)
            {
                var playerResult = await draftService.AddGuestPlayerFromBotAsync(
                    liveSession.AdminUserId,
                    liveSession.Id,
                    displayName);
                if (!playerResult.IsSuccess || playerResult.Value is null)
                    throw new InvalidOperationException(playerResult.Error ?? $"Không thêm được guest {displayName}.");
                reservation.SessionPlayerId = playerResult.Value.Player.Id;
                if (spec.Gender is not null)
                {
                    var profileUpdate = await draftService.UpdatePlayerProfileFromBotAsync(
                        liveSession.AdminUserId,
                        liveSession.Id,
                        displayName,
                        spec.Gender,
                        null,
                        null,
                        sessionPlayerId: reservation.SessionPlayerId);
                    if (!profileUpdate.IsSuccess)
                        throw new InvalidOperationException(profileUpdate.Error ?? $"Không cập nhật được giới tính cho {displayName}.");
                }
                added.Add(reservation);
            }
            else
            {
                waitlisted.Add(reservation);
            }
            db.ZaloGuestReservations.Add(reservation);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var readinessAfter = await new ZaloDraftReadinessService(db)
            .BuildAsync(liveSession.Id, cancellationToken: cancellationToken);
        return new ZaloGuestSignupMutationResult(
            liveSession.Id,
            liveSession.Name,
            readinessBefore.EffectiveSlotCount,
            readinessAfter?.EffectiveSlotCount ?? readinessBefore.EffectiveSlotCount + added.Count,
            capacity,
            added,
            waitlisted);
    }

    public async Task<ZaloGuestUpdateResult> CancelAsync(
        MatchSession session,
        string sponsorZaloUserId,
        ZaloRecruitmentGuestCommand command,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var candidates = await LoadSponsorGuestsAsync(session.Id, sponsorZaloUserId, cancellationToken);
        var selected = ResolveSelection(candidates, command);
        if (selected.NeedsClarification)
            return new ZaloGuestUpdateResult(session.Id, session.Name, [], true, selected.Clarification);
        if (selected.Items.Count == 0)
            return new ZaloGuestUpdateResult(session.Id, session.Name, [], true, "Tui chưa thấy guest nào của ông đang giữ/chờ ở kèo này.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        foreach (var reservation in selected.Items)
        {
            reservation.Status = ZaloGuestReservationStatus.Cancelled;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(reservation.SessionPlayerId))
            {
                var player = await db.SessionPlayers.SingleOrDefaultAsync(
                    item => item.Id == reservation.SessionPlayerId,
                    cancellationToken);
                if (player is not null) player.IsPresent = false;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ZaloGuestUpdateResult(session.Id, session.Name, selected.Items);
    }

    public async Task<ZaloGuestUpdateResult> UpdateProfileAsync(
        MatchSession session,
        string sponsorZaloUserId,
        ZaloRecruitmentGuestCommand command,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var candidates = await LoadSponsorGuestsAsync(session.Id, sponsorZaloUserId, cancellationToken);
        var selected = ResolveSelection(candidates, command);
        if (selected.NeedsClarification)
            return new ZaloGuestUpdateResult(session.Id, session.Name, [], true, selected.Clarification);
        if (selected.Items.Count == 0)
            return new ZaloGuestUpdateResult(session.Id, session.Name, [], true, "Tui chưa thấy guest phù hợp để cập nhật.");
        if (!string.IsNullOrWhiteSpace(command.RenameTo) && selected.Items.Count != 1)
            return new ZaloGuestUpdateResult(session.Id, session.Name, [], true, "Đổi tên cần chỉ đúng một guest, ví dụ `bạn #1 tên Minh`.");

        foreach (var reservation in selected.Items)
        {
            if (!string.IsNullOrWhiteSpace(command.RenameTo))
                reservation.DisplayName = command.RenameTo!.Trim();
            if (command.Gender is not null)
                reservation.Gender = command.Gender;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrWhiteSpace(reservation.SessionPlayerId))
            {
                var player = await db.SessionPlayers.SingleOrDefaultAsync(item => item.Id == reservation.SessionPlayerId, cancellationToken);
                if (player is not null)
                {
                    if (!string.IsNullOrWhiteSpace(command.RenameTo)) player.DisplayName = reservation.DisplayName;
                    if (command.Gender is not null) player.Gender = command.Gender.Value;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return new ZaloGuestUpdateResult(session.Id, session.Name, selected.Items);
    }

    public async Task<IReadOnlyList<ZaloGuestPromotion>> PromoteWaitingAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var session = await db.MatchSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
            return [];
        if (session.StartTime is { } start && start <= DateTimeOffset.UtcNow) return [];

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var readiness = await new ZaloDraftReadinessService(db).BuildAsync(sessionId, cancellationToken: cancellationToken);
        if (readiness is null) return [];
        var available = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
        if (available == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var waiting = await db.ZaloGuestReservations
            .Where(item => item.SessionId == sessionId && item.Status == ZaloGuestReservationStatus.Waitlisted)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.SponsorSequence)
            .Take(available)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var draftService = new SessionDraftService(db);
        var promoted = new List<ZaloGuestPromotion>();
        var effective = readiness.EffectiveSlotCount;
        foreach (var reservation in waiting)
        {
            var add = await draftService.AddGuestPlayerFromBotAsync(session.AdminUserId, session.Id, reservation.DisplayName);
            if (!add.IsSuccess || add.Value is null) continue;
            reservation.SessionPlayerId = add.Value.Player.Id;
            reservation.Status = ZaloGuestReservationStatus.Active;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            if (reservation.Gender is not null)
            {
                await draftService.UpdatePlayerProfileFromBotAsync(
                    session.AdminUserId,
                    session.Id,
                    reservation.DisplayName,
                    reservation.Gender,
                    null,
                    null,
                    sessionPlayerId: reservation.SessionPlayerId);
            }
            effective += 1;
            promoted.Add(new ZaloGuestPromotion(
                session.Id,
                session.Name,
                reservation.SponsorZaloUserId,
                reservation.SponsorDisplayName,
                reservation.DisplayName,
                effective,
                readiness.Capacity));
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return promoted;
    }

    public async Task<IReadOnlyList<string>> ListSessionsWithWaitingAsync(CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        return await db.ZaloGuestReservations
            .AsNoTracking()
            .Where(item => item.Status == ZaloGuestReservationStatus.Waitlisted)
            .Select(item => item.SessionId)
            .Distinct()
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<ZaloGuestReservation>> LoadSponsorGuestsAsync(
        string sessionId,
        string sponsorZaloUserId,
        CancellationToken cancellationToken) =>
        await db.ZaloGuestReservations
            .Where(item => item.SessionId == sessionId &&
                           item.SponsorZaloUserId == sponsorZaloUserId &&
                           (item.Status == ZaloGuestReservationStatus.Active || item.Status == ZaloGuestReservationStatus.Waitlisted))
            .OrderBy(item => item.SponsorSequence)
            .ToListAsync(cancellationToken);

    private static (List<ZaloGuestReservation> Items, bool NeedsClarification, string? Clarification) ResolveSelection(
        List<ZaloGuestReservation> candidates,
        ZaloRecruitmentGuestCommand command)
    {
        if (command.ApplyAll)
        {
            var all = command.Quantity >= candidates.Count ? candidates : candidates.Take(command.Quantity).ToList();
            return (all, false, null);
        }
        if (command.SponsorSequence is { } sequence)
            return (candidates.Where(item => item.SponsorSequence == sequence).ToList(), false, null);
        if (!string.IsNullOrWhiteSpace(command.GuestReference))
        {
            var reference = ZaloBotIntelligence.Normalize(command.GuestReference);
            var matches = candidates.Where(item =>
                    ZaloBotIntelligence.Normalize(item.DisplayName).Contains(reference, StringComparison.Ordinal) ||
                    reference.Contains(ZaloBotIntelligence.Normalize(item.DisplayName), StringComparison.Ordinal))
                .ToList();
            return matches.Count <= 1
                ? (matches, false, null)
                : ([], true, $"Tui thấy nhiều guest khớp '{command.GuestReference}'. Nói `bạn #1/#2...` giúp tui.");
        }
        if (command.Quantity == 1 && candidates.Count > 1)
        {
            var choices = string.Join(", ", candidates.Select(item => $"#{item.SponsorSequence} {item.DisplayName}"));
            return ([], true, $"Ông đang có {candidates.Count} guest: {choices}. Nói rõ bạn nào nha.");
        }
        return (candidates.Take(Math.Min(command.Quantity, candidates.Count)).ToList(), false, null);
    }

    private static string MakeUniqueName(string requested, string sponsor, HashSet<string> occupied)
    {
        var clean = requested.Trim();
        if (!occupied.Contains(ZaloBotIntelligence.Normalize(clean))) return clean;
        var candidate = $"{clean} (bạn {sponsor})";
        if (!occupied.Contains(ZaloBotIntelligence.Normalize(candidate))) return candidate;
        for (var index = 2; index <= 20; index += 1)
        {
            candidate = $"{clean} ({index})";
            if (!occupied.Contains(ZaloBotIntelligence.Normalize(candidate))) return candidate;
        }
        return $"{clean} {Guid.NewGuid():N}"[..Math.Min(80, clean.Length + 33)];
    }
}
