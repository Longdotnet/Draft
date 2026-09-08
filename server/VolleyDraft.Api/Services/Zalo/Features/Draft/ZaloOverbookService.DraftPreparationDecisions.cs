using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDraftPreparationDecisionCommand(
    ZaloDraftPreparationDecisionKind Kind,
    int? RequestedSlotCount = null);

internal static partial class ZaloDraftPreparationDecisionPolicy
{
    private static readonly Regex StopMatch = new(
        @"(?<![a-z0-9])(?:(?:huy|cancel|nghi)\s*(?:keo|san|tran)|(?:keo|san|tran)\s*(?:huy|nghi))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeepRecruiting = new(
        @"(?<![a-z0-9])(?:kiem\s*them|tim\s*them|keu\s*them|reo\s*them|goi\s*them|cho\s*them|doi\s*them|kiem\s*cho\s*du|cho\s*du\s*(?:18|nguoi|slot)|cu\s*kiem\s*them)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlayCurrent = new(
        @"(?<![a-z0-9])(?:(?:chot\s*(?<count1>\d{1,2}))|(?<count2>\d{1,2})\s*(?:van\s*)?(?:danh|choi)(?:\s*(?:nha|di|luon|cung\s*duoc))?|(?:van|cu)\s*(?:danh|choi)(?:\s*(?<count3>\d{1,2}))?|(?:danh|choi)\s*(?<count4>\d{1,2})\s*(?:nguoi|slot)?\s*(?:cung\s*)?(?:duoc|ok))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ZaloDraftPreparationDecisionCommand? TryParse(string? content)
    {
        var normalized = ZaloDraftConversationPolicy.Normalize(content);
        if (normalized.Length == 0) return null;

        // "huy slot" is deliberately NOT a match-level stop. Slot transfer owns it.
        if (!normalized.Contains("huy slot", StringComparison.Ordinal) && StopMatch.IsMatch(normalized))
            return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.StopMatch);
        if (KeepRecruiting.IsMatch(normalized))
            return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.KeepRecruiting);

        var play = PlayCurrent.Match(normalized);
        if (!play.Success) return null;
        foreach (var groupName in new[] { "count1", "count2", "count3", "count4" })
        {
            var group = play.Groups[groupName];
            if (group.Success && int.TryParse(group.Value, out var count))
                return new ZaloDraftPreparationDecisionCommand(
                    ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
                    count);
        }
        return new ZaloDraftPreparationDecisionCommand(ZaloDraftPreparationDecisionKind.PlayCurrentRoster);
    }

    internal static bool CanAutoDraftEvenly(int effectiveSlotCount, int teamCount) =>
        teamCount > 0 &&
        effectiveSlotCount >= teamCount * 2 &&
        effectiveSlotCount % teamCount == 0;

    internal static bool ShouldSupersedeActiveDraftRequest(
        ZaloDraftPreparationDecisionKind commandKind,
        bool canEscalate,
        int activeSlotRisks) =>
        commandKind == ZaloDraftPreparationDecisionKind.KeepRecruiting ||
        !canEscalate ||
        activeSlotRisks > 0;
}

public sealed partial class ZaloOverbookService
{
    private sealed record DraftPreparationSessionResolution(
        MatchSession? Session,
        IReadOnlyList<MatchSession> Candidates);

