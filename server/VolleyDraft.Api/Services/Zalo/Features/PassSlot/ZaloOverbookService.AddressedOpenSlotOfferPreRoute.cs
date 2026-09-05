using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Explicitly addressing NPC must not bypass the same grounded OpenSlotOffer
    /// lifecycle that handles ambient pass-slot coordination. Addressing decides
    /// whether NPC may engage; it must not replace a deterministic domain capability
    /// with GeneralChat/AI routing.
    /// </summary>
    internal async Task<bool> TryHandleAddressedOpenSlotOfferPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!incoming.MentionedBot) return false;

        var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        if (botId.Length == 0 || !incoming.Mentions.Any(mention =>
                string.Equals(
                    ZaloOverbookLogic.NormalizeId(mention.Uid),
                    botId,
                    StringComparison.Ordinal)))
            return false;

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

        // Reuse the canonical explicit-address extraction so the feature parser sees
        // the same question whether a member writes "tui nhận CN" or
        // "@Npc tui nhận CN". Human mention metadata stays intact for owner scoping.
        var question = ZaloBotService.ExtractQuestion(incoming);
        var promoted = incoming with { Content = question };

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

        // If this is a canonical OpenSlotOffer claim that names an actual session,
        // keep the degraded path deterministic even when no claimable offer remains.
        // This turns a stale rescue/late claim into a grounded clarification instead
        // of falling through to GeneralChat and exposing an AI-provider outage.
        if (!ZaloOpenSlotOfferService.IsClaimPhrase(question)) return false;

        // OpenSlotOffer state wins when it actually handled the turn above. Without
        // matching offer state, however, preserve any pre-existing deterministic V1
        // command owner (for example waitlist acceptance) instead of reinterpreting
        // the same wording as a stale marketplace claim.
        var existingDeterministic = ZaloBotIntelligence.ClassifyDeterministically(question);
        if (existingDeterministic.Intent != ZaloBotIntent.Unknown &&
            existingDeterministic.Intent != ZaloBotIntent.GeneralChat)
            return false;

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
        var matchedIds = ZaloBotIntelligence.SelectOperationalSessionCandidateIds(
            question,
            references);
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
}
