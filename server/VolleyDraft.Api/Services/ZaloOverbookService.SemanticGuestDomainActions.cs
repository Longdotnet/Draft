using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private sealed record ExtendedSemanticGuestExecution(string Reply, string Outcome);

    private async Task<ExtendedSemanticGuestExecution?> TryExecuteExtendedSemanticGuestActionAsync(
        MatchSession session,
        string senderId,
        string? senderName,
        string sourceMessageId,
        string? recruitmentMessageId,
        ZaloSemanticGuestValidationResult validation,
        CancellationToken cancellationToken)
    {
        var domain = new ZaloGuestDomainActionService(db);
        if (validation.Action == ZaloSemanticGuestActionKind.AddTentativeGuests)
        {
            var specs = validation.Items.Select(item => new ZaloRecruitmentGuestSpec(
                item.DisplayName, item.Gender, item.Role, item.Level)).ToArray();
            var result = await domain.AddTentativeAsync(
                session,
                senderId,
                FriendlySponsorName(senderName, senderId),
                sourceMessageId,
                recruitmentMessageId,
                specs,
                validation.Quantity,
                cancellationToken);
            var names = string.Join(", ", result.Changed.Select(item => $"#{item.SponsorSequence} {item.DisplayName}"));
            return new(
                $"Tui ghi nhớ tạm {names} cho {result.SessionName} nha, nhưng CHƯA chiếm slot vì ông chưa chốt chắc. Khi biết đi thì nói `bạn đó đi nha`/`#1 chốt đi`; lúc đó tui mới đọc roster thật rồi đưa vào roster hoặc waitlist.",
                result.Idempotent ? "guest_semantic_tentative_idempotent" : "guest_semantic_tentative_added");
        }

        if (validation.Action == ZaloSemanticGuestActionKind.ConfirmGuests)
        {
            var ids = validation.Items.Select(item => item.ReservationId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToArray();
            var result = await domain.ConfirmAsync(session, senderId, ids, cancellationToken);
            var active = result.Changed.Where(item => item.Status == ZaloGuestReservationStatus.Active).ToArray();
            var waiting = result.Changed.Where(item => item.Status == ZaloGuestReservationStatus.Waitlisted).ToArray();
            var details = string.Join(", ", result.Changed.Select(item => $"#{item.SponsorSequence} {item.DisplayName} ({item.Status})"));
            var placement = waiting.Length == 0
                ? "đã vào roster"
                : active.Length == 0
                    ? "roster đang full nên đã vào waitlist"
                    : $"{active.Length} vào roster, {waiting.Length} vào waitlist";
            return new($"Chốt rồi 👌 {details}; {placement}. Roster hiện {result.EffectiveSlots}/{result.Capacity}.", "guest_semantic_confirmed");
        }

        if (validation.Action == ZaloSemanticGuestActionKind.ReplaceGuest)
        {
            var old = validation.Items[0];
            var replacement = validation.Items[1];
            var oldId = old.ReservationId ?? throw new InvalidOperationException("Replacement target is not grounded.");
            var before = await db.ZaloGuestReservations.AsNoTracking().SingleAsync(item => item.Id == oldId, cancellationToken);
            var result = await domain.ReplaceAsync(
                session,
                senderId,
                FriendlySponsorName(senderName, senderId),
                oldId,
                sourceMessageId,
                recruitmentMessageId,
                new ZaloRecruitmentGuestSpec(replacement.DisplayName, replacement.Gender, replacement.Role, replacement.Level),
                cancellationToken);
            var next = result.Changed.Single(item => item.Id != before.Id);
            return new(
                $"Ok, tui thay {before.DisplayName} bằng #{next.SponsorSequence} {next.DisplayName} trong cùng transaction. Trạng thái mới: {next.Status}; roster hiện {result.EffectiveSlots}/{result.Capacity}. Không có khoảng trống trung gian để bot réo tuyển nhầm.",
                "guest_semantic_replaced");
        }

        if (validation.Action == ZaloSemanticGuestActionKind.UpdateGuestProfiles)
        {
            var ids = validation.Items.Select(item => item.ReservationId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToArray();
            var tentativeIds = await db.ZaloGuestReservations.AsNoTracking()
                .Where(item => ids.Contains(item.Id) && item.SessionId == session.Id && item.SponsorZaloUserId == senderId && item.Status == ZaloGuestReservationStatus.Tentative)
                .Select(item => item.Id).ToListAsync(cancellationToken);
            if (tentativeIds.Count == 0) return null;

            var changed = new List<ZaloGuestReservation>();
            var normal = new ZaloGuestReservationService(db);
            foreach (var item in validation.Items)
            {
                if (item.ReservationId is null) continue;
                if (tentativeIds.Contains(item.ReservationId, StringComparer.Ordinal))
                {
                    changed.Add(await domain.UpdateTentativeProfileAsync(
                        session.Id, senderId, item.ReservationId, item, cancellationToken));
                }
                else
                {
                    var result = await normal.UpdateProfileAsync(
                        session,
                        senderId,
                        new ZaloRecruitmentGuestCommand(
                            ZaloRecruitmentGuestCommandKind.UpdateProfile,
                            SponsorSequence: item.SponsorSequence,
                            RenameTo: item.DisplayName,
                            Gender: item.Gender,
                            Role: item.Role,
                            Level: item.Level),
                        cancellationToken);
                    if (result.NeedsClarification)
                        throw new InvalidOperationException(result.Clarification ?? "Không cập nhật được guest.");
                    changed.AddRange(result.Changed);
                }
            }
            return new(BuildSemanticGuestProfileReply(session.Name, changed), "guest_semantic_profile_updated");
        }

        return null;
    }
}
