using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloTeamPreferenceExitPendingPayload(
    ZaloTeamPreferenceExitPlan Plan,
    string PreviewResponse,
    bool Applied,
    string? AppliedResponse);

public sealed partial class ZaloOverbookService
{
    private const string TeamPreferenceExitPendingIntent = "TeamPreferenceExitConfirm";
    private const string TeamPreferenceExitApplyingIntent = "TeamPreferenceExitApplying";
    private const string TeamPreferenceExitAppliedIntent = "TeamPreferenceExitApplied";

    /// <summary>
    /// Consent-removal lane for an existing TeamPreference group.
    /// AI may only suggest meaning/target for a preview. Stable identity, group ownership,
    /// lifecycle, authorization, confirmation and mutation remain deterministic.
    /// </summary>
    private async Task<bool> TryHandleTeamPreferenceExitPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 ||
            string.IsNullOrWhiteSpace(incoming.Content))
            return false;

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
        var connection = connectionRows.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (connection is null) return false;

        var existingIncoming = await db.ZaloGroupMessages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connection.Id && item.MessageId == incoming.MessageId,
            cancellationToken);
        if (existingIncoming?.BotReplySentAt is not null &&
            (existingIncoming.SelectedIntent == TeamPreferenceExitPendingIntent ||
             existingIncoming.SelectedIntent == TeamPreferenceExitAppliedIntent))
            return true;

        var now = DateTimeOffset.UtcNow;
        var pending = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connection.Id &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);
        if (pending is not null && pending.ExpiresAt <= now && IsTeamPreferenceExitState(pending.PendingIntent))
        {
            db.ZaloBotConversationStates.Remove(pending);
            await db.SaveChangesAsync(cancellationToken);
            pending = null;
        }

        if (pending is not null && IsTeamPreferenceExitState(pending.PendingIntent))
        {
            if (await TryContinueTeamPreferenceExitAsync(
                    connection.Id,
                    connection.AccountZaloId,
                    connection.DisplayName,
                    groupId,
                    senderId,
                    pending,
                    incoming,
                    cancellationToken))
                return true;

            // Do not let a stale pending exit swallow a fresh unrelated command.
            if (!ZaloTeamPreferenceExitSemanticInterpreter.LooksPotentialExitLanguage(incoming.Content))
            {
                db.ZaloBotConversationStates.Remove(pending);
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }

        if (!ZaloTeamPreferenceExitSemanticInterpreter.LooksPotentialExitLanguage(incoming.Content))
            return false;

        var exitService = new ZaloTeamPreferenceExitService(db);
        var candidates = await exitService.LoadCandidatesAsync(
            connection.Id,
            groupId,
            senderId,
            cancellationToken);
        if (candidates.Count == 0)
            return false;

        var mentions = ExtractTeamPreferenceExitMentions(incoming, candidates);
        var directNegation = ZaloNaturalCommandParser.IsNegatedTeamPreference(incoming.Content);
        ZaloTeamPreferenceExitMeaningDecision semantic;
        var aiCalled = false;

        // Explicit negation + one authoritative member mention is safe to understand
        // deterministically. It still produces only a preview, never an immediate write.
        if (directNegation && mentions.Count == 1 &&
            candidates.Any(candidate => candidate.Members.Any(member =>
                member.ZaloUserId == ZaloOverbookLogic.NormalizeId(mentions[0].ZaloUserId))))
        {
            semantic = new(
                ZaloTeamPreferenceExitMeaningKind.SuggestExit,
                1,
                ZaloOverbookLogic.NormalizeId(mentions[0].ZaloUserId),
                mentions[0].DisplayName,
                null,
                "deterministic_explicit_negation");
        }
        else
        {
            var recentIds = await LoadTeamPreferenceExitRecentMessageIdsAsync(
                connection.Id,
                groupId,
                incoming.MessageId,
                cancellationToken);
            var context = await ZaloReadOnlyConversationContextLoader.LoadAsync(
                db,
                connection.Id,
                groupId,
                incoming,
                recentIds,
                8,
                cancellationToken);
            aiCalled = true;
            semantic = await new ZaloTeamPreferenceExitSemanticInterpreter(configuration, logger)
                .InterpretAsync(
                    connection.Id,
                    groupId,
                    senderId,
                    incoming.Content,
                    context,
                    candidates,
                    mentions,
                    cancellationToken);
        }

        if (semantic.Kind != ZaloTeamPreferenceExitMeaningKind.SuggestExit || semantic.Confidence < .65)
        {
            if (!directNegation) return false;

            var guidance = mentions.Count == 0
                ? "Mình hiểu bạn đang muốn đổi yêu cầu chung team, nhưng chưa xác định chắc người nào. Hãy @mention đúng người hoặc nói ‘tui muốn rời nhóm chung team’; chưa có dữ liệu nào bị đổi."
                : "Mình hiểu bạn đang muốn đổi yêu cầu chung team nhưng chưa đủ chắc để chọn đúng nhóm/người. Nói rõ hơn giúp mình; chưa có dữ liệu nào bị đổi.";
            await SendTeamPreferenceExitReplyAsync(
                connection.Id, connection.AccountZaloId, connection.DisplayName,
                groupId, incoming, guidance, TeamPreferenceExitPendingIntent,
                aiCalled, "needs_clarification", cancellationToken);
            return true;
        }

        var sessionSelector = semantic.SessionReference;
        if (candidates.Count > 1 && string.IsNullOrWhiteSpace(sessionSelector))
            sessionSelector = incoming.Content;
        var plan = exitService.BuildPlan(
            candidates,
            senderId,
            incoming.MessageId,
            semantic.TargetZaloUserId,
            semantic.TargetDisplayName,
            sessionSelector,
            aiCalled,
            semantic.Confidence,
            semantic.Reason,
            out var clarification);
        if (plan is null)
        {
            if (string.IsNullOrWhiteSpace(clarification)) return false;
            await SendTeamPreferenceExitReplyAsync(
                connection.Id, connection.AccountZaloId, connection.DisplayName,
                groupId, incoming, clarification!, TeamPreferenceExitPendingIntent,
                aiCalled, "grounding_clarification", cancellationToken);
            return true;
        }

        var preview = BuildTeamPreferenceExitPreview(plan, semantic.TargetDisplayName);
        await SaveTeamPreferenceExitPendingAsync(
            connection.Id,
            groupId,
            senderId,
            new ZaloTeamPreferenceExitPendingPayload(plan, preview, false, null),
            cancellationToken);
        await SendTeamPreferenceExitReplyAsync(
            connection.Id, connection.AccountZaloId, connection.DisplayName,
            groupId, incoming, preview, TeamPreferenceExitPendingIntent,
            aiCalled, "preview", cancellationToken);
        return true;
    }

    private async Task<bool> TryContinueTeamPreferenceExitAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        string senderId,
        ZaloBotConversationState pending,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        ZaloTeamPreferenceExitPendingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ZaloTeamPreferenceExitPendingPayload>(pending.PendingPayloadJson);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null)
        {
            db.ZaloBotConversationStates.Remove(pending);
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (!payload.Applied && string.Equals(payload.Plan.SourceMessageId, incoming.MessageId, StringComparison.Ordinal))
        {
            await SendTeamPreferenceExitReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                payload.PreviewResponse, TeamPreferenceExitPendingIntent,
                payload.Plan.AiInterpreted, "preview", cancellationToken);
            return true;
        }

        if (ZaloBotIntelligence.IsCancel(incoming.Content))
        {
            db.ZaloBotConversationStates.Remove(pending);
            await db.SaveChangesAsync(cancellationToken);
            await SendTeamPreferenceExitReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                "Ok, mình huỷ preview tách nhóm chung team. Chưa có dữ liệu nào bị đổi.",
                TeamPreferenceExitPendingIntent,
                false,
                "cancelled",
                cancellationToken);
            return true;
        }

        var confirmed = ZaloBotIntelligence.IsConfirmation(incoming.Content) ||
                        ZaloAmbientLeasePendingContinuationPolicy.IsStrongConfirmation(incoming.Content);
        if (!confirmed)
            return false;

        if (payload.Applied && !string.IsNullOrWhiteSpace(payload.AppliedResponse))
        {
            await SendTeamPreferenceExitReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                payload.AppliedResponse!, TeamPreferenceExitAppliedIntent,
                payload.Plan.AiInterpreted, "applied_replay", cancellationToken);
            await db.ZaloBotConversationStates
                .Where(item => item.Id == pending.Id && item.PendingIntent == TeamPreferenceExitAppliedIntent)
                .ExecuteDeleteAsync(cancellationToken);
            return true;
        }

        var claimed = await db.ZaloBotConversationStates
            .Where(item => item.Id == pending.Id && item.PendingIntent == TeamPreferenceExitPendingIntent)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PendingIntent, TeamPreferenceExitApplyingIntent)
                .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (claimed == 0)
            return true;

        var history = new ZaloBotActionHistoryService(db, NullLogger<ZaloBotActionHistoryService>.Instance);
        var before = await history.CaptureAsync(payload.Plan.SessionId, cancellationToken);
        var applied = await new ZaloTeamPreferenceExitService(db)
            .ApplyAsync(senderId, payload.Plan, cancellationToken);
        if (!applied.IsSuccess || applied.Value is null)
        {
            await db.ZaloBotConversationStates
                .Where(item => item.Id == pending.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await SendTeamPreferenceExitReplyAsync(
                connectionId, accountId, botName, groupId, incoming,
                applied.Error ?? "Nhóm chung team đã thay đổi nên mình không áp dụng preview cũ. Hãy gửi lại yêu cầu.",
                TeamPreferenceExitPendingIntent,
                payload.Plan.AiInterpreted,
                "apply_rejected",
                cancellationToken);
            return true;
        }

        await history.RecordAsync(
            payload.Plan.SessionId,
            incoming.SenderId,
            incoming.SenderName,
            "TeamPreferenceExit",
            payload.Plan.Action == ZaloTeamPreferenceExitAction.RemoveSelf
                ? $"{payload.Plan.SenderDisplayName} tự rời nhóm chung team trong {payload.Plan.SessionName}"
                : $"{payload.Plan.SenderDisplayName} bỏ {payload.Plan.RemoveDisplayName} khỏi nhóm chung team trong {payload.Plan.SessionName}",
            before,
            cancellationToken);

        var response = BuildTeamPreferenceExitAppliedResponse(payload.Plan, applied.Value);
        var appliedPayload = payload with { Applied = true, AppliedResponse = response };
        var appliedPayloadJson = JsonSerializer.Serialize(appliedPayload);
        var appliedAt = DateTimeOffset.UtcNow;
        var appliedExpiresAt = appliedAt.AddMinutes(10);
        await db.ZaloBotConversationStates
            .Where(item => item.Id == pending.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PendingIntent, TeamPreferenceExitAppliedIntent)
                .SetProperty(item => item.PendingPayloadJson, appliedPayloadJson)
                .SetProperty(item => item.ExpiresAt, appliedExpiresAt)
                .SetProperty(item => item.UpdatedAt, appliedAt), cancellationToken);

        var sent = await TrySendTeamPreferenceExitReplyAsync(
            connectionId, accountId, botName, groupId, incoming,
            response, TeamPreferenceExitAppliedIntent,
            payload.Plan.AiInterpreted, "applied", cancellationToken);
        if (sent)
        {
            await db.ZaloBotConversationStates
                .Where(item => item.Id == pending.Id && item.PendingIntent == TeamPreferenceExitAppliedIntent)
                .ExecuteDeleteAsync(cancellationToken);
        }
        return true;
    }

    private async Task SaveTeamPreferenceExitPendingAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloTeamPreferenceExitPendingPayload payload,
        CancellationToken cancellationToken)
    {
        var state = await db.ZaloBotConversationStates.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId &&
            item.GroupId == groupId &&
            item.SenderZaloUserId == senderId,
            cancellationToken);
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = senderId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotConversationStates.Add(state);
        }

        state.PendingIntent = TeamPreferenceExitPendingIntent;
        state.PendingPayloadJson = JsonSerializer.Serialize(payload);
        state.PreviousCommand = "TeamPreferenceExit";
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:ConversationTtlMinutes", 15), 1, 120));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task SendTeamPreferenceExitReplyAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string text,
        string intent,
        bool aiCalled,
        string outcome,
        CancellationToken cancellationToken) =>
        TrySendTeamPreferenceExitReplyAsync(
            connectionId, accountId, botName, groupId, incoming,
            text, intent, aiCalled, outcome, cancellationToken);

    private async Task<bool> TrySendTeamPreferenceExitReplyAsync(
        string connectionId,
        string accountId,
        string botName,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string text,
        string intent,
        bool aiCalled,
        string outcome,
        CancellationToken cancellationToken)
    {
        var stored = await EnsureV2IncomingMessageAsync(connectionId, groupId, incoming, cancellationToken);
        if (stored.BotReplySentAt is not null) return true;

        var idempotencyKey = $"team-preference-exit:{accountId}:{incoming.MessageId}:{outcome}";
        try
        {
            var send = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                text,
                [],
                idempotencyKey: idempotencyKey);
            if (!send.Sent)
                throw new InvalidOperationException("Zalo bridge did not confirm TeamPreference exit reply.");

            var providerReplyId = NormalizeProviderMessageId(send.MessageId);
            var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
            await EnsureV2OutboundMessageAsync(
                connectionId,
                groupId,
                persistedReplyId,
                accountId,
                botName,
                text,
                cancellationToken);
            if (providerReplyId is not null)
            {
                await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                    connectionId,
                    groupId,
                    providerReplyId,
                    incoming.MessageId,
                    cancellationToken);
            }

            var repliedAt = DateTimeOffset.UtcNow;
            await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == connectionId && item.MessageId == incoming.MessageId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.BotReplySentAt, repliedAt)
                    .SetProperty(item => item.SelectedIntent, intent)
                    .SetProperty(item => item.AiCalled, aiCalled)
                    .SetProperty(item => item.ReplyOutcome, $"team_preference_exit_{outcome}")
                    .SetProperty(item => item.ProcessingToken, (string?)null), cancellationToken);

            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    incoming.MessageId,
                    groupId,
                    ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                    incoming.MentionedBot ? "ExplicitMention" : "AmbientGroundedTeamPreferenceExit",
                    IntentSource: aiCalled ? "SemanticAiPreviewOnly" : "DeterministicPreRouting",
                    Intent: intent,
                    Confidence: 1,
                    AiCalled: aiCalled,
                    FallbackReason: outcome,
                    ReplyMessageId: persistedReplyId),
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not send TeamPreference exit reply Group={GroupId} Message={MessageId} Outcome={Outcome}",
                groupId,
                incoming.MessageId,
                outcome);
            await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == connectionId && item.MessageId == incoming.MessageId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.SelectedIntent, intent)
                    .SetProperty(item => item.AiCalled, aiCalled)
                    .SetProperty(item => item.ReplyOutcome, "team_preference_exit_send_failed")
                    .SetProperty(item => item.ProcessingToken, (string?)null), cancellationToken);
            return false;
        }
    }

    private static string BuildTeamPreferenceExitPreview(
        ZaloTeamPreferenceExitPlan plan,
        string? semanticTargetName)
    {
        var remaining = plan.RemainingMemberNames.Count >= 2
            ? $" Nhóm còn lại nếu áp dụng: {string.Join(", ", plan.RemainingMemberNames)}."
            : " Nếu áp dụng, ràng buộc nhóm chung team này sẽ được xoá vì còn dưới 2 người.";

        if (plan.Action == ZaloTeamPreferenceExitAction.RemoveTarget)
        {
            return $"Mình hiểu bạn có vẻ không muốn tiếp tục chung team với {plan.RemoveDisplayName} trong {plan.SessionName}. " +
                   $"Nếu đúng, mình sẽ bỏ {plan.RemoveDisplayName} khỏi nhóm chung team của bạn.{remaining} " +
                   "Mình chưa đổi dữ liệu. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ.";
        }

        var target = string.IsNullOrWhiteSpace(semanticTargetName)
            ? string.Empty
            : $" với {semanticTargetName}";
        var authorityNote = plan.SenderHadProvenance
            ? string.Empty
            : " Mình không tự kick người khác chỉ từ câu chat này; quyền chắc chắn của bạn là rút chính mình.";
        return $"Mình hiểu bạn có vẻ không muốn tiếp tục ràng buộc chung team{target} trong {plan.SessionName}." +
               authorityNote +
               $" Nếu đúng, mình sẽ tách chính bạn ({plan.SenderDisplayName}) khỏi nhóm.{remaining} " +
               "Mình chưa đổi dữ liệu. Gõ @bot xác nhận để áp dụng hoặc @bot huỷ.";
    }

    private static string BuildTeamPreferenceExitAppliedResponse(
        ZaloTeamPreferenceExitPlan plan,
        ZaloTeamPreferenceExitApplyResult result)
    {
        if (result.GroupDeleted)
        {
            return $"Đã tách {result.RemovedDisplayName} khỏi yêu cầu chung team trong {plan.SessionName}. " +
                   "Nhóm cũ không còn đủ 2 người nên ràng buộc chung team đã được xoá.";
        }

        return $"Đã tách {result.RemovedDisplayName} khỏi nhóm chung team trong {plan.SessionName}. " +
               $"Nhóm còn lại: {string.Join(", ", result.RemainingMemberNames)}.";
    }

    private static List<ZaloMentionedUser> ExtractTeamPreferenceExitMentions(
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<ZaloTeamPreferenceExitCandidate> candidates)
    {
        var botIds = new HashSet<string>(StringComparer.Ordinal)
        {
            ZaloOverbookLogic.NormalizeId(incoming.BotId),
            ZaloOverbookLogic.NormalizeId(incoming.AccountId)
        };
        var members = candidates.SelectMany(candidate => candidate.Members).ToList();
        var result = new List<ZaloMentionedUser>();
        foreach (var mention in incoming.Mentions ?? [])
        {
            var uid = ZaloOverbookLogic.NormalizeId(mention.Uid);
            if (uid.Length == 0 || botIds.Contains(uid)) continue;
            var canonical = members.FirstOrDefault(member => member.ZaloUserId == uid)?.DisplayName;
            var label = canonical ?? ExtractTeamPreferenceExitMentionLabel(incoming.Content, mention);
            if (string.IsNullOrWhiteSpace(label)) label = uid;
            if (result.All(item => ZaloOverbookLogic.NormalizeId(item.ZaloUserId) != uid))
                result.Add(new ZaloMentionedUser(uid, label));
        }
        return result;
    }

    private static string ExtractTeamPreferenceExitMentionLabel(string? content, ZaloBridgeMention mention)
    {
        var value = content ?? string.Empty;
        if (mention.Pos >= 0 && mention.Len > 0 && mention.Pos + mention.Len <= value.Length)
            return value.Substring(mention.Pos, mention.Len).Trim().TrimStart('@');
        return string.Empty;
    }

    private async Task<IReadOnlyList<string>> LoadTeamPreferenceExitRecentMessageIdsAsync(
        string connectionId,
        string groupId,
        string currentMessageId,
        CancellationToken cancellationToken)
    {
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId)
            .Select(item => new { item.MessageId, item.SentAt })
            .ToListAsync(cancellationToken);
        return rows
            .Where(item => !string.Equals(item.MessageId, currentMessageId, StringComparison.Ordinal))
            .OrderBy(item => item.SentAt)
            .TakeLast(12)
            .Select(item => item.MessageId)
            .ToList();
    }

    private static bool IsTeamPreferenceExitState(string? intent) =>
        intent == TeamPreferenceExitPendingIntent ||
        intent == TeamPreferenceExitApplyingIntent ||
        intent == TeamPreferenceExitAppliedIntent;
}