    private async Task<bool> TryHandleDraftPreparationDecisionAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientSettings ambientSettings,
        CancellationToken cancellationToken)
    {
        // This lane is deterministic domain behavior. Ambient ShadowMode controls AI
        // participation, not explicit leader authority. Keep the parameter so the same
        // call site can be used from ambient/pre-routing without coupling semantics.
        _ = ambientSettings;

        // A natural "draft đi" after a leader explicitly locked a partial roster is
        // handled here before Social AI. It is never equivalent to the lock itself:
        // linked poll, live role, roster/share fingerprint, slot risks, profiles and
        // engine divisibility are all revalidated on this second turn.
        if (ZaloDraftConversationPolicy.IsStrongDraftConfirmation(incoming.Content) &&
            await TryHandlePartialRosterDraftCommandAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                cancellationToken))
            return true;

        var command = ZaloDraftPreparationDecisionPolicy.TryParse(incoming.Content);
        if (command is null) return false;

        var resolution = await ResolveDraftPreparationDecisionSessionAsync(
            connectionId,
            groupId,
            incoming.Content,
            requirePlayCurrentDecision: false,
            cancellationToken);
        var session = resolution.Session;
        if (session is null)
        {
            return await TryReplyDraftPreparationAmbiguityAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                resolution.Candidates,
                "quyết định kèo",
                cancellationToken);
        }

        var role = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            senderId);
        if (!role.IsSuccess || role.Value?.CanOperateBot != true)
        {
            // Leader decisions are authority-bearing state, not crowd sentiment.
            return false;
        }

        var connection = session.ZaloConnection!;
        var actorName = string.IsNullOrWhiteSpace(incoming.SenderName)
            ? senderId
            : incoming.SenderName.Trim();
        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var previousDecision = await decisionStore.GetAsync(session.Id, cancellationToken);

        if (command.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
        {
            await SupersedeAnyActiveDraftRequestAsync(session, cancellationToken);
            await decisionStore.SetAsync(
                session.Id,
                command.Kind,
                null,
                null,
                senderId,
                actorName,
                incoming.MessageId,
                cancellationToken);
            var change = ZaloDraftPreparationClientCopy.BuildDecisionChangePrefix(
                previousDecision, command.Kind, null, actorName);
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.StopMatch(change, session.Name),
                [],
                "draft_preparation_stop_match",
                cancellationToken);
            return true;
        }

        var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
        if (!sync.Success)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.VoteRefreshFailed(session.Name),
                [],
                "draft_preparation_decision_poll_sync_failed",
                cancellationToken);
            return true;
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, DateTimeOffset.UtcNow, cancellationToken);
        if (readiness is null) return false;
        var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);

        // A new KeepRecruiting direction always supersedes any pending draft request,
        // even when the roster is currently full and otherwise draft-ready. The latest
        // organizer intent must win over a stale confirmation seeded by an earlier turn.
        if (ZaloDraftPreparationDecisionPolicy.ShouldSupersedeActiveDraftRequest(
                command.Kind,
                readiness.CanEscalate,
                activeSlotRisks))
            await SupersedeAnyActiveDraftRequestAsync(session, cancellationToken);

        if (command.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
        {
            if (readiness.EffectiveSlotCount >= readiness.Capacity && activeSlotRisks == 0)
            {
                await decisionStore.SetAsync(
                    session.Id,
                    command.Kind,
                    null,
                    null,
                    senderId,
                    actorName,
                    incoming.MessageId,
                    cancellationToken);
                await SendDraftReplyAsync(
                    connectionId,
                    connection.AccountZaloId,
                    connection.DisplayName,
                    groupId,
                    incoming,
                    ZaloDraftPreparationClientCopy.KeepRecruitingAlreadyFull(
                        session.Name,
                        readiness.EffectiveSlotCount,
                        readiness.Capacity),
                    [],
                    "draft_preparation_recruitment_already_full",
                    cancellationToken);
                return true;
            }

            await decisionStore.SetAsync(
                session.Id,
                command.Kind,
                null,
                null,
                senderId,
                actorName,
                incoming.MessageId,
                cancellationToken);
            var change = ZaloDraftPreparationClientCopy.BuildDecisionChangePrefix(
                previousDecision, command.Kind, null, actorName);
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.KeepRecruiting(
                    change,
                    session.Name,
                    readiness.EffectiveSlotCount,
                    readiness.Capacity),
                [],
                "draft_preparation_keep_recruiting",
                cancellationToken);
            return true;
        }

        if (activeSlotRisks > 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.PassRisk(
                    session.Name,
                    readiness.EffectiveSlotCount,
                    readiness.Capacity,
                    activeSlotRisks),
                [],
                "draft_preparation_play_current_slot_risk",
                cancellationToken);
            return true;
        }

        if (readiness.EffectiveSlotCount > readiness.Capacity)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.OverCapacity(
                    session.Name,
                    readiness.EffectiveSlotCount,
                    readiness.Capacity),
                [],
                "draft_preparation_play_current_over_capacity",
                cancellationToken);
            return true;
        }

        if (command.RequestedSlotCount is { } requested && requested != readiness.EffectiveSlotCount)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.CountMismatch(
                    readiness.EffectiveSlotCount,
                    readiness.Capacity,
                    requested),
                [],
                "draft_preparation_play_current_count_mismatch",
                cancellationToken);
            return true;
        }

        if (readiness.EffectiveSlotCount <= 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.Empty(session.Name, readiness.Capacity),
                [],
                "draft_preparation_play_current_empty",
                cancellationToken);
            return true;
        }

        if (string.IsNullOrWhiteSpace(readiness.Fingerprint))
        {
            await SendDraftReplyAsync(
                connectionId,
                connection.AccountZaloId,
                connection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.SafeSnapshotUnavailable(session.Name),
                [],
                "draft_preparation_fingerprint_unavailable",
                cancellationToken);
            return true;
        }

        await decisionStore.SetAsync(
            session.Id,
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            readiness.Fingerprint,
            readiness.EffectiveSlotCount,
            senderId,
            actorName,
            incoming.MessageId,
            cancellationToken);

        var countLabel = ZaloDraftPreparationClientCopy.PlayerCountLabel(
            readiness.PresentPlayerCount,
            readiness.EffectiveSlotCount);
        var evenlyDraftable = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(
            readiness.EffectiveSlotCount,
            session.TeamCount);
        var decisionChange = ZaloDraftPreparationClientCopy.BuildDecisionChangePrefix(
            previousDecision,
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            readiness.EffectiveSlotCount,
            actorName);
        string reply;
        string outcome;
        if (readiness.MissingProfileCount > 0)
        {
            reply = ZaloDraftPreparationClientCopy.MissingProfiles(
                decisionChange,
                countLabel,
                readiness.MissingProfileCount,
                readiness.MissingProfileNames);
            outcome = "draft_preparation_play_current_missing_profiles";
        }
        else if (evenlyDraftable)
        {
            reply = ZaloDraftPreparationClientCopy.Locked(
                decisionChange,
                countLabel,
                session.TeamCount,
                readiness.EffectiveSlotCount / session.TeamCount);
            outcome = "draft_preparation_play_current_locked";
        }
        else
        {
            reply = ZaloDraftPreparationClientCopy.NotEven(
                decisionChange,
                countLabel,
                readiness.EffectiveSlotCount,
                session.TeamCount);
            outcome = "draft_preparation_play_current_not_even";
        }
        await SendDraftReplyAsync(
            connectionId,
            connection.AccountZaloId,
            connection.DisplayName,
            groupId,
            incoming,
            reply,
            [],
            outcome,
            cancellationToken);
        return true;
    }

    private async Task<bool> TryHandlePartialRosterDraftCommandAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        if (botService is null) return false;
        var resolution = await ResolveDraftPreparationDecisionSessionAsync(
            connectionId,
            groupId,
            incoming.Content,
            requirePlayCurrentDecision: true,
            cancellationToken);
        var session = resolution.Session;
        if (session is null)
        {
            return await TryReplyDraftPreparationAmbiguityAsync(
                connectionId,
                groupId,
                senderId,
                incoming,
                resolution.Candidates,
                "chia đội với danh sách đã chốt",
                cancellationToken);
        }

        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
        if (decision?.Kind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster) return false;

        var authorization = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            senderId);
        if (!authorization.IsSuccess)
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                "Tui chưa xác minh được quyền trưởng/phó từ Zalo nên chưa chia đội nha. Dữ liệu vẫn giữ nguyên.",
                [],
                "draft_partial_role_lookup_failed",
                cancellationToken);
            return true;
        }
        if (authorization.Value?.CanOperateBot != true)
        {
            // Do not consume ordinary members' chat merely because a leader decision exists.
            return false;
        }

        // The actor who authorized playing the partial roster must still hold a live
        // organizer role too. Another leader cannot revive a stale authorization from
        // someone whose role was removed.
        var decisionActorAuthorization = await integration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            decision.ActorZaloUserId);
        if (!decisionActorAuthorization.IsSuccess ||
            decisionActorAuthorization.Value?.CanOperateBot != true)
        {
            await decisionStore.ClearAsync(session.Id, cancellationToken);
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.DecisionActorRoleStale(session.Name),
                [],
                "draft_partial_decision_actor_role_stale",
                cancellationToken);
            return true;
        }

        var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
        if (!sync.Success)
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.DraftVoteRefreshFailed(session.Name),
                [],
                "draft_partial_poll_refresh_failed",
                cancellationToken);
            return true;
        }

        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, DateTimeOffset.UtcNow, cancellationToken);
        if (readiness is null) return false;
        decision = await decisionStore.GetAsync(session.Id, cancellationToken);
        if (decision?.Kind != ZaloDraftPreparationDecisionKind.PlayCurrentRoster ||
            !decision.MatchesRoster(readiness))
        {
            await decisionStore.ClearAsync(session.Id, cancellationToken);
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.PlayerListChanged(session.Name),
                [],
                "draft_partial_roster_changed",
                cancellationToken);
            return true;
        }

        if (readiness.State is ZaloDraftReadinessState.SessionStarted or
                               ZaloDraftReadinessState.InvalidStatus or
                               ZaloDraftReadinessState.AlreadyDrafted or
                               ZaloDraftReadinessState.MissingStartTime)
        {
            await decisionStore.ClearAsync(session.Id, cancellationToken);
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                BuildReadinessBlockerText(readiness),
                [],
                readiness.ReasonCode,
                cancellationToken);
            return true;
        }

        var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);
        if (activeSlotRisks > 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.PartialPassRisk(session.Name, activeSlotRisks),
                [],
                "draft_partial_slot_risk",
                cancellationToken);
            return true;
        }

        if (readiness.MissingProfileCount > 0)
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                $"Chưa chia đội được nha, còn {readiness.MissingProfileCount} người thiếu thông tin: {string.Join(", ", readiness.MissingProfileNames.Take(6))}.",
                [],
                "draft_partial_missing_profiles",
                cancellationToken);
            return true;
        }

        if (!ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(
                readiness.EffectiveSlotCount,
                session.TeamCount))
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                ZaloDraftPreparationClientCopy.PartialNotEven(
                    readiness.EffectiveSlotCount,
                    session.TeamCount),
                [],
                "draft_partial_not_even",
                cancellationToken);
            return true;
        }

        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        var expiry = GetRequestExpiry(
            readiness.StartTime,
            DateTimeOffset.UtcNow,
            settings,
            settings.TargetedConfirmationMinutes);
        if (!await SeedDraftConfirmationAsync(
                connectionId,
                groupId,
                senderId,
                session.Id,
                expiry,
                cancellationToken,
                refuseToOverwriteDifferentPending: true))
        {
            await SendDraftReplyAsync(
                connectionId,
                session.ZaloConnection!.AccountZaloId,
                session.ZaloConnection.DisplayName,
                groupId,
                incoming,
                "Ông đang có một yêu cầu NPC khác chờ xác nhận nên tui chưa ghi đè để chia đội. Xử lý/huỷ lượt kia rồi nói `draft đi` lại nha.",
                [],
                "draft_partial_pending_conflict",
                cancellationToken);
            return true;
        }

        try
        {
            // Reuse the existing mutation router after all partial-roster gates pass.
            // The seeded pending state fixes the exact session; the router retains its
            // own authorization, poll sync, profile checks, action history and idempotency.
            await botService.HandleIncomingAsync(
                PromoteToBot(incoming, "xác nhận draft"),
                cancellationToken);
        }
        catch
        {
            await RemoveDraftPendingAsync(
                connectionId,
                groupId,
                senderId,
                session.Id,
                cancellationToken);
            throw;
        }
        return true;
    }

    private async Task<DraftPreparationSessionResolution> ResolveDraftPreparationDecisionSessionAsync(
        string connectionId,
        string groupId,
        string? content,
        bool requirePlayCurrentDecision,
        CancellationToken cancellationToken)
    {
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
            .OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue)
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return new DraftPreparationSessionResolution(null, []);

        if (requirePlayCurrentDecision)
        {
            var decisionStore = new ZaloDraftPreparationDecisionStore(db);
            var withDecision = new List<MatchSession>();
            foreach (var item in sessions)
            {
                var decision = await decisionStore.GetAsync(item.Id, cancellationToken);
                if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster)
                    withDecision.Add(item);
            }
            sessions = withDecision;
            if (sessions.Count == 0) return new DraftPreparationSessionResolution(null, []);
        }

        var normalized = ZaloDraftConversationPolicy.Normalize(content);
        var references = sessions
            .Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime))
            .ToList();
        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(normalized, references);
        var matched = sessions.Where(item => matchedIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
        if (matched.Count == 1)
            return new DraftPreparationSessionResolution(matched[0], matched);
        if (matched.Count == 0 && sessions.Count == 1)
            return new DraftPreparationSessionResolution(sessions[0], sessions);
        return new DraftPreparationSessionResolution(
            null,
            matched.Count > 1 ? matched : sessions);
    }

    private async Task<bool> TryReplyDraftPreparationAmbiguityAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<MatchSession> candidates,
        string actionLabel,
        CancellationToken cancellationToken)
    {
        if (candidates.Count < 2) return false;
        var authoritySession = candidates[0];
        var role = await integration.GetGroupRoleAuthorizationAsync(
            authoritySession.AdminUserId,
            authoritySession.Id,
            senderId);
        if (!role.IsSuccess || role.Value?.CanOperateBot != true) return false;

        var choices = string.Join(", ", candidates.Take(4).Select(FormatDraftSessionChoice));
        await SendDraftReplyAsync(
            connectionId,
            authoritySession.ZaloConnection!.AccountZaloId,
            authoritySession.ZaloConnection.DisplayName,
            groupId,
            incoming,
            $"Ông đang muốn {actionLabel} cho trận nào: {choices}? Nói rõ T4/T6, ngày hoặc tên kèo giúp tui; tui không đoán khi group có nhiều trận.",
            [],
            "draft_preparation_session_ambiguous",
            cancellationToken);
        return true;
    }

    private async Task SupersedeAnyActiveDraftRequestAsync(
        MatchSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return;

        var escalationStore = new ZaloDraftEscalationStore(db);
        var request = await escalationStore.LoadForSessionAsync(
            session.ZaloConnectionId,
            session.ZaloGroupId,
            session.Id,
            cancellationToken);
        if (request is null ||
            request.State is not (ZaloDraftEscalationState.AwaitingRequesterConsent or
                                  ZaloDraftEscalationState.ProactiveSoft or
                                  ZaloDraftEscalationState.ApproverTagged or
                                  ZaloDraftEscalationState.Executing))
            return;

        await escalationStore.SetStateAsync(
            request.Id,
            ZaloDraftEscalationState.Superseded,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.PrimaryApproverId))
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId,
                session.ZaloGroupId,
                request.PrimaryApproverId,
                session.Id,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SecondaryApproverId))
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId,
                session.ZaloGroupId,
                request.SecondaryApproverId,
                session.Id,
                cancellationToken);
    }
}
