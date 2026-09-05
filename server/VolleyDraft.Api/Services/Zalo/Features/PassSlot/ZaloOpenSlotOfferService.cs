using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloOpenSlotOfferHandleResult(
    bool Handled,
    string? Response,
    string Intent = "OpenSlotOffer",
    string? SessionId = null);

/// <summary>
/// Turns a previously opened member-owned pass-slot offer into a safe multi-user
/// conversation. A claimant may reserve the offer with natural chat, but an actual
/// post-draft slot mutation only occurs after that same claimant confirms. Pre-draft
/// registration remains poll-authoritative and is never rewritten from ambient chat.
/// </summary>
public sealed class ZaloOpenSlotOfferService(VolleyDraftDbContext db)
{
    private static readonly Regex BareClaimPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi|tao)\s+(?:nhan|lay|hot|giu)|de\s+(?:tui|toi|minh|em)(?:\s+(?:nhan|lay|hot|giu))?)(?:\s+(?:nha|nhe|di|a|aa|voi|luon|hen|he))?[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QualifiedClaimPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi|tao)\s+(?:nhan|lay|hot|giu)|(?:de|cho)\s+(?:tui|toi|minh|em)(?:\s+(?:nhan|lay|hot|giu))?)\s+(?:(?:slot|suat|keo)(?:\s+.{0,40})?|(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?:\s+.{0,20})?|cua\s+[\p{L}\p{N}][\p{L}\p{N}\s._-]{0,40})[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CancelOfferPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi)\s+)?(?:huy|bo|thoi)\s+(?:pass|nhuong)(?:\s+(?:slot|suat|keo))?|^(?:khong|ko)\s+(?:pass|nhuong)\s+nua$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PendingClaimCancelPattern = new(
        @"^(?:huy|cancel|thoi|bo\s+qua|khong\s+can\s+nua|thoi\s+khoi(?:\s+di)?|hoi\s+khoi\s+di|khoi(?:\s+di)?|bo\s+di|khong\s+lam\s+nua|(?:huy|bo)\s+(?:nhan|claim)(?:\s+(?:slot|suat|keo))?|khong\s+nhan\s+nua)[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PendingClaimConfirmationPattern = new(
        @"^(?:xac\s+nhan(?:\s+draft)?|dong\s+y|ok\s+chay|chay\s+di|thuc\s+hien\s+di|duoc|ok|chot|lam\s+di|tao\s+di|trien\s+khai|xong|done|roi|ok\s+xong|vote\s+xong)[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ZaloOpenSlotOfferStore store = new(db);
    private readonly SessionDraftService draftService = new(db);

    public static bool IsClaimPhrase(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized.Length > 0 &&
               (BareClaimPattern.IsMatch(normalized) || QualifiedClaimPattern.IsMatch(normalized));
    }

    /// <summary>
    /// Pending OpenSlotOffer state may consume only cancellation language that clearly
    /// refers to the pending reservation itself. Broad conversation-level cancellation
    /// (for example "hủy reminder" or "hủy share slot") belongs to the fresh intent
    /// router and must not be stolen merely because a reservation is still active.
    /// </summary>
    internal static bool IsPendingClaimCancellation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        return normalized.Length > 0 && PendingClaimCancelPattern.IsMatch(normalized);
    }

    /// <summary>
    /// Confirmation ownership is deliberately exact for the same reason as cancel:
    /// "chốt" continues a reservation, while domain-qualified phrases such as
    /// "chốt slot" may belong to another deterministic capability such as waitlist.
    /// </summary>
    internal static bool IsPendingClaimConfirmation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        return normalized.Length > 0 && PendingClaimConfirmationPattern.IsMatch(normalized);
    }

    public async Task<ZaloOpenSlotOfferHandleResult> TryHandleAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        var senderId = CleanId(incoming.SenderId);
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return new(false, null);

        var pending = await store.LoadPendingClaimAsync(connectionId, groupId, senderId, cancellationToken);
        if (pending?.Status == ZaloOpenSlotOfferStatus.ClaimPending &&
            pending.ClaimExpiresAt is { } existingClaimExpiresAt &&
            existingClaimExpiresAt <= DateTimeOffset.UtcNow)
        {
            await store.ReleaseClaimAsync(pending.Id, senderId, cancellationToken);
            pending = null;
        }

        if (pending is not null)
        {
            if (IsPendingClaimCancellation(incoming.Content))
            {
                if (pending.Status == ZaloOpenSlotOfferStatus.Applying)
                {
                    return new(
                        true,
                        $"Slot {FriendlyName(pending.OwnerDisplayName)} ở {pending.SessionName} đang chốt vào roster rồi ⏳ Tui không nhả giữa lúc cập nhật để tránh một slot thành hai trạng thái. Nếu thao tác bị gián đoạn, worker sẽ đối chiếu roster rồi tự recovery.",
                        ZaloBotIntent.SlotTransfer.ToString());
                }

                var released = await store.ReleaseClaimAsync(pending.Id, senderId, cancellationToken);
                return released
                    ? new(true, $"Oke, tui nhả slot {pending.OwnerDisplayName} ở {pending.SessionName} lại nha 😆 Ai khác vẫn có thể nhận.")
                    : new(true, $"Slot {pending.SessionName} vừa đổi trạng thái ở lượt khác nên tui không báo huỷ bừa nha. Tui giữ theo trạng thái mới nhất.");
            }

            if (IsPendingClaimConfirmation(incoming.Content))
                return await ConfirmClaimAsync(connectionId, groupId, incoming, pending, cancellationToken);

            if (IsClaimPhrase(incoming.Content))
            {
                return new(
                    true,
                    pending.Status == ZaloOpenSlotOfferStatus.Applying
                        ? $"Ông đang được chốt slot {FriendlyName(pending.OwnerDisplayName)} ở {pending.SessionName} rồi ⏳ Tui không chạy thêm claim khác giữa lúc cập nhật."
                        : $"Ông đang giữ slot {FriendlyName(pending.OwnerDisplayName)} ở {pending.SessionName} rồi á 😆 Muốn đổi sang slot khác thì huỷ nhận kèo này trước nha.");
            }
        }

        if (CancelOfferPattern.IsMatch(ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty)))
            return await CancelOwnedOfferAsync(connectionId, groupId, senderId, incoming.Content, cancellationToken);

        if (!IsClaimPhrase(incoming.Content)) return new(false, null);

        var offers = await store.ListClaimableAsync(connectionId, groupId, senderId, cancellationToken);
        if (offers.Count == 0) return new(false, null);

        var humanMentionIds = incoming.Mentions
            .Select(mention => CleanId(mention.Uid))
            .Where(id => id.Length > 0 && !string.Equals(id, CleanId(incoming.BotId), StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (humanMentionIds.Count > 0)
        {
            var mentionedOffers = offers
                .Where(offer => humanMentionIds.Contains(offer.OwnerZaloUserId))
                .ToList();
            if (mentionedOffers.Count == 0) return new(false, null);
            offers = mentionedOffers;
        }

        var referenced = offers.Where(offer => MatchesOfferReference(incoming.Content, offer)).ToList();
        if (referenced.Count > 0) offers = referenced;
        if (offers.Count > 1)
        {
            var choices = string.Join(" | ", offers.Take(4).Select(offer => $"{FriendlyName(offer.OwnerDisplayName)}: {offer.SessionName}"));
            return new(true, $"Tui thấy đang có mấy slot mở nè 😆 {choices}. Ông nhận kèo nào thì nói kiểu ‘tui nhận T6’ nha.");
        }

        var offer = offers[0];
        var session = await LoadSessionAsync(connectionId, groupId, offer.SessionId, cancellationToken);
        if (session is null || session.Status == SessionStatus.Cancelled ||
            session.StartTime is { } start && start <= DateTimeOffset.UtcNow)
            return new(true, $"Slot {offer.SessionName} này hết hiệu lực rồi á, tui không nhận bừa nha.");

        var owner = ResolveOwner(session, offer);
        if (owner is null)
            return new(true, $"Tui không còn xác minh được slot của {FriendlyName(offer.OwnerDisplayName)} ở {offer.SessionName}, nên chưa cho nhận nha.");

        if (string.Equals(offer.OwnerZaloUserId, senderId, StringComparison.Ordinal))
            return new(true, "Slot của chính ông mà 😆 Muốn huỷ pass thì nói ‘huỷ pass’ nha.");

        if (session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection &&
            IsSenderAlreadyPresent(session, senderId, incoming.SenderName))
        {
            return new(
                true,
                $"Ông đang có slot {session.Name} rồi á 😆 Poll chỉ tính một suất/người nên tui không cho hốt thêm slot này. Nếu muốn share/chơi chung thì nói riêng nha.");
        }

        if (session.Status == SessionStatus.Finished)
        {
            var preview = await draftService.PreviewPostDraftSlotTransferAsync(
                session.AdminUserId,
                session.Id,
                owner.DisplayName,
                new ShareSlotParticipantInput(incoming.SenderName, senderId),
                cancellationToken);
            if (!preview.IsSuccess || preview.Value is null)
                return new(true, preview.Error ?? "Slot này hiện chưa nhận được á.");
        }
        else if (session.Status == SessionStatus.Drafting)
        {
            return new(true, $"{offer.SessionName} đang draft dở á 😅 Chờ draft xong rồi nhận slot này giúp tui nha.");
        }

        var claimMinutes = session.Status == SessionStatus.Finished ? 10 : 20;
        var reservationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(claimMinutes);
        if (reservationExpiresAt > offer.ExpiresAt) reservationExpiresAt = offer.ExpiresAt;
        var claimed = await store.TryClaimAsync(
            offer,
            senderId,
            CleanName(incoming.SenderName),
            incoming.MessageId,
            reservationExpiresAt,
            cancellationToken);
        if (!claimed)
            return new(true, "Slot vừa có người chạm trước rồi 😭 Tui refresh lại kèo nha.");

        var claimant = FriendlyName(incoming.SenderName);
        var ownerName = FriendlyName(owner.DisplayName);
        if (session.Status == SessionStatus.Finished)
        {
            return new(
                true,
                $"{claimant} hốt slot {ownerName} ở {session.Name} nha 😆 Chốt trong khoảng {claimMinutes} phút thì nói ‘chốt’ cái tui chuyển luôn.",
                ZaloBotIntent.SlotTransfer.ToString());
        }

        return new(
            true,
            $"{claimant} nhận slot {ownerName} ở {session.Name} nha 👌 Kèo chưa draft nên roster vẫn theo poll: {ownerName} bỏ vote, {claimant} vote {session.Name}. Xong nói ‘xong’ trong khoảng {claimMinutes} phút để tui check, tui không tự sửa poll đâu.",
            "OpenSlotOfferPreDraftClaim");
    }

    private async Task<ZaloOpenSlotOfferHandleResult> ConfirmClaimAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloOpenSlotOfferSnapshot offer,
        CancellationToken cancellationToken)
    {
        var claimantId = CleanId(incoming.SenderId);

        // Applying is intentionally non-interruptible. ClaimExpiresAt is the user's
        // reservation deadline before confirmation; once the CAS enters Applying, the
        // domain transfer owns the critical section and rescue handles only stale crash
        // recovery by checking canonical roster state.
        if (offer.Status == ZaloOpenSlotOfferStatus.Applying)
            return new(true, "Tui đang chốt slot này vào roster rồi ⏳ Không chạy lại hay huỷ ngang để tránh nhân đôi thao tác nha.", ZaloBotIntent.SlotTransfer.ToString());

        if (offer.ClaimExpiresAt is { } claimExpiresAt && claimExpiresAt <= DateTimeOffset.UtcNow)
        {
            var released = await store.ReleaseClaimAsync(offer.Id, claimantId, cancellationToken);
            return released
                ? new(true, $"Claim slot {offer.SessionName} vừa hết thời gian giữ rồi 😅 Tui mở lại cho cả nhóm nha.")
                : new(true, $"Slot {offer.SessionName} vừa đổi trạng thái ở lượt khác; tui không tự mở lại khi chưa chắc nha.");
        }

        var session = await LoadSessionAsync(connectionId, groupId, offer.SessionId, cancellationToken);
        if (session is null || session.Status == SessionStatus.Cancelled ||
            session.StartTime is { } start && start <= DateTimeOffset.UtcNow)
        {
            await store.ReleaseClaimAsync(offer.Id, claimantId, cancellationToken);
            return new(true, $"Kèo {offer.SessionName} hết hiệu lực rồi nên tui không chuyển slot nha.");
        }

        if (session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection)
        {
            var ownerStillPresent = ResolveOwner(session, offer) is not null;
            var claimantPresent = IsSenderAlreadyPresent(session, claimantId, incoming.SenderName);
            if (!ownerStillPresent && claimantPresent)
            {
                if (await store.TryBeginApplyAsync(offer.Id, claimantId, cancellationToken))
                    await store.CompleteAsync(offer.Id, claimantId, cancellationToken);
                return new(true, $"Oke 👌 roster {session.Name} giờ đã thấy {FriendlyName(incoming.SenderName)} vào và {FriendlyName(offer.OwnerDisplayName)} ra rồi, slot coi như chốt.");
            }

            return new(
                true,
                $"Tui chưa thấy poll/roster {session.Name} đổi đúng á. {FriendlyName(offer.OwnerDisplayName)} bỏ vote + {FriendlyName(incoming.SenderName)} vote {session.Name} trước nha, rồi nói ‘xong’ tui check lại.",
                "OpenSlotOfferPreDraftClaim");
        }

        if (session.Status == SessionStatus.Drafting)
            return new(true, $"{session.Name} đang draft dở, chờ draft xong rồi nói ‘chốt’ lại nha 😅");

        if (session.Status != SessionStatus.Finished)
            return new(true, $"Trạng thái {session.Name} đang đổi nên tui chưa dám chuyển slot.");

        var owner = ResolveOwner(session, offer);
        if (owner is null)
        {
            await store.ReleaseClaimAsync(offer.Id, claimantId, cancellationToken);
            return new(true, $"Tui không còn thấy slot của {FriendlyName(offer.OwnerDisplayName)} trong {session.Name}, nên dừng claim này nha.");
        }

        var preview = await draftService.PreviewPostDraftSlotTransferAsync(
            session.AdminUserId,
            session.Id,
            owner.DisplayName,
            new ShareSlotParticipantInput(incoming.SenderName, claimantId),
            cancellationToken);
        if (!preview.IsSuccess || preview.Value is null)
        {
            await store.ReleaseClaimAsync(offer.Id, claimantId, cancellationToken);
            return new(true, preview.Error ?? "Slot này vừa đổi trạng thái nên tui chưa chuyển được nha.");
        }

        if (!await store.TryBeginApplyAsync(offer.Id, claimantId, cancellationToken))
            return new(true, "Slot này đang được chốt ở lượt khác rồi á, tui không chạy trùng nha.");

        var history = new ZaloBotActionHistoryService(db, NullLogger<ZaloBotActionHistoryService>.Instance);
        var before = await history.CaptureAsync(session.Id, cancellationToken);
        var transferred = await draftService.TransferPostDraftSlotAsync(
            session.AdminUserId,
            session.Id,
            owner.DisplayName,
            new ShareSlotParticipantInput(incoming.SenderName, claimantId));
        if (!transferred.IsSuccess || transferred.Value is null)
        {
            // Internal compensation is allowed after the domain transaction reports a
            // failure. User-driven cancel is blocked during Applying; this is the one
            // controlled path that may reopen it because the roster write did not win.
            await store.ReleaseClaimAsync(offer.Id, claimantId, cancellationToken);
            return new(true, transferred.Error ?? "Tui chưa chuyển được slot này, dữ liệu chưa đổi nha.");
        }

        await history.RecordAsync(
            session.Id,
            claimantId,
            CleanName(incoming.SenderName),
            "SlotTransfer",
            $"Open-slot offer: {transferred.Value.FromPlayerName} → {transferred.Value.ToPlayerName} trong {session.Name}",
            before,
            cancellationToken);
        await store.CompleteAsync(offer.Id, claimantId, cancellationToken);

        var profileNote = transferred.Value.NeedsProfileUpdate
            ? " Profile mới còn thiếu vị trí/trình độ thì cập nhật sau nha."
            : string.Empty;
        return new(
            true,
            $"Done 😆 {transferred.Value.ToPlayerName} hốt slot {transferred.Value.FromPlayerName} ở {session.Name} rồi, vào {transferred.Value.TeamName}.{profileNote}",
            ZaloBotIntent.SlotTransferConfirm.ToString(),
            session.Id);
    }

    private async Task<ZaloOpenSlotOfferHandleResult> CancelOwnedOfferAsync(
        string connectionId,
        string groupId,
        string senderId,
        string? content,
        CancellationToken cancellationToken)
    {
        var offers = await store.ListOwnedActiveAsync(connectionId, groupId, senderId, cancellationToken);
        if (offers.Count == 0) return new(false, null);
        var referenced = offers.Where(offer => MatchesOfferReference(content, offer)).ToList();
        if (referenced.Count > 0) offers = referenced;
        if (offers.Count > 1)
        {
            var choices = string.Join(", ", offers.Take(4).Select(offer => offer.SessionName));
            return new(true, $"Huỷ pass kèo nào á? Tui đang thấy {choices}.");
        }

        var offer = offers[0];
        if (offer.Status == ZaloOpenSlotOfferStatus.Applying)
        {
            var claimant = FriendlyName(offer.ClaimantDisplayName);
            return new(
                true,
                $"Slot {offer.SessionName} đang chốt cho {claimant} vào roster rồi ⏳ Giờ tui không huỷ ngang; làm vậy có thể khiến marketplace và đội hình lệch nhau. Chờ lượt chốt kết thúc nha.");
        }

        var cancelled = await store.CancelAsync(offer.Id, senderId, cancellationToken);
        if (!cancelled)
            return new(true, $"Slot {offer.SessionName} vừa đổi trạng thái ở lượt khác nên tui chưa huỷ bừa nha.");

        if (offer.Status == ZaloOpenSlotOfferStatus.ClaimPending && !string.IsNullOrWhiteSpace(offer.ClaimantDisplayName))
        {
            return new(
                true,
                $"Oke, huỷ pass slot {offer.SessionName} nha 👌 Reservation của {FriendlyName(offer.ClaimantDisplayName)} cũng dừng vì chủ slot đã đổi ý trước lúc chốt.");
        }

        return new(true, $"Oke, huỷ pass slot {offer.SessionName} nha 👌");
    }

    private async Task<MatchSession?> LoadSessionAsync(
        string connectionId,
        string groupId,
        string sessionId,
        CancellationToken cancellationToken) =>
        await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.Players)
                .ThenInclude(player => player.PlayerProfile)
            .SingleOrDefaultAsync(session =>
                session.Id == sessionId &&
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId &&
                session.BotEnabled,
                cancellationToken);

    internal static SessionPlayer? ResolveOwner(MatchSession session, ZaloOpenSlotOfferSnapshot offer)
    {
        var byUid = session.Players.FirstOrDefault(player =>
            player.IsPresent && CleanId(player.PlayerProfile?.ZaloUserId) == offer.OwnerZaloUserId);
        if (byUid is not null) return byUid;

        var matches = session.Players
            .Where(player => player.IsPresent &&
                             string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId) &&
                             ZaloBotIntelligence.Normalize(player.DisplayName) ==
                             ZaloBotIntelligence.Normalize(offer.OwnerDisplayName))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsSenderAlreadyPresent(MatchSession session, string senderId, string? senderName)
    {
        if (session.Players.Any(player =>
                player.IsPresent && CleanId(player.PlayerProfile?.ZaloUserId) == senderId))
            return true;

        var normalizedName = ZaloBotIntelligence.Normalize(senderName ?? string.Empty);
        if (normalizedName.Length == 0) return false;
        var blankUidMatches = session.Players
            .Where(player => player.IsPresent &&
                             string.IsNullOrWhiteSpace(player.PlayerProfile?.ZaloUserId) &&
                             ZaloBotIntelligence.Normalize(player.DisplayName) == normalizedName)
            .Take(2)
            .Count();
        return blankUidMatches == 1;
    }

    private static bool MatchesOfferReference(string? content, ZaloOpenSlotOfferSnapshot offer)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var session = ZaloBotIntelligence.Normalize(offer.SessionName);
        if (session.Length > 0 && ContainsPhrase(normalized, session)) return true;
        var owner = FriendlyName(offer.OwnerDisplayName);
        var normalizedOwner = ZaloBotIntelligence.Normalize(owner);
        return normalizedOwner.Length >= 2 && ContainsPhrase(normalized, normalizedOwner);
    }

    private static bool ContainsPhrase(string value, string phrase) =>
        Regex.IsMatch(value, $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])", RegexOptions.CultureInvariant);

    internal static string FriendlyName(string? value)
    {
        var parts = CleanName(value).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "bạn" : parts[^1];
    }

    private static string CleanName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.EndsWith("_0", StringComparison.Ordinal)) text = text[..^2];
        return text.Length <= 100 ? text : text[..100];
    }
}