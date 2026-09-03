using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloUpcomingMatchDiscoveryRunResult(
    int CandidateProposalCount,
    int PromptedCount,
    int SkippedCount,
    int FailedCount);

/// <summary>
/// Safety net before MatchLifecycleCoordinator has a MatchSession to coordinate.
///
/// Auto Session V2 deliberately ignores polls that its classifier cannot identify
/// with enough confidence. That is safe, but it can leave a real organizer-created
/// match silent forever. An otherwise valid proposal can also become silent when its
/// original organizer conversation expires without an answer. This service revisits
/// those two recoverable cases when a schedule option enters the configured lead
/// window (two days by default, at noon Vietnam time).
///
/// It never creates a session itself: it opens or revives the existing organizer
/// conversation so the proven Auto Session action executor keeps ownership of the
/// final transaction and poll/session link.
/// </summary>
internal sealed class ZaloUpcomingMatchDiscoveryService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector protector,
    IConfiguration configuration,
    ILogger<ZaloUpcomingMatchDiscoveryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ZaloAutoSessionStore autoSessions = new(db);
    private readonly ZaloAutoSessionV2Store runtimeStore = new(db);
    private readonly ZaloAutoSessionConversationStore conversations = new(db);

    public static ZaloUpcomingMatchDiscoveryService Create(IServiceProvider services) =>
        new(
            services.GetRequiredService<VolleyDraftDbContext>(),
            services.GetRequiredService<ZaloBridgeClient>(),
            services.GetRequiredService<ZaloCredentialProtector>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ZaloUpcomingMatchDiscoveryService>());

    public Task<ZaloUpcomingMatchDiscoveryRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<ZaloUpcomingMatchDiscoveryRunResult> RunAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("AutoSession:UpcomingDiscovery:Enabled", true))
            return new(0, 0, 1, 0);

        await autoSessions.EnsureAsync(cancellationToken);
        await conversations.EnsureAsync(cancellationToken);
        await runtimeStore.EnsureAsync(cancellationToken);
        if (!(await runtimeStore.GetRuntimeAsync(cancellationToken)).GlobalEnabled)
            return new(0, 0, 1, 0);

        var keys = await GetCandidateProposalKeysAsync(cancellationToken);
        var prompted = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var key in keys)
        {
            try
            {
                if (await TryPromptAsync(key.TrackedGroupId, key.PollId, now, cancellationToken))
                    prompted += 1;
                else
                    skipped += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed += 1;
                logger.LogWarning(
                    exception,
                    "Upcoming match discovery failed TrackedGroup={TrackedGroupId} Poll={PollId}",
                    key.TrackedGroupId,
                    key.PollId);
            }
        }

        return new(keys.Count, prompted, skipped, failed);
    }

    private async Task<bool> TryPromptAsync(
        string trackedGroupId,
        string pollId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var proposal = await autoSessions.GetProposalAsync(trackedGroupId, pollId, cancellationToken);
        if (proposal is null) return false;

        var existingConversation = await conversations.GetByProposalAsync(proposal.Id, cancellationToken);
        if (!IsProposalRecoverable(proposal, existingConversation, now)) return false;

        var tracked = await autoSessions.GetTrackedGroupAsync(trackedGroupId, cancellationToken);
        if (tracked is null || !tracked.AutoSessionEnabled) return false;
        if (await runtimeStore.GetRolloutModeAsync(tracked.Id, cancellationToken) != ZaloAutoSessionRolloutMode.Live)
            return false;

        var parsed = DeserializeCandidates(proposal.CandidatesJson)
            .Where(candidate => candidate.StartTime > now)
            .OrderBy(candidate => candidate.StartTime)
            .ToList();
        if (parsed.Count == 0) return false;

        var unlinked = new List<ZaloAutoSessionCandidate>();
        foreach (var candidate in parsed)
        {
            if (await autoSessions.GetLinkAsync(tracked.Id, proposal.PollId, candidate.OptionId, cancellationToken) is not null)
                continue;
            if (await HasMatchingSessionAsync(tracked, candidate, cancellationToken))
                continue;
            unlinked.Add(candidate);
        }
        if (unlinked.Count == 0) return false;

        var leadDays = Math.Clamp(configuration.GetValue("AutoSession:UpcomingDiscovery:LeadDays", 2), 1, 7);
        var triggerHour = Math.Clamp(configuration.GetValue("AutoSession:UpcomingDiscovery:TriggerHourLocal", 12), 0, 23);
        var trigger = unlinked.FirstOrDefault(candidate =>
            ZaloUpcomingMatchDiscoveryPolicy.IsDue(now, candidate.StartTime, leadDays, triggerHour));
        if (trigger is null) return false;

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == tracked.ZaloConnectionId && item.Status == ZaloConnectionStatus.Connected,
                cancellationToken);
        if (connection is null) return false;

        using var credentialsDocument = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
        var credentials = credentialsDocument.RootElement.Clone();
        var livePoll = await bridge.GetPollAsync(credentials, proposal.PollId);
        if (livePoll.IsAnonymous || livePoll.IsClosed) return false;
        if (!string.Equals(
                ZaloPollScheduleParser.ComputeStructureHash(livePoll),
                proposal.PollStructureHash,
                StringComparison.Ordinal))
            return false;

        var liveOptionIds = livePoll.Options.Select(option => option.Id).ToHashSet(StringComparer.Ordinal);
        unlinked = unlinked.Where(candidate => liveOptionIds.Contains(candidate.OptionId)).ToList();
        if (unlinked.Count == 0) return false;

        var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
        var organizerIds = GetOrganizerIds(roles);
        if (organizerIds.Count == 0) return false;

        // Prefer the organizer who actually created the poll. If their role changed,
        // fall back to the current group creator. We intentionally keep one primary
        // conversation owner; Conversation V3 can escalate to trusted backups later.
        var pollCreatorId = NormalizeId(proposal.PollCreatorId);
        var primaryOrganizerId = organizerIds.Contains(pollCreatorId, StringComparer.Ordinal)
            ? pollCreatorId
            : NormalizeId(roles.CreatorId);
        if (primaryOrganizerId.Length == 0 ||
            !organizerIds.Contains(primaryOrganizerId, StringComparer.Ordinal))
            return false;

        var names = await ResolveNamesAsync(credentials, [primaryOrganizerId]);
        var body = ZaloUpcomingMatchDiscoveryPolicy.BuildPrompt(
            proposal.PollQuestion,
            unlinked,
            trigger,
            Math.Max(1, tracked.DefaultTeamCount * tracked.DefaultTeamSize),
            now);
        var outgoing = BuildMentionMessage([primaryOrganizerId], names, body);
        var keySuffix = proposal.PollStructureHash.Length >= 12
            ? proposal.PollStructureHash[..12]
            : proposal.PollStructureHash;
        var sent = await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            tracked.GroupId,
            outgoing.Message,
            outgoing.Mentions,
            idempotencyKey: $"upcoming-match-discovery:{proposal.Id}:{keySuffix}");
        if (!sent.Sent || string.IsNullOrWhiteSpace(sent.MessageId))
            throw new InvalidOperationException("Zalo bridge did not confirm the upcoming-match discovery prompt.");

        // Persist only the still-unlinked upcoming options. If the poll has T5 + T7
        // and T5 reaches the lead window first, the organizer can still confirm both
        // or say "T5 thôi" in this one conversation. That avoids losing the later
        // option when the proposal is eventually marked Created.
        proposal.CandidatesJson = JsonSerializer.Serialize(unlinked, JsonOptions);
        proposal.Status = ZaloPollSessionProposalStatus.AwaitingApproval;
        proposal.ProposalMessageId = sent.MessageId.Trim();
        proposal.ClassifierReason = ZaloUpcomingMatchDiscoveryPolicy.MarkRescuedReason(proposal.ClassifierReason);
        proposal.LastError = null;
        proposal = await autoSessions.UpsertProposalAsync(proposal, cancellationToken);

        await CreateOrReviveConversationAsync(
            proposal,
            tracked,
            unlinked,
            primaryOrganizerId,
            sent.MessageId.Trim(),
            now,
            existingConversation,
            cancellationToken);

        logger.LogInformation(
            "Upcoming match discovery prompted organizer Group={GroupId} Poll={PollId} Trigger={TriggerDay} Candidates={Count}",
            tracked.GroupId,
            proposal.PollId,
            trigger.DayKey,
            unlinked.Count);
        return true;
    }

    private static bool IsProposalRecoverable(
        ZaloPollSessionProposalData proposal,
        ZaloAutoSessionConversationData? existingConversation,
        DateTimeOffset now)
    {
        if (proposal.Status == ZaloPollSessionProposalStatus.Ignored)
        {
            return string.IsNullOrWhiteSpace(proposal.ProposalMessageId) &&
                   ZaloUpcomingMatchDiscoveryPolicy.IsRecoverableIgnoredReason(proposal.ClassifierReason);
        }

        if (proposal.Status != ZaloPollSessionProposalStatus.AwaitingApproval)
            return false;

        // A discovery prompt is the one near-match recovery pass. If that prompt is
        // itself ignored until it expires, do not keep tagging the group repeatedly.
        if (proposal.ClassifierReason.StartsWith("upcoming_discovery:", StringComparison.Ordinal))
            return false;

        if (existingConversation is not null)
            return !ZaloUpcomingMatchDiscoveryPolicy.IsConversationStillActive(
                existingConversation.State,
                existingConversation.ExpiresAt,
                now);

        // V2 and Conversation V3 normally create the conversation in the same pass.
        // Give that path time to converge before treating a missing row as abandoned.
        return now - proposal.UpdatedAt >= TimeSpan.FromHours(6);
    }

    private async Task CreateOrReviveConversationAsync(
        ZaloPollSessionProposalData proposal,
        ZaloTrackedGroupData tracked,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        string primaryOrganizerId,
        string messageId,
        DateTimeOffset now,
        ZaloAutoSessionConversationData? existingConversation,
        CancellationToken cancellationToken)
    {
        var draft = new ZaloAutoSessionConversationDraft(
            candidates.Select(candidate => new ZaloAutoSessionConversationDraftItem(
                candidate.OptionId,
                candidate.OptionContent,
                candidate.DayKey,
                candidate.StartTime,
                candidate.VoteCount,
                true)).ToList(),
            tracked.DefaultLocation,
            Math.Max(2, tracked.DefaultTeamSize));
        var draftJson = JsonSerializer.Serialize(draft, JsonOptions);
        var followUpHours = Math.Clamp(
            configuration.GetValue("AutoSession:UpcomingDiscovery:FollowUpHours", 6),
            1,
            24);
        var earliestStart = candidates.Min(candidate => candidate.StartTime);
        var expiry = earliestStart < now.AddHours(48) ? earliestStart : now.AddHours(48);
        if (expiry <= now.AddHours(1)) expiry = now.AddHours(1);
        var nextFollowUp = now.AddHours(followUpHours);
        if (nextFollowUp >= expiry) nextFollowUp = expiry.AddMinutes(-30);
        DateTimeOffset? safeFollowUp = nextFollowUp > now ? nextFollowUp : null;

        ZaloAutoSessionConversationData conversation;
        if (existingConversation is null)
        {
            conversation = await conversations.CreateIfMissingAsync(
                new ZaloAutoSessionConversationData
                {
                    ProposalId = proposal.Id,
                    TrackedGroupId = tracked.Id,
                    PollId = proposal.PollId,
                    GroupId = tracked.GroupId,
                    OriginalOrganizerId = primaryOrganizerId,
                    ActiveOrganizerId = primaryOrganizerId,
                    State = ZaloAutoSessionConversationState.PreviewSent,
                    InitialDraftJson = draftJson,
                    DraftJson = draftJson,
                    PreviewMessageId = messageId,
                    CurrentBotMessageId = messageId,
                    Version = 0,
                    ReminderCount = 0,
                    LastBotMessageAt = now,
                    NextFollowUpAt = safeFollowUp,
                    ExpiresAt = expiry,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                cancellationToken);
        }
        else
        {
            // Same poll structure, same proposal identity. Revive the durable V3
            // conversation instead of creating a competing state machine. The new bot
            // turn below makes reply-to-new-prompt routing work even though the original
            // preview message remains part of the audit trail.
            existingConversation.ActiveOrganizerId = primaryOrganizerId;
            existingConversation.State = ZaloAutoSessionConversationState.PreviewSent;
            existingConversation.DraftJson = draftJson;
            existingConversation.CurrentBotMessageId = messageId;
            existingConversation.LastQuestionType = null;
            existingConversation.LastIntent = null;
            existingConversation.Version += 1;
            existingConversation.ReminderCount = 0;
            existingConversation.LastOrganizerMessageAt = null;
            existingConversation.LastBotMessageAt = now;
            existingConversation.NextFollowUpAt = safeFollowUp;
            existingConversation.ExpiresAt = expiry;
            existingConversation.LastError = null;
            conversation = await conversations.SaveAsync(existingConversation, cancellationToken);
        }

        await conversations.AddTurnAsync(
            conversation.Id,
            messageId,
            "Bot",
            "bot",
            "Auto Session",
            "upcoming_match_discovery",
            "UpcomingMatchDiscovery",
            "system",
            1,
            cancellationToken);
    }

    private async Task<bool> HasMatchingSessionAsync(
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionCandidate candidate,
        CancellationToken cancellationToken)
    {
        // Avoid asking to create an obvious duplicate if a human already created the
        // same match outside Auto Session. A narrow +/- 75 minute window is used so
        // two genuinely separate sessions on one date are not collapsed together.
        var start = candidate.StartTime.AddMinutes(-75);
        var end = candidate.StartTime.AddMinutes(75);
        return await db.MatchSessions
            .AsNoTracking()
            .AnyAsync(session =>
                session.ZaloConnectionId == tracked.ZaloConnectionId &&
                session.ZaloGroupId == tracked.GroupId &&
                session.Status != SessionStatus.Cancelled &&
                session.StartTime != null &&
                session.StartTime >= start &&
                session.StartTime <= end,
                cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        JsonElement credentials,
        IReadOnlyList<string> organizerIds)
    {
        try
        {
            var members = await bridge.GetMembersAsync(credentials, organizerIds);
            return members
                .GroupBy(member => NormalizeId(member.ZaloUserId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(exception, "Could not resolve organizer name for upcoming match discovery");
            return organizerIds.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        }
    }

    private async Task<IReadOnlyList<ProposalKey>> GetCandidateProposalKeysAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "TrackedGroupId", "PollId"
            FROM "ZaloPollSessionProposals"
            WHERE "Status" IN ('Ignored', 'AwaitingApproval')
            ORDER BY "UpdatedAt" DESC
            LIMIT 200;
            """;
        var result = new List<ProposalKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ProposalKey(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static IReadOnlyList<string> GetOrganizerIds(BridgeGroupRoles roles) =>
        new[] { NormalizeId(roles.CreatorId) }
            .Concat(roles.AdminIds.Select(NormalizeId))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static (string Message, IReadOnlyList<BridgeOutgoingMention> Mentions) BuildMentionMessage(
        IReadOnlyList<string> targetIds,
        IReadOnlyDictionary<string, string> names,
        string body)
    {
        var builder = new StringBuilder();
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var targetId in targetIds)
        {
            var name = names.GetValueOrDefault(targetId, targetId).Trim();
            if (name.Length == 0) name = targetId;
            var token = $"@{name}";
            if (builder.Length > 0) builder.Append(' ');
            var position = builder.Length;
            builder.Append(token);
            mentions.Add(new BridgeOutgoingMention(targetId, position, token.Length));
        }
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(body);
        return (builder.ToString(), mentions);
    }

    private static IReadOnlyList<ZaloAutoSessionCandidate> DeserializeCandidates(string json)
    {
        try { return JsonSerializer.Deserialize<List<ZaloAutoSessionCandidate>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string NormalizeId(string? value)
    {
        var valueClean = (value ?? string.Empty).Trim();
        return valueClean.EndsWith("_0", StringComparison.Ordinal) ? valueClean[..^2] : valueClean;
    }

    private sealed record ProposalKey(string TrackedGroupId, string PollId);
}

internal static class ZaloUpcomingMatchDiscoveryPolicy
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    internal static bool IsRecoverableIgnoredReason(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        if (value.StartsWith("preview_only:", StringComparison.Ordinal) ||
            value.StartsWith("upcoming_discovery:", StringComparison.Ordinal))
            return false;
        return value is not "anonymous_poll"
            and not "closed_poll"
            and not "no_current_schedule_option"
            and not "poll_creator_is_not_group_organizer"
            and not "all_schedule_options_already_linked";
    }

    internal static bool IsConversationStillActive(
        ZaloAutoSessionConversationState state,
        DateTimeOffset expiresAt,
        DateTimeOffset now) =>
        expiresAt > now &&
        state is ZaloAutoSessionConversationState.PreviewSent
            or ZaloAutoSessionConversationState.Discussing
            or ZaloAutoSessionConversationState.Clarifying
            or ZaloAutoSessionConversationState.ReadyToConfirm;

    internal static bool IsDue(
        DateTimeOffset now,
        DateTimeOffset matchStart,
        int leadDays = 2,
        int triggerHourLocal = 12)
    {
        leadDays = Math.Clamp(leadDays, 1, 7);
        triggerHourLocal = Math.Clamp(triggerHourLocal, 0, 23);
        var localStart = matchStart.ToOffset(VietnamOffset);
        var localNow = now.ToOffset(VietnamOffset);
        var triggerDate = localStart.Date.AddDays(-leadDays).AddHours(triggerHourLocal);
        var trigger = new DateTimeOffset(triggerDate, VietnamOffset);
        return localNow >= trigger && localNow < localStart;
    }

    internal static string MarkRescuedReason(string? originalReason)
    {
        var value = (originalReason ?? string.Empty).Trim();
        if (value.StartsWith("upcoming_discovery:", StringComparison.Ordinal)) return value;
        return $"upcoming_discovery:{(value.Length == 0 ? "classifier_missed_schedule" : value)}";
    }

    internal static string BuildPrompt(
        string pollQuestion,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        ZaloAutoSessionCandidate trigger,
        int capacity,
        DateTimeOffset now)
    {
        var triggerLocal = trigger.StartTime.ToOffset(VietnamOffset);
        var nowLocal = now.ToOffset(VietnamOffset);
        var days = Math.Max(0, (triggerLocal.Date - nowLocal.Date).Days);
        var leadText = days == 0 ? "hôm nay" : days == 1 ? "ngày mai" : $"còn {days} ngày";
        var lines = candidates
            .OrderBy(candidate => candidate.StartTime)
            .Select(candidate =>
            {
                var local = candidate.StartTime.ToOffset(VietnamOffset);
                return $"• {candidate.DayKey} {local:dd/MM HH:mm} — {candidate.VoteCount}/{Math.Max(1, capacity)} người đang vote";
            });
        var question = Truncate(pollQuestion, 160);
        return $"Tui rà lại lịch sắp tới vì {trigger.DayKey} {leadText} mà chưa thấy trận tương ứng được tạo từ poll “{question}”.\n\n" +
               $"{string.Join("\n", lines)}\n\n" +
               "Kèo này có chơi không? Cứ reply tin này và nói tự nhiên nha — ví dụ “có, tạo đi”, “T5 thôi”, hoặc “bỏ qua”. Tui chỉ tạo sau khi trưởng/phó xác nhận.";
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
