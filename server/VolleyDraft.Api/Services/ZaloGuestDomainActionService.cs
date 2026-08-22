using System.Data;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloGuestDomainActionResult(
    string SessionId,
    string SessionName,
    IReadOnlyList<ZaloGuestReservation> Changed,
    int EffectiveSlots,
    int Capacity,
    bool Idempotent = false);

/// <summary>
/// Higher-level guest transactions that cannot be safely decomposed into unrelated
/// chat commands. Tentative never occupies a roster slot; Confirm promotes it using
/// the live roster; Replace swaps old/new inside one serializable transaction so no
/// intermediate empty slot can wake recruitment.
/// </summary>
internal sealed class ZaloGuestDomainActionService(VolleyDraftDbContext db)
{
    public async Task<ZaloGuestDomainActionResult> AddTentativeAsync(
        MatchSession session,
        string sponsorId,
        string sponsorName,
        string sourceMessageId,
        string? recruitmentMessageId,
        IReadOnlyList<ZaloRecruitmentGuestSpec> specs,
        int quantity,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var existing = await db.ZaloGuestReservations.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.SourceMessageId == sourceMessageId)
            .OrderBy(x => x.GuestIndex).ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            var ready = await new ZaloDraftReadinessService(db).BuildAsync(session.Id, cancellationToken: cancellationToken);
            return new(session.Id, session.Name, existing, ready?.EffectiveSlotCount ?? 0,
                ready?.Capacity ?? session.TeamCount * session.TeamSize, true);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var live = await LoadLiveSessionAsync(session.Id, cancellationToken);
        var next = (await db.ZaloGuestReservations.Where(x => x.SessionId == live.Id && x.SponsorZaloUserId == sponsorId)
            .Select(x => (int?)x.SponsorSequence).MaxAsync(cancellationToken) ?? 0) + 1;
        var occupied = await OccupiedNamesAsync(live.Id, cancellationToken);
        var changed = new List<ZaloGuestReservation>();
        for (var i = 0; i < Math.Clamp(quantity, 1, 2); i++)
        {
            var spec = i < specs.Count ? specs[i] : new ZaloRecruitmentGuestSpec();
            var sequence = next + i;
            var name = MakeUniqueName(string.IsNullOrWhiteSpace(spec.DisplayName)
                ? $"Bạn của {sponsorName} #{sequence}"
                : spec.DisplayName!.Trim(), occupied);
            occupied.Add(ZaloBotIntelligence.Normalize(name));
            var row = new ZaloGuestReservation
            {
                SessionId = live.Id,
                SponsorZaloUserId = sponsorId,
                SponsorDisplayName = sponsorName,
                DisplayName = name,
                GuestIndex = i + 1,
                SponsorSequence = sequence,
                Gender = spec.Gender,
                Role = spec.Role,
                Level = spec.Level,
                SourceMessageId = sourceMessageId,
                RecruitmentMessageId = recruitmentMessageId,
                Status = ZaloGuestReservationStatus.Tentative,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloGuestReservations.Add(row);
            changed.Add(row);
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var readiness = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken);
        return new(live.Id, live.Name, changed, readiness?.EffectiveSlotCount ?? 0,
            readiness?.Capacity ?? live.TeamCount * live.TeamSize);
    }

