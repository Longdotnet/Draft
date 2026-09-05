using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Addressing decides whether NPC may engage; it must not replace the grounded
    /// OpenSlotOffer lifecycle with GeneralChat/AI routing. A deliberately narrow set
    /// of natural unmentioned claim phrases is also allowed when the backend has an
    /// active offer, so members can say "tui vô T6" without teaching a second domain
    /// implementation how slot ownership works.
    /// </summary>
    internal async Task<bool> TryHandleAddressedOpenSlotOfferPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var extractedQuestion = incoming.MentionedBot
            ? ZaloBotService.ExtractQuestion(incoming)
            : (incoming.Content ?? string.Empty).Trim();
        var hasNaturalClaim = TryPromoteNaturalOpenSlotClaim(extractedQuestion, out var naturalClaim);
        var hasNaturalPendingConfirmation = IsNaturalPendingClaimConfirmation(extractedQuestion);
        if (!incoming.MentionedBot && !hasNaturalClaim && !hasNaturalPendingConfirmation) return false;

        if (incoming.MentionedBot)
        {
            var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
            if (botId.Length == 0 || !incoming.Mentions.Any(mention =>
                    string.Equals(
                        ZaloOverbookLogic.NormalizeId(mention.Uid),
                        botId,
                        StringComparison.Ordinal)))
                return false;
        }

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return false;

        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session =>
                               session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new
            {
                item.Id,
                item.AccountZaloId,
                item.DisplayName,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var connection = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (connection is null) return false;

        var store = new ZaloOpenSlotOfferStore(db);
        var pending = await store.LoadPendingClaimAsync(
            connection.Id,
            groupId,
            senderId,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var hasLivePendingConversation = pending is not null &&
            (pending.Status == ZaloOpenSlotOfferStatus.Applying ||
             pending.Status == ZaloOpenSlotOfferStatus.ClaimPending &&
             (pending.ClaimExpiresAt is null || pending.ClaimExpiresAt > now));

        // A phrase such as "lấy cái đó" is too ambiguous to wake NPC by itself. It is
        // promoted to confirmation only while this sender has a live, group-scoped
        // OpenSlotOffer reservation. That state is durable, so restart/deploy does not
        // change the meaning of the follow-up.
        var question = hasNaturalPendingConfirmation && hasLivePendingConversation
            ? "chot"
            : hasNaturalClaim
                ? naturalClaim
                : extractedQuestion;
        var promoted = incoming with { Content = question };

        // Existing deterministic commands still own fresh turns. The only exception is
        // a live marketplace continuation already owned by this sender. This preserves
        // WaitlistAccept and other legacy/product semantics while making wording more
        // natural inside the OpenSlotOffer lifecycle.
        var existingDeterministic = ZaloBotIntelligence.ClassifyDeterministically(extractedQuestion);
        var hasExistingDeterministicOwner =
            existingDeterministic.Intent != ZaloBotIntent.Unknown &&
            existingDeterministic.Intent != ZaloBotIntent.GeneralChat;
        if (hasExistingDeterministicOwner && !hasLivePendingConversation)
            return false;

        // For an unmentioned natural claim, require actual authoritative marketplace
        // state before NPC participates. This prevents ordinary "tui vô" group chatter
        // from becoming a bot command when no pass-slot offer exists.
        if (!incoming.MentionedBot && hasNaturalClaim && !hasLivePendingConversation)
        {
            var claimable = await store.ListClaimableAsync(connection.Id, groupId, senderId, cancellationToken);
            if (claimable.Count == 0) return false;
        }

        var result = await new ZaloOpenSlotOfferService(db).TryHandleAsync(
            connection.Id,
            groupId,
            promoted,
            cancellationToken);
        if (result.Handled && !string.IsNullOrWhiteSpace(result.Response))
        {
            await SendDeterministicPreRouteResponseAsync(
                connection.Id,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                result.Response!,
                result.Intent,
                cancellationToken);
            return true;
        }

        if (hasExistingDeterministicOwner) return false;
        if (!ZaloOpenSlotOfferService.IsClaimPhrase(question)) return false;

        var sessionRows = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == connection.Id &&
                session.ZaloGroupId == groupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled)
            .Select(session => new
            {
                session.Id,
                session.Name,
                session.StartTime
            })
            .ToListAsync(cancellationToken);
        if (sessionRows.Count == 0) return false;

        var references = sessionRows
            .Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime))
            .ToList();
        var matchedIds = ZaloBotIntelligence.SelectOperationalSessionCandidateIds(question, references);
        var matched = sessionRows
            .Where(session => matchedIds.Contains(session.Id, StringComparer.Ordinal))
            .Take(2)
            .ToList();
        if (matched.Count != 1) return false;

        var sessionName = matched[0].Name;
        await SendDeterministicPreRouteResponseAsync(
            connection.Id,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            $"Tui hiểu ông đang muốn nhận slot ở {sessionName} 👌 Nhưng hiện tui không còn thấy slot pass nào đang mở khớp kèo đó. Có thể slot đã được nhận, hết hạn hoặc owner đã huỷ pass; nếu đang nói slot khác thì nói tên người hoặc kèo giúp tui nha.",
            ZaloBotIntent.SlotTransfer.ToString(),
            cancellationToken);
        return true;
    }

    internal static bool TryPromoteNaturalOpenSlotClaim(string? content, out string canonicalClaim)
    {
        canonicalClaim = string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        if (normalized.Length == 0) return false;

        var subjects = new[] { "tui", "toi", "minh", "em", "anh", "chi", "tao" };
        foreach (var subject in subjects)
        {
            foreach (var verb in new[] { "vo", "vao" })
            {
                var prefix = $"{subject} {verb}";
                if (TryBuildNaturalClaim(normalized, prefix, out canonicalClaim)) return true;
            }
        }

        foreach (var lead in new[] { "cho tui", "cho toi", "cho minh", "cho em", "de tui", "de toi", "de minh", "de em" })
        {
            foreach (var verb in new[] { "vo", "vao" })
            {
                var prefix = $"{lead} {verb}";
                if (TryBuildNaturalClaim(normalized, prefix, out canonicalClaim)) return true;
            }
        }

        return false;
    }

    private static bool TryBuildNaturalClaim(string normalized, string prefix, out string canonicalClaim)
    {
        canonicalClaim = string.Empty;
        if (string.Equals(normalized, prefix, StringComparison.Ordinal))
        {
            canonicalClaim = "tui nhan";
            return true;
        }

        if (!normalized.StartsWith(prefix + " ", StringComparison.Ordinal)) return false;
        var tail = normalized[(prefix.Length + 1)..].Trim();
        if (!IsNaturalSessionReference(tail)) return false;
        canonicalClaim = "tui nhan " + tail;
        return true;
    }

    private static bool IsNaturalSessionReference(string tail)
    {
        if (tail.Length == 0) return false;
        if (tail is "cn" or "chu nhat") return true;
        if (tail.StartsWith("cn ", StringComparison.Ordinal) ||
            tail.StartsWith("chu nhat ", StringComparison.Ordinal) ||
            tail.StartsWith("thu ", StringComparison.Ordinal) ||
            tail.StartsWith("slot ", StringComparison.Ordinal) ||
            tail.StartsWith("suat ", StringComparison.Ordinal) ||
            tail.StartsWith("keo ", StringComparison.Ordinal) ||
            tail.StartsWith("cua ", StringComparison.Ordinal))
            return true;
        return tail.Length >= 2 && tail[0] == 't' && tail[1] is >= '2' and <= '7' &&
               (tail.Length == 2 || char.IsWhiteSpace(tail[2]));
    }

    internal static bool IsNaturalPendingClaimConfirmation(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
        return normalized is "lay cai do" or "u lay cai do" or "uh lay cai do" or "ok lay cai do" or
            "chot cai do" or "ok chot cai do";
    }
}
