using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Bridges a read-only ambient TeamPreference proposal into the existing,
/// explicitly-confirmed TeamPreference write path.
///
/// The V2 proposal itself never grants write authority. A handoff is allowed only
/// for a deterministic confirmation by the same sender when either:
/// 1) the user replies to the exact provider message that presented the proposal, or
/// 2) the turn is explicitly addressed to the bot. The no-mention lease path may
/// synthesize that address metadata only after its own latest-proposal checks.
/// </summary>
public sealed class ZaloAmbientTeamPreferenceHandoff(VolleyDraftDbContext db)
{
    public const string ProposalIntent = "AmbientTeamPreferenceProposal";

    /// <summary>
    /// Validates a trusted confirmation and promotes the V2 proposal into the legacy
    /// TeamPreferenceConfirm envelope consumed by ZaloBotService in the same webhook.
    /// Returning true means a trusted handoff envelope was created; the caller must
    /// still let the normal bot handler consume this same inbound message.
    /// </summary>
    public async Task<bool> TryPromoteExactReplyConfirmationAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var accountId = Clean(incoming.AccountId, 100);
        var groupId = Clean(incoming.GroupId, 100);
        var senderId = Clean(incoming.SenderId, 100);
        var botId = Clean(incoming.BotId, 100);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || botId.Length == 0)
            return false;

        var normalized = ZaloBotIntelligence.Normalize(incoming.Content);
        if (!ZaloBotIntelligence.IsConfirmation(normalized) &&
            !ZaloAmbientLeasePendingContinuationPolicy.IsStrongConfirmation(normalized))
            return false;

        var quotedMessageId = Clean(incoming.Quote?.MessageId, 160);
        var quotedSenderId = Clean(incoming.Quote?.SenderId, 100);
        var exactReply = quotedMessageId.Length > 0 &&
                         string.Equals(quotedSenderId, botId, StringComparison.Ordinal);
        var explicitlyAddressed = incoming.MentionedBot && incoming.Mentions.Any(mention =>
            string.Equals(Clean(mention.Uid, 100), botId, StringComparison.Ordinal));
        if (!exactReply && !explicitlyAddressed)
            return false;

        // Match the same active connection selection semantics used by V2 pre-routing,
        // without pushing DateTimeOffset ORDER BY into SQLite.
        var connectionRows = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId &&
                           item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == groupId))
            .Select(item => new { item.Id, item.UpdatedAt })
            .ToListAsync(cancellationToken);
        var connectionId = connectionRows
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(connectionId)) return false;

        var store = new ZaloConversationStateV2Store(db);
        var state = await store.LoadActiveAsync(groupId, senderId, cancellationToken);
        if (state is null || !string.Equals(state.Intent, ProposalIntent, StringComparison.Ordinal))
            return false;

        var proposalSourceMessageId = Clean(state.LastMessageId, 160);
        if (proposalSourceMessageId.Length == 0) return false;

        // An exact reply must bind to the provider BotReply edge for this exact
        // proposal source. An explicitly addressed confirmation still has to prove
        // that the current proposal was actually presented to this sender; its address
        // authority comes from native mention or the separately verified lease policy.
        var providerProposalReplyId = Clean(await new ZaloMessageGraphQuery(db)
            .LoadBotReplyMessageIdAsync(
                connectionId,
                groupId,
                proposalSourceMessageId,
                cancellationToken), 160);
        if (exactReply)
        {
            if (providerProposalReplyId.Length == 0 ||
                !string.Equals(providerProposalReplyId, quotedMessageId, StringComparison.Ordinal))
                return false;
        }
        else
        {
            var proposalSource = await db.ZaloGroupMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ZaloConnectionId == connectionId &&
                    item.GroupId == groupId &&
                    item.MessageId == proposalSourceMessageId &&
                    item.SenderId == senderId &&
                    !item.IsFromBot,
                    cancellationToken);
            if (proposalSource?.BotReplySentAt is null)
                return false;
        }

        var collected = ParseObject(state.CollectedArgumentsJson);
        if (collected is null) return false;

        var requesterId = GetString(collected, "requesterZaloUserId", 100);
        var requesterName = GetString(collected, "requesterDisplayName", 160);
        var partnerId = GetString(collected, "partnerZaloUserId", 100);
        var partnerName = GetString(collected, "partnerDisplayName", 160);
        var sessionId = GetString(collected, "sessionId", 100);

        if (requesterId.Length == 0 ||
            !string.Equals(requesterId, senderId, StringComparison.Ordinal) ||
            requesterName.Length == 0 || partnerId.Length == 0 || partnerName.Length == 0 || sessionId.Length == 0)
            return false;

        var session = await db.MatchSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == sessionId &&
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.Status != SessionStatus.Cancelled &&
                item.Status != SessionStatus.Drafting &&
                item.Status != SessionStatus.Finished,
                cancellationToken);
        if (session is null) return false;

        // Rebuild the preview from stable Zalo UIDs at confirmation time. This
        // catches poll/roster/share/preference changes between proposal and confirm.
        var preview = await new SessionDraftService(db).PreviewTeamPreferenceGroupFromBotAsync(
            session.AdminUserId,
            session.Id,
            [
                new ShareSlotParticipantInput(requesterName, requesterId),
                new ShareSlotParticipantInput(partnerName, partnerId)
            ]);
        if (!preview.IsSuccess || preview.Value is null || !preview.Value.IsFeasible)
            return false;

        var now = DateTimeOffset.UtcNow;
        var pendingExpiry = new[] { state.ExpiresAt, now.AddSeconds(30) }.Min();
        if (pendingExpiry <= now) return false;

        // Never overwrite an unrelated live legacy confirmation. Trusted proposal
        // authorization must not silently destroy another pending domain workflow.
        var legacy = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);
        if (legacy is not null && legacy.ExpiresAt > now &&
            !string.Equals(legacy.PendingIntent, ZaloBotIntent.TeamPreferenceConfirm.ToString(), StringComparison.Ordinal))
            return false;

        if (legacy is null)
        {
            legacy = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = senderId,
                CreatedAt = now
            };
            db.ZaloBotConversationStates.Add(legacy);
        }

        legacy.PendingIntent = ZaloBotIntent.TeamPreferenceConfirm.ToString();
        legacy.PendingPayloadJson = JsonSerializer.Serialize(new
        {
            SessionId = session.Id,
            Plan = preview.Value,
            SelfService = true
        });
        legacy.PreviousCommand = $"{ZaloBotIntent.TeamPreference}:{(exactReply ? "ExactReply" : "Addressed")}:" +
                                 Clean(incoming.MessageId, 160);
        legacy.ExpiresAt = pendingExpiry;
        legacy.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        // The authorization token is one-shot. The legacy pending row above exists
        // only so the already-tested atomic ZaloBotService path can consume this same
        // webhook and record the normal domain action history.
        await store.CompleteAsync(groupId, senderId, cancellationToken);

        await new ZaloBotTraceStore(db).WriteAsync(
            new ZaloBotTraceEntry(
                MessageId: Clean(incoming.MessageId, 160),
                GroupId: groupId,
                SenderZaloUserId: senderId,
                AddressReason: exactReply
                    ? "ExactProposalReplyConfirmation"
                    : "AddressedProposalConfirmation",
                IntentSource: "AmbientProposalHandoff",
                Intent: ZaloBotIntent.TeamPreferenceConfirm.ToString(),
                Confidence: 1,
                QuotedMessageId: exactReply ? quotedMessageId : null,
                PendingStateBefore: ProposalIntent,
                PendingStateAfter: ZaloBotIntent.TeamPreferenceConfirm.ToString(),
                ResolvedSessionId: session.Id,
                ResolvedPersonIdsJson: JsonSerializer.Serialize(new[] { requesterId, partnerId }),
                AiCalled: false),
            cancellationToken);

        return true;
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonObject obj, string propertyName, int maxLength)
    {
        try
        {
            return Clean(obj[propertyName]?.GetValue<string>(), maxLength);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
