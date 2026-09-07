using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Beginner-safe deterministic help for pass/share-slot questions. This lane is
    /// informational only: it never opens an offer, claims a slot, changes poll/roster,
    /// or calls AI. Explicit bot addressing/reply ownership is required so ordinary
    /// group discussion about slot workflows does not wake NPC.
    /// </summary>
    internal async Task<bool> TryHandlePassSlotGuidancePreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var guidance = TryBuildAddressedSlotWorkflowGuidance(incoming);
        if (guidance is null) return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return false;

        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == groupId))
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

        await SendDeterministicPreRouteResponseAsync(
            connection.Id,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            guidance.Text,
            guidance.Intent == ZaloBotIntent.ShareSlot ? "ShareSlotGuidance" : "PassSlotGuidance",
            cancellationToken);
        return true;
    }

    internal static ZaloSlotWorkflowGuidanceResult? TryBuildAddressedSlotWorkflowGuidance(
        ZaloIncomingMessageEvent incoming)
    {
        if (!IsExplicitlyAddressedToBot(incoming)) return null;

        var question = incoming.MentionedBot
            ? ZaloBotService.ExtractQuestion(incoming)
            : incoming.Content ?? string.Empty;
        return ZaloSlotWorkflowGuidance.TryBuild(question);
    }

    private static bool IsExplicitlyAddressedToBot(ZaloIncomingMessageEvent incoming)
    {
        if (incoming.MentionedBot)
        {
            var botId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
            return botId.Length > 0 && incoming.Mentions.Any(mention =>
                string.Equals(
                    ZaloOverbookLogic.NormalizeId(mention.Uid),
                    botId,
                    StringComparison.Ordinal));
        }

        var quotedSenderId = ZaloOverbookLogic.NormalizeId(incoming.Quote?.SenderId);
        var currentBotId = ZaloOverbookLogic.NormalizeId(incoming.BotId);
        return currentBotId.Length > 0 &&
               string.Equals(currentBotId, quotedSenderId, StringComparison.Ordinal);
    }
}