    public async Task<ZaloGuestDomainActionResult> ConfirmAsync(
        MatchSession session,
        string sponsorId,
        IReadOnlyList<string> reservationIds,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var ids = reservationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Chưa có guest tentative nào để xác nhận.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var live = await LoadLiveSessionAsync(session.Id, cancellationToken);
        var matched = await db.ZaloGuestReservations
            .Where(x => x.SessionId == live.Id && x.SponsorZaloUserId == sponsorId && ids.Contains(x.Id))
            .OrderBy(x => x.SponsorSequence)
            .ToListAsync(cancellationToken);
        if (matched.Count != ids.Length)
            throw new InvalidOperationException("Guest cần xác nhận không còn thuộc đúng sponsor/kèo này.");

        var tentative = matched.Where(x => x.Status == ZaloGuestReservationStatus.Tentative).ToList();
        if (tentative.Count == 0)
        {
            if (matched.All(x => x.Status is ZaloGuestReservationStatus.Active or ZaloGuestReservationStatus.Waitlisted))
            {
                var ready = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new(live.Id, live.Name, matched, ready?.EffectiveSlotCount ?? 0,
                    ready?.Capacity ?? live.TeamCount * live.TeamSize, true);
            }
            throw new InvalidOperationException("Không còn guest tentative phù hợp để xác nhận.");
        }
        if (matched.Any(x => x.Status is ZaloGuestReservationStatus.Cancelled or ZaloGuestReservationStatus.Linked))
            throw new InvalidOperationException("Một guest cần xác nhận đã đổi trạng thái nên tui chưa chốt tiếp.");

        var readiness = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Không đọc được roster hiện tại.");
        var available = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
        var draft = new SessionDraftService(db);
        for (var i = 0; i < tentative.Count; i++)
        {
            var row = tentative[i];
            if (i < available)
            {
                var add = await draft.AddGuestPlayerFromBotAsync(live.AdminUserId, live.Id, row.DisplayName);
                if (!add.IsSuccess || add.Value is null)
                    throw new InvalidOperationException(add.Error ?? "Không đưa guest vào roster được.");
                row.SessionPlayerId = add.Value.Player.Id;
                row.Status = ZaloGuestReservationStatus.Active;
                if (row.Gender is not null || row.Role is not null || row.Level is not null)
                {
                    var profile = await draft.UpdatePlayerProfileFromBotAsync(
                        live.AdminUserId, live.Id, row.DisplayName, row.Gender, row.Role, row.Level,
                        sessionPlayerId: row.SessionPlayerId);
                    if (!profile.IsSuccess)
                        throw new InvalidOperationException(profile.Error ?? "Không cập nhật profile guest được.");
                }
            }
            else
            {
                row.SessionPlayerId = null;
                row.Status = ZaloGuestReservationStatus.Waitlisted;
            }
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var after = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken);
        return new(live.Id, live.Name, matched, after?.EffectiveSlotCount ?? readiness.EffectiveSlotCount,
            readiness.Capacity);
    }

    public async Task<ZaloGuestDomainActionResult> CancelTentativeAsync(
        MatchSession session,
        string sponsorId,
        IReadOnlyList<string> reservationIds,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var ids = reservationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Chưa có guest tentative nào để huỷ.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var live = await LoadLiveSessionAsync(session.Id, cancellationToken);
        var rows = await db.ZaloGuestReservations
            .Where(x => x.SessionId == live.Id && x.SponsorZaloUserId == sponsorId && ids.Contains(x.Id))
            .OrderBy(x => x.SponsorSequence)
            .ToListAsync(cancellationToken);
        if (rows.Count != ids.Length)
            throw new InvalidOperationException("Guest tentative cần huỷ không còn thuộc đúng sponsor/kèo này.");

        var changed = rows.Where(x => x.Status == ZaloGuestReservationStatus.Tentative).ToList();
        var idempotent = changed.Count == 0 && rows.All(x => x.Status == ZaloGuestReservationStatus.Cancelled);
        if (!idempotent && changed.Count == 0)
            throw new InvalidOperationException("Guest này không còn là tentative nên không huỷ theo luồng tentative được.");

        foreach (var row in changed)
        {
            row.Status = ZaloGuestReservationStatus.Cancelled;
            row.SessionPlayerId = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var readiness = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken);
        return new(live.Id, live.Name, rows, readiness?.EffectiveSlotCount ?? 0,
            readiness?.Capacity ?? live.TeamCount * live.TeamSize, idempotent);
    }

    public async Task<ZaloGuestDomainActionResult> ReplaceAsync(
        MatchSession session,
        string sponsorId,
        string sponsorName,
        string oldReservationId,
        string sourceMessageId,
        string? recruitmentMessageId,
        ZaloRecruitmentGuestSpec replacement,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var duplicate = await db.ZaloGuestReservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.SourceMessageId == sourceMessageId && x.GuestIndex == 1, cancellationToken);
        if (duplicate is not null)
        {
            var ready = await new ZaloDraftReadinessService(db).BuildAsync(session.Id, cancellationToken: cancellationToken);
            return new(session.Id, session.Name, [duplicate], ready?.EffectiveSlotCount ?? 0,
                ready?.Capacity ?? session.TeamCount * session.TeamSize, true);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var live = await LoadLiveSessionAsync(session.Id, cancellationToken);
        var old = await db.ZaloGuestReservations.SingleOrDefaultAsync(x =>
            x.Id == oldReservationId && x.SessionId == live.Id && x.SponsorZaloUserId == sponsorId &&
            (x.Status == ZaloGuestReservationStatus.Active ||
             x.Status == ZaloGuestReservationStatus.Waitlisted ||
             x.Status == ZaloGuestReservationStatus.Tentative), cancellationToken)
            ?? throw new InvalidOperationException("Guest cần thay không còn ở trạng thái có thể thay.");
        var oldStatus = old.Status;
        if (!string.IsNullOrWhiteSpace(old.SessionPlayerId))
        {
            var player = await db.SessionPlayers.SingleOrDefaultAsync(x => x.Id == old.SessionPlayerId, cancellationToken);
            if (player is not null) player.IsPresent = false;
        }
        old.Status = ZaloGuestReservationStatus.Cancelled;
        old.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken); // still inside transaction; rollback restores old slot on failure.

