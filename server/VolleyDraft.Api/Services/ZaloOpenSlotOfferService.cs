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
    string Intent = "OpenSlotOffer");

/// <summary>
/// Turns a previously opened member-owned pass-slot offer into a safe multi-user
/// conversation. A claimant may reserve the offer with natural chat, but an actual
/// post-draft slot mutation only occurs after that same claimant confirms. Pre-draft
/// registration remains poll-authoritative and is never rewritten from ambient chat.
/// </summary>
public sealed class ZaloOpenSlotOfferService(VolleyDraftDbContext db)
{
    private static readonly Regex BareClaimPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi|tao)\s+(?:nhan|lay|hot|giu)|(?:de|cho)\s+(?:tui|toi|minh|em)(?:\s+(?:nhan|lay|hot|giu))?)(?:\s+(?:nha|nhe|di|a|aa|voi|luon|hen|he))?[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QualifiedClaimPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi|tao)\s+(?:nhan|lay|hot|giu)|(?:de|cho)\s+(?:tui|toi|minh|em)(?:\s+(?:nhan|lay|hot|giu))?)\s+(?:(?:slot|suat|keo)(?:\s+.{0,40})?|(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?:\s+.{0,20})?|cua\s+[\p{L}\p{N}][\p{L}\p{N}\s._-]{0,40})[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CancelOfferPattern = new(
        @"^(?:(?:tui|toi|minh|em|anh|chi)\s+)?(?:huy|bo|thoi)\s+(?:pass|nhuong)(?:\s+(?:slot|suat|keo))?|^(?:khong|ko)\s+(?:pass|nhuong)\s+nua$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PreDraftDonePattern = new(
        @"^(?:xong|done|roi|ok\s+xong|vote\s+xong)[!?.]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ZaloOpenSlotOfferStore store = new(db);
    private readonly SessionDraftService draftService = new(db);

    public static bool IsClaimPhrase(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized.Length > 0 &&
               (BareClaimPattern.IsMatch(normalized) || QualifiedClaimPattern.IsMatch(normalized));
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

        var pending = await store.LoadPendingClaimAsync(groupId, senderId, cancellationToken);
        if (pending is not null)
        {
            if (ZaloBotIntelligence.IsCancel(incoming.Content ?? string.Empty))
            {
                await store.ReleaseClaimAsync(pending.Id, senderId, cancellationToken);
                return new(true, $"Oke, tui nhả slot {pending.OwnerDisplayName} ở {pending.SessionName} lại nha 😆 Ai khác vẫn có thể nhận.");
            }

            var normalized = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
            if (ZaloBotIntelligence.IsConfirmation(normalized) || PreDraftDonePattern.IsMatch(normalized))
                return await ConfirmClaimAsync(connectionId, groupId, incoming, pending, cancellationToken);
        }

        if (CancelOfferPattern.IsMatch(ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty)))
            return await CancelOwnedOfferAsync(groupId, senderId, incoming.Content, cancellationToken);

        if (!IsClaimPhrase(incoming.Content)) return new(false, null);

        var offers = await store.ListClaimableAsync(groupId, senderId, cancellationToken);
        if (offers.Count == 0) return new(false, null);

        // If the claimant explicitly mentions a human, only that human may be the
        // source owner. A side conversation such as "@Nam tui nhận xét..." must not
        // accidentally consume somebody else's open offer.
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

        var claimed = await store.TryClaimAsync(
            offer,
            senderId,
            CleanName(incoming.SenderName),
            incoming.MessageId,
            cancellationToken);
        if (!claimed)
            return new(true, "Slot vừa có người chạm trước rồi 😭 Tui refresh lại kèo nha.");

        var claimant = FriendlyName(incoming.SenderName);
        var ownerName = FriendlyName(owner.DisplayName);
        if (session.Status == SessionStatus.Finished)
        {
            return new(
                true,
                $"{claimant} hốt slot {ownerName} ở {session.Name} nha 😆 Chốt thì nói ‘chốt’ cái tui chuyển luôn.",
                ZaloBotIntent.SlotTransfer.ToString());
        }

        return new(
            true,
            $"{claimant} nhận slot {ownerName} ở {session.Name} nha 👌 Kèo chưa draft nên roster vẫn theo poll: {ownerName} bỏ vote, {claimant} vote {session.Name}. Xong nói ‘xong’ để tui check, tui không tự sửa poll đâu.",
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
        if (offer.Status == ZaloOpenSlotOfferStatus.Applying)
            return new(true, "Tui đang chốt slot này rồi, đợi xíu nha 😆", ZaloBotIntent.SlotTransfer.ToString());

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
            var claimantPresent = session.Players.Any(player =>
                player.IsPresent && CleanId(player.PlayerProfile?.ZaloUserId) == claimantId);
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
            ZaloBotIntent.SlotTransferConfirm.ToString());
    }

    private async Task<ZaloOpenSlotOfferHandleResult> CancelOwnedOfferAsync(
        string groupId,
        string senderId,
        string? content,
        CancellationToken cancellationToken)
    {
        var offers = await store.ListOwnedActiveAsync(groupId, senderId, cancellationToken);
        if (offers.Count == 0) return new(false, null);
        var referenced = offers.Where(offer => MatchesOfferReference(content, offer)).ToList();
        if (referenced.Count > 0) offers = referenced;
        if (offers.Count > 1)
        {
            var choices = string.Join(", ", offers.Take(4).Select(offer => offer.SessionName));
            return new(true, $"Huỷ pass kèo nào á? Tui đang thấy {choices}.");
        }

        var offer = offers[0];
        await store.CancelAsync(offer.Id, senderId, cancellationToken);
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

    private static SessionPlayer? ResolveOwner(MatchSession session, ZaloOpenSlotOfferSnapshot offer)
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

    private static string FriendlyName(string? value)
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
