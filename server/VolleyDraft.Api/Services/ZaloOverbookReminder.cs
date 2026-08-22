using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var states = await store.GetEnabledAsync(cancellationToken);
        if (states.Count == 0) return 0;

        var refreshMinutes = Math.Clamp(configuration.GetValue("Scheduler:PollRefreshMinutes", 20), 10, 180);
        var sent = 0;
        foreach (var snapshot in states.Take(100))
        {
            var session = await db.MatchSessions
                .AsNoTracking()
                .Include(item => item.ZaloConnection)
                .SingleOrDefaultAsync(item => item.Id == snapshot.SessionId, cancellationToken);
            if (session is null || session.Status is SessionStatus.Cancelled or SessionStatus.Drafting or SessionStatus.Finished)
            {
                snapshot.NextReminderAt = null;
                snapshot.Enabled = session is not null && snapshot.Enabled;
                await store.SaveAsync(snapshot, cancellationToken);
                continue;
            }
            if (!session.BotEnabled || session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId))
                continue;

            var needsRefresh = snapshot.LastObservedAt is null ||
                               snapshot.LastObservedAt <= now.AddMinutes(-refreshMinutes) ||
                               snapshot.NextReminderAt is not null && snapshot.NextReminderAt <= now;
            if (needsRefresh)
            {
                try
                {
                    var synced = await integration.SyncLatestPollAsync(session.AdminUserId, session.Id);
                    if (!synced.IsSuccess)
                    {
                        snapshot.LastError = synced.Error ?? "Không đồng bộ được poll trước cảnh báo vượt slot.";
                        snapshot.NextReminderAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60));
                        await store.SaveAsync(snapshot, cancellationToken);
                        continue;
                    }
                    await ObserveAsync(session.Id, null, cancellationToken);
                }
                catch (Exception exception)
                {
                    snapshot.LastError = Truncate(exception.Message, 1000);
                    snapshot.NextReminderAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60));
                    await store.SaveAsync(snapshot, cancellationToken);
                    logger.LogWarning(exception, "Could not refresh overbook poll Session={SessionId}", session.Id);
                    continue;
                }
            }

            var state = await store.GetAsync(session.Id, cancellationToken);
            if (state is null || !state.Enabled || state.NeedsConfirmation || state.ExcessSlotCount <= 0 ||
                state.CurrentTargetVoterIds.Count == 0 || state.ReminderCount >= state.MaxReminders ||
                state.NextReminderAt is null || state.NextReminderAt > now)
                continue;

            try
            {
                var observation = await ReadObservationAsync(session.Id, cancellationToken);
                var targetIds = state.CurrentTargetVoterIds
                    .Where(id => observation.OrderedVoterIds.Contains(id, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (targetIds.Count == 0)
                {
                    RequireConfirmation(state, "TargetsNoLongerPresent");
                    await store.SaveAsync(state, cancellationToken);
                    continue;
                }

                var reminderNumber = state.ReminderCount + 1;
                var body = await BuildReminderBodyAsync(session, state, observation, reminderNumber, cancellationToken);
                var outgoing = BuildMentionMessage(targetIds, observation.DisplayNames, body);
                var idempotencyKey = $"overbook:{session.Id}:{state.IncidentKey ?? "unknown"}:{reminderNumber}";
                await bridge.SendGroupMessageAsync(
                    session.ZaloConnection!.AccountZaloId,
                    session.ZaloGroupId!,
                    outgoing.Message,
                    outgoing.Mentions,
                    idempotencyKey: idempotencyKey);

                state.ReminderCount = reminderNumber;
                state.LastReminderAt = now;
                state.NextReminderAt = reminderNumber >= state.MaxReminders
                    ? null
                    : now.AddMinutes(state.ReminderIntervalMinutes);
                state.LastError = null;
                await store.SaveAsync(state, cancellationToken);
                await SaveBotMessageAsync(session, idempotencyKey, outgoing.Message, now, cancellationToken);
                sent += 1;
            }
            catch (Exception exception)
            {
                state.LastError = Truncate(exception.Message, 1000);
                snapshot.NextReminderAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60));
                await store.SaveAsync(snapshot, cancellationToken);
                logger.LogWarning(exception, "Could not send overbook reminder Session={SessionId}", session.Id);
            }
        }
        return sent;
    }

    public async Task<bool> TryHandleZaloConfirmationAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        // Draft preparation decisions are deterministic domain state. Route them
        // before mention/Ambient gating so a live leader can naturally say
        // "15 vẫn đánh", "kiếm thêm", or the follow-up "draft đi" with or without @bot.
        // Slot-level "huỷ slot" is deliberately not matched by this lane.
        var draftAccountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var draftGroupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        if (draftAccountId.Length > 0 && draftGroupId.Length > 0)
        {
            var draftConnectionRows = await db.ZaloConnections
                .AsNoTracking()
                .Where(item => item.AccountZaloId == draftAccountId &&
                               item.MatchSessions.Any(session => session.BotEnabled && session.ZaloGroupId == draftGroupId))
                .Select(item => new { item.Id, item.AccountZaloId, item.DisplayName, item.UpdatedAt })
                .ToListAsync(cancellationToken);
            var draftConnection = draftConnectionRows.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (draftConnection is not null)
            {
                var ambientSettings = ZaloAmbientSettings.FromConfiguration(configuration);
                if (await TryHandleDraftPreparationDecisionAsync(
                        draftConnection.Id,
                        draftGroupId,
                        ZaloOverbookLogic.NormalizeId(incoming.SenderId),
                        incoming,
                        ambientSettings,
                        cancellationToken))
                    return true;

                // Draft-autopilot still owns its narrow natural-readiness/escalation
                // turns. The new preparation lane replaces only proactive scheduling,
                // not the existing interactive safety/approval router.
                if (await TryHandleDraftAutopilotAsync(
                        draftConnection.Id,
                        draftConnection.AccountZaloId,
                        draftConnection.DisplayName,
                        draftGroupId,
                        incoming,
                        cancellationToken))
                    return true;
            }
        }

        if (await TryHandleV2PreRoutingAsync(incoming, cancellationToken)) return true;
        if (await TryHandleRecruitmentGuestSingleLaneGateAsync(incoming, cancellationToken)) return true;
        if (!incoming.MentionedBot) return false;
        var normalized = ZaloBotIntelligence.Normalize(incoming.Content);
        var isConfirmation = normalized.Contains("xac nhan", StringComparison.Ordinal) &&
                             (normalized.Contains("vote du", StringComparison.Ordinal) ||
                              normalized.Contains("vuot slot", StringComparison.Ordinal) ||
                              normalized.Contains("slot du", StringComparison.Ordinal));
        if (!isConfirmation) return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.ZaloConnection)
            .Where(session => session.BotEnabled &&
                              session.ZaloConnection != null &&
                              session.ZaloConnection.AccountZaloId == accountId &&
                              session.ZaloGroupId == groupId &&
                              session.Status != SessionStatus.Cancelled &&
                              session.Status != SessionStatus.Finished)
            .OrderBy(session => session.StartTime ?? DateTimeOffset.MaxValue)
            .ToListAsync(cancellationToken);

        var pending = new List<(MatchSession Session, ZaloOverbookStateData State)>();
        foreach (var session in sessions)
        {
            var state = await store.GetAsync(session.Id, cancellationToken);
            if (state is not null && state.NeedsConfirmation && state.ExcessSlotCount > 0 && state.SuggestedTargetVoterIds.Count > 0)
                pending.Add((session, state));
        }
        if (pending.Count == 0) return false;
        if (pending.Count != 1) return false;
        var candidate = pending[0];
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var configuredOperators = ParseStringList(candidate.Session.BotOperatorZaloUserIdsJson);
        var canOperate = configuredOperators.Contains(senderId, StringComparer.Ordinal);
        if (!canOperate)
        {
            var role = await integration.GetGroupRoleAuthorizationAsync(candidate.Session.AdminUserId, candidate.Session.Id, senderId);
            canOperate = role.IsSuccess && role.Value?.CanOperateBot == true;
        }
        if (!canOperate) return false;

        var observation = await ReadObservationAsync(candidate.Session.Id, cancellationToken);
        var targets = candidate.State.SuggestedTargetVoterIds
            .Where(id => observation.OrderedVoterIds.Contains(id, StringComparer.Ordinal))
            .ToList();
        var validation = ValidateConfirmedTargets(observation, targets);
        if (validation is not null) return false;

        ApplyConfirmedTargets(candidate.State, targets, DateTimeOffset.UtcNow);
        await store.SaveAsync(candidate.State, cancellationToken);
        var names = targets.Select(id => observation.DisplayNames.GetValueOrDefault(id, id)).ToList();
        await bridge.SendGroupMessageAsync(
            candidate.Session.ZaloConnection!.AccountZaloId,
            candidate.Session.ZaloGroupId!,
            $"Đã xác nhận lượt vote dư của {candidate.Session.Name}: {string.Join(", ", names)}. Bot sẽ chỉ tag nhắc, không tự đưa ai vào waitlist và không tự xoá vote.",
            [],
            idempotencyKey: $"overbook-confirm:{candidate.Session.Id}:{candidate.State.IncidentKey}");
        return true;
    }

    private async Task<string> BuildReminderBodyAsync(
        MatchSession session,
        ZaloOverbookStateData state,
        OverbookObservation observation,
        int reminderNumber,
        CancellationToken cancellationToken)
    {
        var stage = ZaloOverbookMessageCatalog.GetStageName(reminderNumber);
        if (state.MessageSource == ZaloOverbookMessageSource.Ai && ai.IsConfigured)
        {
            var factual = $"Kèo {session.Name} đang vượt {state.ExcessSlotCount} slot ({state.EffectiveSlotCount}/{session.TeamCount * session.TeamSize}). Đây là lần nhắc {reminderNumber}. Những người được mention là lượt vote vượt slot. Vui lòng kiểm tra và bỏ vote dư để nhóm có thể chốt roster và draft. Không có hành động waitlist và bot không tự xoá vote.";
            var instruction = stage switch
            {
                ZaloOverbookMessageCatalog.StubbornStage => "Viết kiểu Gen Z cà khịa mạnh, thể hiện bot đã nhắc rất nhiều lần nhưng không xúc phạm cá nhân, không đe doạ.",
                ZaloOverbookMessageCatalog.SarcasticStage => "Viết kiểu Gen Z cà khịa/meme, vui nhưng rõ là cần bỏ vote dư ngay.",
                ZaloOverbookMessageCatalog.CalloutStage => "Viết lời réo tên rõ ràng hơn, hơi nghiêm túc nhưng vẫn thân thiện kiểu Gen Z.",
                _ => "Viết lời nhắc nhẹ nhàng, thân thiện kiểu Gen Z."
            };
            var rewritten = await ai.RewriteFactualAnswerAsync(
                new ZaloAiRewriteContext(
                    $"{instruction} Giữ nguyên số slot và số lần nhắc. Không nói sẽ chuyển waitlist, tự xoá vote hay tự đổi roster.",
                    "Những người vote vượt slot",
                    ZaloBotIntent.ScheduleReminder,
                    factual),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(rewritten) &&
                rewritten.Contains((session.TeamCount * session.TeamSize).ToString(), StringComparison.Ordinal) &&
                rewritten.Contains(state.EffectiveSlotCount.ToString(), StringComparison.Ordinal))
                return rewritten.Trim();
        }

        var useAdminPool = state.MessageSource == ZaloOverbookMessageSource.AdminPool;
        IReadOnlyList<string> pool;
        string tierPrefix;
        if (useAdminPool && ZaloOverbookMessageCatalog.TryGetAdvancedExactBank(state.ReminderMessageBanks, reminderNumber, out var exactBank))
        {
            pool = exactBank;
            tierPrefix = $"advanced-reminder-{reminderNumber}:";
        }
        else if (useAdminPool && ZaloOverbookMessageCatalog.TryGetCustomStageBank(state.ReminderMessageBanks, stage, out var stageBank))
        {
            pool = stageBank;
            tierPrefix = $"stage-custom-{stage}:";
        }
        else if (useAdminPool && state.ReminderMessageBanks.TryGetValue(reminderNumber, out var legacyBank) && legacyBank.Count > 0)
        {
            pool = legacyBank;
            tierPrefix = $"legacy-reminder-{reminderNumber}:";
        }
        else if (useAdminPool)
        {
            pool = ZaloOverbookMessageCatalog.GetDefaultStageBank(stage);
            tierPrefix = $"stage-default-{stage}:";
        }
        else
        {
            pool = stage switch
            {
                ZaloOverbookMessageCatalog.LightStage => DefaultFriendlyMessages,
                ZaloOverbookMessageCatalog.CalloutStage => DefaultSeriousMessages,
                _ => DefaultStrictMessages
            };
            tierPrefix = $"system-{stage}:";
        }

        var used = state.UsedMessageKeys.Where(key => key.StartsWith(tierPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var available = Enumerable.Range(0, pool.Count)
            .Where(index => !used.Contains($"{tierPrefix}{index}"))
            .ToList();
        if (available.Count == 0)
        {
            state.UsedMessageKeys.RemoveAll(key => key.StartsWith(tierPrefix, StringComparison.Ordinal));
            available = Enumerable.Range(0, pool.Count).ToList();
        }
        if (available.Count > 1 && state.LastMessageKey is not null)
            available.RemoveAll(index => $"{tierPrefix}{index}" == state.LastMessageKey);
        if (available.Count == 0) available = Enumerable.Range(0, pool.Count).ToList();
        var selectedIndex = available[Random.Shared.Next(available.Count)];
        var key = $"{tierPrefix}{selectedIndex}";
        state.UsedMessageKeys.Add(key);
        state.LastMessageKey = key;
        await store.SaveAsync(state, cancellationToken);

        var targetNames = state.CurrentTargetVoterIds
            .Select(id => observation.DisplayNames.GetValueOrDefault(id, id))
            .ToList();
        return ApplyTemplate(pool[selectedIndex], session, state, reminderNumber, targetNames);
    }

    private static string ApplyTemplate(
        string template,
        MatchSession session,
        ZaloOverbookStateData state,
        int reminderNumber,
        IReadOnlyList<string> targetNames)
    {
        var capacity = session.TeamCount * session.TeamSize;
        return template
            .Replace("{sessionName}", session.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{effectiveSlotCount}", state.EffectiveSlotCount.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{rawVoterCount}", state.RawVoterCount.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{capacity}", capacity.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{firstExcessSlot}", (capacity + 1).ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{excessCount}", state.ExcessSlotCount.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{reminderNumber}", reminderNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{names}", string.Join(", ", targetNames), StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static OverbookOutgoingMessage BuildMentionMessage(
        IReadOnlyList<string> targetIds,
        IReadOnlyDictionary<string, string> names,
        string body)
    {
        var builder = new StringBuilder();
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var id in targetIds)
        {
            if (builder.Length > 0) builder.Append(' ');
            var displayName = names.GetValueOrDefault(id, id).TrimStart('@');
            var label = $"@{displayName}";
            var position = builder.Length;
            builder.Append(label);
            mentions.Add(new BridgeOutgoingMention(id, position, label.Length));
        }
        if (builder.Length > 0) builder.Append(' ');
        builder.Append(body);
        return new OverbookOutgoingMessage(builder.ToString(), mentions);
    }

    private async Task SaveBotMessageAsync(
        MatchSession session,
        string messageId,
        string content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return;
        if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(
                item => item.ZaloConnectionId == session.ZaloConnectionId && item.MessageId == messageId,
                cancellationToken))
            return;
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = session.ZaloConnectionId,
            GroupId = session.ZaloGroupId,
            MessageId = messageId,
            SenderId = session.ZaloConnection.AccountZaloId,
            SenderName = session.ZaloConnection.DisplayName,
            Content = content,
            IsFromBot = true,
            SentAt = now,
            ReceivedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<string> NormalizeMessages(IReadOnlyList<string>? messages, int maxCount = 50) =>
        (messages ?? [])
            .Select(message => message.Trim())
            .Where(message => message.Length > 0)
            .Select(message => message.Length <= 600 ? message : message[..600])
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Clamp(maxCount, 1, 200))
            .ToList();

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(ZaloOverbookLogic.NormalizeId)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