        var next = (await db.ZaloGuestReservations.Where(x => x.SessionId == live.Id && x.SponsorZaloUserId == sponsorId)
            .Select(x => (int?)x.SponsorSequence).MaxAsync(cancellationToken) ?? 0) + 1;
        var occupied = await OccupiedNamesAsync(live.Id, cancellationToken);
        var name = MakeUniqueName(string.IsNullOrWhiteSpace(replacement.DisplayName)
            ? $"Bạn của {sponsorName} #{next}"
            : replacement.DisplayName!.Trim(), occupied);
        var row = new ZaloGuestReservation
        {
            SessionId = live.Id,
            SponsorZaloUserId = sponsorId,
            SponsorDisplayName = sponsorName,
            DisplayName = name,
            GuestIndex = 1,
            SponsorSequence = next,
            Gender = replacement.Gender,
            Role = replacement.Role,
            Level = replacement.Level,
            SourceMessageId = sourceMessageId,
            RecruitmentMessageId = recruitmentMessageId ?? old.RecruitmentMessageId,
            Status = oldStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (oldStatus == ZaloGuestReservationStatus.Active)
        {
            var draft = new SessionDraftService(db);
            var add = await draft.AddGuestPlayerFromBotAsync(live.AdminUserId, live.Id, name);
            if (!add.IsSuccess || add.Value is null)
                throw new InvalidOperationException(add.Error ?? "Không thay guest vào roster được.");
            row.SessionPlayerId = add.Value.Player.Id;
            if (row.Gender is not null || row.Role is not null || row.Level is not null)
            {
                var profile = await draft.UpdatePlayerProfileFromBotAsync(
                    live.AdminUserId, live.Id, name, row.Gender, row.Role, row.Level, sessionPlayerId: row.SessionPlayerId);
                if (!profile.IsSuccess)
                    throw new InvalidOperationException(profile.Error ?? "Không cập nhật profile guest thay thế được.");
            }
        }
        db.ZaloGuestReservations.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var after = await new ZaloDraftReadinessService(db).BuildAsync(live.Id, cancellationToken: cancellationToken);
        return new(live.Id, live.Name, [old, row], after?.EffectiveSlotCount ?? 0,
            after?.Capacity ?? live.TeamCount * live.TeamSize);
    }

    public async Task<ZaloGuestReservation> UpdateTentativeProfileAsync(
        string sessionId,
        string sponsorId,
        string reservationId,
        ZaloSemanticGuestValidatedItem item,
        CancellationToken cancellationToken)
    {
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db, cancellationToken);
        var row = await db.ZaloGuestReservations.SingleOrDefaultAsync(x =>
            x.Id == reservationId && x.SessionId == sessionId && x.SponsorZaloUserId == sponsorId &&
            x.Status == ZaloGuestReservationStatus.Tentative, cancellationToken)
            ?? throw new InvalidOperationException("Guest tentative không còn tồn tại.");
        if (!string.IsNullOrWhiteSpace(item.DisplayName)) row.DisplayName = item.DisplayName.Trim();
        if (item.Gender is not null) row.Gender = item.Gender;
        if (item.Role is not null) row.Role = item.Role;
        if (item.Level is not null) row.Level = item.Level;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    private async Task<MatchSession> LoadLiveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var live = await db.MatchSessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Kèo không còn tồn tại.");
        if (live.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
            throw new InvalidOperationException("Kèo không còn nhận thay đổi guest.");
        if (live.StartTime is { } start && start <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Kèo đã tới giờ.");
        return live;
    }

    private async Task<HashSet<string>> OccupiedNamesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var names = await db.SessionPlayers.AsNoTracking().Where(x => x.SessionId == sessionId)
            .Select(x => x.DisplayName).ToListAsync(cancellationToken);
        names.AddRange(await db.ZaloGuestReservations.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.Status != ZaloGuestReservationStatus.Cancelled)
            .Select(x => x.DisplayName).ToListAsync(cancellationToken));
        return names.Select(ZaloBotIntelligence.Normalize).ToHashSet(StringComparer.Ordinal);
    }

    private static string MakeUniqueName(string requested, HashSet<string> occupied)
    {
        var candidate = requested.Trim();
        if (!occupied.Contains(ZaloBotIntelligence.Normalize(candidate))) return candidate;
        for (var i = 2; i <= 99; i++)
        {
            var next = $"{candidate} ({i})";
            if (!occupied.Contains(ZaloBotIntelligence.Normalize(next))) return next;
        }
        return $"{candidate} {Guid.NewGuid():N}"[..Math.Min(candidate.Length + 9, 80)];
    }
}
