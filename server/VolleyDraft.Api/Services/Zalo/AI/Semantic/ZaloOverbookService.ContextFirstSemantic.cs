using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Semantic-first adapter for natural leader decisions that deterministic command
    /// parsing did not understand. AI only selects an intent + grounded session. The
    /// action is translated back into the existing deterministic draft-preparation
    /// lane, which rechecks live role, poll, roster fingerprint, profile and slot state.
    /// </summary>
    private async Task<bool> TryHandleContextFirstDraftPreparationAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        if (ambientSettings.ShadowMode ||
            !configuration.GetValue("ZaloBot:Semantic:DraftPreparation:Enabled", true))
            return false;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.BotEnabled &&
                item.ZaloConnection != null &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection) &&
                (item.StartTime == null ||
                 (item.StartTime > now && item.StartTime <= now.AddHours(36))))
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return false;

        // Avoid spending an AI call on ordinary members. Authorization is checked
        // again by the deterministic lane after interpretation, so this is only a
        // budget gate and never the final permission check.
        var authority = sessions
            .OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue)
            .First();
        var role = await integration.GetGroupRoleAuthorizationAsync(
            authority.AdminUserId,
            authority.Id,
            senderId);
        if (!role.IsSuccess || role.Value?.CanOperateBot != true) return false;

        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var snapshot = new List<ZaloDraftSemanticSessionSnapshot>(sessions.Count);
        foreach (var session in sessions.OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue))
        {
            var existing = await decisionStore.GetAsync(session.Id, cancellationToken);
            snapshot.Add(new ZaloDraftSemanticSessionSnapshot(
                session.Id,
                session.Name,
                session.StartTime,
                session.TeamCount,
                session.TeamSize,
                existing?.Kind.ToString(),
                existing?.EffectiveSlotCount));
        }

        var plan = await new ZaloDraftPreparationSemanticInterpreter(configuration, logger)
            .InterpretAsync(
                db,
                connectionId,
                groupId,
                incoming,
                snapshot,
                cancellationToken);
        if (plan is null || !plan.IsActionable) return false;

        MatchSession? selected = null;
        if (!string.IsNullOrWhiteSpace(plan.SessionId))
        {
            selected = sessions.SingleOrDefault(item =>
                string.Equals(item.Id, plan.SessionId, StringComparison.Ordinal));
        }
        else if (sessions.Count == 1)
        {
            selected = sessions[0];
        }

        if (selected is null)
        {
            return await TryReplyDraftPreparationAmbiguityAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                sessions.OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue).ToList(),
                "quyết định kèo",
                cancellationToken);
        }

        // Canonical text is routing metadata only. The existing deterministic lane
        // owns all mutation and reloads canonical DB/poll state before writing.
        var canonical = plan.Intent switch
        {
            ZaloDraftSemanticIntent.StopMatch => $"hủy kèo {selected.Name}",
            ZaloDraftSemanticIntent.KeepRecruiting => $"kiếm thêm {selected.Name}",
            ZaloDraftSemanticIntent.PlayCurrentRoster when plan.RequestedSlotCount is { } count =>
                $"chốt {count} {selected.Name}",
            ZaloDraftSemanticIntent.PlayCurrentRoster => $"vẫn đánh {selected.Name}",
            ZaloDraftSemanticIntent.StartDraft => $"draft đi {selected.Name}",
            _ => string.Empty
        };
        if (canonical.Length == 0) return false;

        logger.LogInformation(
            "Context-first draft semantic plan Group={GroupId} Message={MessageId} Intent={Intent} Session={SessionId} Confidence={Confidence}",
            groupId,
            incoming.MessageId,
            plan.Intent,
            selected.Id,
            plan.Confidence);

        var promoted = incoming with { Content = canonical };
        return await TryHandleDraftPreparationDecisionAsync(
            connectionId,
            groupId,
            senderId,
            promoted,
            ambientSettings,
            cancellationToken);
    }

    /// <summary>
    /// First-pass semantic collector for targeted profile conversations. Confident AI
    /// interpretations are handled here; everything else is left untouched for the
    /// hardened deterministic V2 parser, so rollout is additive and fail-closed.
    /// </summary>
    private async Task<int> ProcessMissingProfileRepliesContextFirstAsync(
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("ZaloBot:Semantic:MissingProfile:Enabled", true))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var promptStore = new ZaloMissingProfilePromptStore(db);
        var prompts = await promptStore.GetActiveAsync(now, 100, cancellationToken);
        if (prompts.Count == 0) return 0;

        var promptGroups = prompts
            .GroupBy(ProfileIdentityKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.PromptedAt).ThenBy(item => item.Id).ToList(),
                StringComparer.Ordinal);
        var sessionIds = prompts.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).ToList();
        var sessionNames = await db.MatchSessions
            .AsNoTracking()
            .Where(item => sessionIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name })
            .ToDictionaryAsync(item => item.Id, item => item.Name, StringComparer.Ordinal, cancellationToken);

        var interpreter = new ZaloProfileSemanticInterpreter(configuration, logger);
        var semanticCache = new Dictionary<string, ZaloProfileSemanticInterpretation?>(StringComparer.Ordinal);
        var handled = 0;

        foreach (var initialPrompt in prompts)
        {
            var prompt = initialPrompt;
            var session = await db.MatchSessions
                .AsNoTracking()
                .Include(item => item.ZaloConnection)
                .SingleOrDefaultAsync(item =>
                    item.Id == prompt.SessionId &&
                    item.ZaloConnectionId == prompt.ZaloConnectionId &&
                    item.ZaloGroupId == prompt.GroupId,
                    cancellationToken);
            if (session is null || session.ZaloConnection is null || !session.BotEnabled ||
                session.Status is SessionStatus.Drafting or SessionStatus.Finished or SessionStatus.Cancelled)
                continue;

            var player = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
            if (!PlayerStillMatchesPrompt(player, prompt)) continue;
            var currentMissing = GetMissingProfileFlags(player!);
            if (!currentMissing.Gender && !currentMissing.Role && !currentMissing.Level) continue;

            var messages = await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(message =>
                    message.ZaloConnectionId == prompt.ZaloConnectionId &&
                    message.GroupId == prompt.GroupId &&
                    !message.IsFromBot &&
                    message.SenderId == prompt.ZaloUserId &&
                    message.BotReplySentAt == null &&
                    message.SentAt > prompt.LastProcessedAt &&
                    message.SentAt >= prompt.PromptedAt &&
                    message.SentAt <= prompt.ExpiresAt)
                .Select(message => new
                {
                    message.Id,
                    message.MessageId,
                    message.Content,
                    message.SentAt,
                    message.ReceivedAt,
                    message.ReplyAttemptCount,
                    message.ProcessingToken,
                    message.ProcessingStartedAt
                })
                .ToListAsync(cancellationToken);
            var candidates = messages
                .OrderBy(item => item.SentAt)
                .ThenBy(item => item.ReceivedAt)
                .Take(20)
                .ToList();
            if (candidates.Count == 0) continue;

            var identityPrompts = promptGroups[ProfileIdentityKey(prompt)];
            var semanticPrompts = identityPrompts
                .Select(item => new ZaloProfileSemanticPromptSnapshot(
                    item.Id,
                    item.SessionId,
                    sessionNames.GetValueOrDefault(item.SessionId) ?? item.SessionId,
                    item.MissingGender,
                    item.MissingRole,
                    item.MissingLevel))
                .ToList();

            foreach (var message in candidates)
            {
                var cacheKey = $"{prompt.ZaloConnectionId}\u001f{prompt.GroupId}\u001f{prompt.ZaloUserId}\u001f{message.MessageId}";
                if (!semanticCache.TryGetValue(cacheKey, out var semantic))
                {
                    var incoming = new ZaloIncomingMessageEvent(
                        session.ZaloConnection.AccountZaloId,
                        string.Empty,
                        prompt.GroupId,
                        message.MessageId,
                        prompt.ZaloUserId,
                        prompt.DisplayName,
                        message.Content,
                        [],
                        false,
                        message.SentAt.ToUnixTimeMilliseconds(),
                        null);
                    semantic = await interpreter.InterpretAsync(
                        db,
                        prompt.ZaloConnectionId,
                        prompt.GroupId,
                        incoming,
                        semanticPrompts,
                        cancellationToken);
                    semanticCache[cacheKey] = semantic;
                }

                if (semantic is null || !semantic.IsUseful) continue;
                if (!string.IsNullOrWhiteSpace(semantic.SessionId) &&
                    !string.Equals(semantic.SessionId, prompt.SessionId, StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(semantic.SessionId) && identityPrompts.Count > 1)
                {
                    if (!string.Equals(prompt.Id, identityPrompts[0].Id, StringComparison.Ordinal)) continue;
                    var ambiguousClaim = await TryClaimProfileInputAsync(
                        message.Id,
                        message.ReplyAttemptCount,
                        message.ProcessingToken,
                        message.ProcessingStartedAt,
                        cancellationToken);
                    if (ambiguousClaim is null) continue;
                    try
                    {
                        var choices = semanticPrompts.Select(item => item.SessionName).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);
                        await SendProfileConversationReplyAsync(
                            session,
                            prompt,
                            message.MessageId,
                            $"Tui hiểu phần hồ sơ ông vừa nói rồi 👌 Nhưng đang có hơn một kèo ({string.Join(", ", choices)}), nên chưa dám gắn nhầm. Reply đúng tin của kèo đó hoặc nói tên/ngày kèo giúp tui nha.",
                            cancellationToken);
                        await MarkProfileInputHandledAsync(
                            message.Id,
                            "profile_semantic_session_ambiguous",
                            ambiguousClaim,
                            cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        handled += 1;
                    }
                    catch
                    {
                        await ReleaseProfileInputClaimAsync(message.Id, ambiguousClaim, cancellationToken);
                        throw;
                    }
                    continue;
                }

                var claim = await TryClaimProfileInputAsync(
                    message.Id,
                    message.ReplyAttemptCount,
                    message.ProcessingToken,
                    message.ProcessingStartedAt,
                    cancellationToken);
                if (claim is null) continue;

                try
                {
                    if (semantic.Route is ZaloProfileSemanticRoute.Defer or ZaloProfileSemanticRoute.Dismiss)
                    {
                        var response = semantic.Route == ZaloProfileSemanticRoute.Dismiss
                            ? $"Ok {prompt.DisplayName}, tui bỏ qua lượt hỏi hồ sơ này nha 👌 Dữ liệu hiện tại giữ nguyên."
                            : $"Ok {prompt.DisplayName}, để sau cũng được 👌 Tui dừng hỏi lượt này; nếu gần chốt vẫn thiếu thì mới nhắc lại nhẹ.";
                        await SendProfileConversationReplyAsync(session, prompt, message.MessageId, response, cancellationToken);
                        await MarkProfileInputHandledAsync(
                            message.Id,
                            semantic.Route == ZaloProfileSemanticRoute.Dismiss ? "profile_semantic_dismissed" : "profile_semantic_deferred",
                            claim,
                            cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        await promptStore.CompleteAsync(prompt.Id, message.SentAt, cancellationToken);
                        handled += 1;
                        break;
                    }

                    var freshPlayer = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
                    if (!PlayerStillMatchesPrompt(freshPlayer, prompt))
                    {
                        await MarkProfileInputHandledAsync(message.Id, "profile_semantic_context_stale", claim, cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        handled += 1;
                        break;
                    }

                    var freshMissing = GetMissingProfileFlags(freshPlayer!);
                    var gender = freshMissing.Gender ? semantic.Gender : null;
                    var roleValue = freshMissing.Role ? semantic.Role : null;
                    var level = freshMissing.Level ? semantic.Level : null;
                    if (gender is null && roleValue is null && level is null)
                    {
                        await SendProfileConversationReplyAsync(
                            session,
                            prompt,
                            message.MessageId,
                            $"Tui hiểu ý ông, nhưng phần đó hồ sơ đã có rồi. Hiện chỉ còn {BuildProfileMissingHint(freshMissing.Gender, freshMissing.Role, freshMissing.Level)}",
                            cancellationToken);
                        await MarkProfileInputHandledAsync(message.Id, "profile_semantic_no_missing_value", claim, cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        handled += 1;
                        continue;
                    }

                    var history = new ZaloBotActionHistoryService(db, NullLogger<ZaloBotActionHistoryService>.Instance);
                    var before = await history.CaptureAsync(session.Id, cancellationToken);
                    var updated = await new SessionDraftService(db).UpdatePlayerProfileFromBotAsync(
                        session.AdminUserId,
                        session.Id,
                        freshPlayer!.DisplayName,
                        gender,
                        roleValue,
                        level,
                        prompt.ZaloUserId,
                        prompt.SessionPlayerId);
                    if (!updated.IsSuccess || updated.Value is null)
                    {
                        await MarkProfileInputHandledAsync(message.Id, "profile_semantic_update_blocked", claim, cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        handled += 1;
                        continue;
                    }

                    await history.RecordAsync(
                        session.Id,
                        prompt.ZaloUserId,
                        prompt.DisplayName,
                        "UpdateOwnProfile",
                        $"{prompt.DisplayName} tự bổ sung hồ sơ qua semantic conversation Zalo",
                        before,
                        cancellationToken);

                    var refreshed = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
                    if (refreshed is null)
                    {
                        await MarkProfileInputHandledAsync(message.Id, "profile_semantic_updated_context_removed", claim, cancellationToken);
                        await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                        await promptStore.CompleteAsync(prompt.Id, message.SentAt, cancellationToken);
                        handled += 1;
                        break;
                    }

                    var missing = GetMissingProfileFlags(refreshed);
                    var completed = !missing.Gender && !missing.Role && !missing.Level;
                    var accepted = FormatSemanticProfileValues(gender, roleValue, level);
                    var reply = completed
                        ? $"Ok {prompt.DisplayName} 😎 tui hiểu và ghi {string.Join(" · ", accepted)} rồi. Hồ sơ kèo {session.Name} xong."
                        : $"Ok {prompt.DisplayName}, tui hiểu và ghi {string.Join(" · ", accepted)} rồi 👌 Còn {BuildProfileMissingHint(missing.Gender, missing.Role, missing.Level)} Cứ nói tiếp bình thường nha.";
                    await SendProfileConversationReplyAsync(session, prompt, message.MessageId, reply, cancellationToken);
                    await MarkProfileInputHandledAsync(message.Id, "profile_semantic_updated", claim, cancellationToken);
                    await MarkProfileSemanticAuditAsync(message.Id, cancellationToken);
                    await promptStore.UpdateProgressAsync(
                        prompt.Id,
                        missing.Gender,
                        missing.Role,
                        missing.Level,
                        message.SentAt,
                        completed,
                        cancellationToken);
                    handled += 1;
                    if (completed) break;
                }
                catch
                {
                    await ReleaseProfileInputClaimAsync(message.Id, claim, cancellationToken);
                    throw;
                }
            }
        }

        return handled;
    }

    private async Task MarkProfileSemanticAuditAsync(string rowId, CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages
            .Where(item => item.Id == rowId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.AiCalled, true)
                .SetProperty(item => item.SelectedIntent, "UpdateOwnProfileSemantic"),
                cancellationToken);
    }

    private static IReadOnlyList<string> FormatSemanticProfileValues(
        PlayerGender? gender,
        PlayerRole? role,
        PlayerLevel? level)
    {
        var accepted = new List<string>();
        if (gender is not null) accepted.Add(gender == PlayerGender.Female ? "nữ" : "nam");
        if (role is not null) accepted.Add(role switch
        {
            PlayerRole.Attack => "công",
            PlayerRole.Defense => "thủ",
            PlayerRole.Setter => "chuyền 2",
            PlayerRole.FullStack => "toàn diện",
            _ => role.ToString()
        });
        if (level is not null) accepted.Add(level switch
        {
            PlayerLevel.Good => "tốt",
            PlayerLevel.Average => "trung bình",
            PlayerLevel.New => "mới chơi",
            _ => level.ToString()
        });
        return accepted;
    }
}