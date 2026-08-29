using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionV2Service(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector protector,
    ZaloPollClassifierService classifier,
    ZaloAutoSessionService legacyAutoSession,
    IConfiguration configuration,
    ILogger<ZaloAutoSessionV2Service> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ExplicitTimeRegex = new(
        @"(?<!\d)[0-2]?\d\s*(?:h|:)(?:\s*[0-5]?\d)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ZaloAutoSessionStore store = new(db);
    private readonly ZaloAutoSessionV2Store v2Store = new(db);
    private readonly ZaloAutoSessionObservabilityStore observabilityStore = new(db);

    public static ZaloAutoSessionV2Service Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var classifier = new ZaloPollClassifierService(
            httpClientFactory.CreateClient(),
            configuration,
            loggerFactory.CreateLogger<ZaloPollClassifierService>());
        return new ZaloAutoSessionV2Service(
            services.GetRequiredService<VolleyDraftDbContext>(),
            services.GetRequiredService<ZaloBridgeClient>(),
            services.GetRequiredService<ZaloCredentialProtector>(),
            classifier,
            ZaloAutoSessionService.Create(services),
            configuration,
            loggerFactory.CreateLogger<ZaloAutoSessionV2Service>());
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await store.EnsureAsync(cancellationToken);
        await store.SeedFromExistingSessionsAsync(cancellationToken);
        await v2Store.EnsureAsync(cancellationToken);
    }

    public async Task ObservePollBoardEventAsync(
        ZaloPollBoardEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(incoming.EventType, "update_board", StringComparison.OrdinalIgnoreCase)) return;
        await EnsureAsync(cancellationToken);
        if (!(await v2Store.GetRuntimeAsync(cancellationToken)).GlobalEnabled) return;

        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return;

        var trackedGroups = await store.GetActiveTrackedGroupsForAccountAsync(accountId, groupId, cancellationToken);
        foreach (var tracked in trackedGroups)
        {
            var rollout = await v2Store.GetRolloutModeAsync(tracked.Id, cancellationToken);
            if (rollout == ZaloAutoSessionRolloutMode.Disabled) continue;
            if (!await v2Store.IsRetryDueAsync(tracked.Id, cancellationToken)) continue;
            await v2Store.RecordPollEventAsync(tracked.Id, cancellationToken);

            try
            {
                var connection = await GetConnectionAsync(tracked.ZaloConnectionId, cancellationToken);
                if (connection is null) continue;
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var poll = await ResolveEventPollAsync(credentials, tracked, incoming, cancellationToken);
                if (poll is null) continue;
                await ProcessPollAsync(tracked, connection, credentials, poll, rollout, cancellationToken);
                await v2Store.RecordSuccessAsync(tracked.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await v2Store.RecordErrorAsync(tracked.Id, exception.Message, cancellationToken);
                logger.LogWarning(exception, "Auto-session v2 event handling failed Group={GroupId}", tracked.GroupId);
            }
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var runtime = await v2Store.GetRuntimeAsync(cancellationToken);
        if (!runtime.GlobalEnabled) return;

        var trackedGroups = await store.GetActiveTrackedGroupsAsync(cancellationToken);
        var maxPolls = Math.Clamp(configuration.GetValue("AutoSession:ReconcilePollLimit", 20), 3, 50);
        var maxAgeDays = Math.Clamp(configuration.GetValue("AutoSession:PollMaxAgeDays", 21), 3, 90);
        var oldestCreatedAt = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeMilliseconds();
        var hasLiveGroup = false;

        foreach (var tracked in trackedGroups.Take(100))
        {
            var rollout = await v2Store.GetRolloutModeAsync(tracked.Id, cancellationToken);
            if (rollout == ZaloAutoSessionRolloutMode.Disabled) continue;
            if (rollout == ZaloAutoSessionRolloutMode.Live) hasLiveGroup = true;
            if (!await v2Store.IsRetryDueAsync(tracked.Id, cancellationToken)) continue;
            await v2Store.RecordReconcileAsync(tracked.Id, cancellationToken);

            try
            {
                var connection = await GetConnectionAsync(tracked.ZaloConnectionId, cancellationToken);
                if (connection is null)
                    throw new InvalidOperationException("zalo_connection_not_connected");
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var polls = await bridge.GetPollsAsync(credentials, tracked.GroupId);
                foreach (var poll in polls
                             .Where(item => item.CreatedAtUnixMs >= oldestCreatedAt)
                             .OrderByDescending(item => item.CreatedAtUnixMs)
                             .Take(maxPolls))
                {
                    await ProcessPollAsync(tracked, connection, credentials, poll, rollout, cancellationToken);
                }
                await v2Store.RecordSuccessAsync(tracked.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await v2Store.RecordErrorAsync(tracked.Id, exception.Message, cancellationToken);
                logger.LogWarning(exception, "Auto-session v2 reconciliation failed Group={GroupId}", tracked.GroupId);
            }
        }

        // V2 owns discovery/preview. The proven legacy confirmation path still owns the
        // transaction that creates sessions, but only Live proposals are left in
        // AwaitingApproval, so PreviewOnly can never create a website session.
        if (hasLiveGroup &&
            !configuration.GetValue("AutoSession:ConversationV3Enabled", true))
            await legacyAutoSession.ProcessPendingConfirmationsAsync(cancellationToken);

        await CaptureLearningSignalsAsync(trackedGroups, cancellationToken);
    }

    private async Task<BridgePoll?> ResolveEventPollAsync(
        JsonElement credentials,
        ZaloTrackedGroupData tracked,
        ZaloPollBoardEvent incoming,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(incoming.BoardId))
        {
            try
            {
                return await bridge.GetPollAsync(credentials, incoming.BoardId.Trim());
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug(exception, "Board id {BoardId} was not directly resolvable as poll", incoming.BoardId);
            }
        }
        var polls = await bridge.GetPollsAsync(credentials, tracked.GroupId);
        return polls
            .Where(item => !item.IsClosed)
            .OrderByDescending(item => item.UpdatedAtUnixMs)
            .FirstOrDefault();
    }

    private async Task ProcessPollAsync(
        ZaloTrackedGroupData tracked,
        ZaloConnection connection,
        JsonElement credentials,
        BridgePoll poll,
        ZaloAutoSessionRolloutMode rollout,
        CancellationToken cancellationToken)
    {
        var structureHash = ZaloPollScheduleParser.ComputeStructureHash(poll);
        var existing = await store.GetProposalAsync(tracked.Id, poll.Id, cancellationToken);
        var existingPreviewOnly = existing is not null &&
                                  existing.Status == ZaloPollSessionProposalStatus.Ignored &&
                                  existing.ClassifierReason.StartsWith("preview_only:", StringComparison.Ordinal);
        if (existing is not null && string.Equals(existing.PollStructureHash, structureHash, StringComparison.Ordinal))
        {
            if (existing.Status == ZaloPollSessionProposalStatus.Failed)
            {
                if (DateTimeOffset.UtcNow - existing.UpdatedAt < TimeSpan.FromMinutes(10)) return;
            }
            else if (rollout == ZaloAutoSessionRolloutMode.PreviewOnly && existingPreviewOnly &&
                     !string.IsNullOrWhiteSpace(existing.ProposalMessageId))
            {
                return;
            }
            else if (rollout == ZaloAutoSessionRolloutMode.Live &&
                     existing.Status == ZaloPollSessionProposalStatus.AwaitingApproval &&
                     !string.IsNullOrWhiteSpace(existing.ProposalMessageId))
            {
                return;
            }
            else if (existing.Status is ZaloPollSessionProposalStatus.Created or ZaloPollSessionProposalStatus.Rejected)
            {
                return;
            }
            else if (!existingPreviewOnly && existing.Status == ZaloPollSessionProposalStatus.Ignored)
            {
                var retryMinutes = Math.Clamp(
                    configuration.GetValue("AutoSession:IgnoredRetryMinutes", 15),
                    2,
                    1440);
                if (!ShouldRetryIgnoredProposal(
                        existing,
                        poll,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMinutes(retryMinutes)))
                    return;
            }
        }

        var learnedRules = await v2Store.GetApprovedDayTimeRulesAsync(tracked.Id, cancellationToken);
        var parsed = ApplyLearnedDayDefaults(
            ZaloPollScheduleParser.ExtractCandidates(poll, tracked),
            learnedRules);
        var proposal = existing ?? new ZaloPollSessionProposalData
        {
            Id = Guid.NewGuid().ToString("n"),
            TrackedGroupId = tracked.Id,
            PollId = poll.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        proposal.PollQuestion = poll.Question;
        proposal.PollCreatorId = NormalizeId(poll.CreatorId);
        proposal.PollUpdatedAtUnixMs = poll.UpdatedAtUnixMs;
        proposal.PollStructureHash = structureHash;
        proposal.ProposalMessageId = null;
        proposal.ApprovedByZaloUserId = null;
        proposal.ApprovedAt = null;
        proposal.LastError = null;

        if (poll.IsAnonymous || poll.IsClosed || parsed.Count == 0)
        {
            proposal.CandidatesJson = JsonSerializer.Serialize(parsed, JsonOptions);
            proposal.Status = ZaloPollSessionProposalStatus.Ignored;
            proposal.ClassifierConfidence = 0;
            proposal.ClassifierReason = poll.IsAnonymous
                ? "anonymous_poll"
                : poll.IsClosed
                    ? "closed_poll"
                    : "no_current_schedule_option";
            await store.UpsertProposalAsync(proposal, cancellationToken);
            return;
        }

        var candidates = new List<ZaloAutoSessionCandidate>();
        foreach (var candidate in parsed)
        {
            if (await store.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken) is null)
                candidates.Add(candidate);
        }
        proposal.CandidatesJson = JsonSerializer.Serialize(candidates, JsonOptions);
        if (candidates.Count == 0)
        {
            proposal.Status = ZaloPollSessionProposalStatus.Created;
            proposal.ClassifierConfidence = 1;
            proposal.ClassifierReason = "all_schedule_options_already_linked";
            await store.UpsertProposalAsync(proposal, cancellationToken);
            return;
        }

        var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
        var organizerIds = GetOrganizerIds(roles);
        var pollCreatorId = NormalizeId(poll.CreatorId);
        if (!organizerIds.Contains(pollCreatorId, StringComparer.Ordinal))
        {
            proposal.Status = ZaloPollSessionProposalStatus.Ignored;
            proposal.ClassifierConfidence = 0;
            proposal.ClassifierReason = "poll_creator_is_not_group_organizer";
            await store.UpsertProposalAsync(proposal, cancellationToken);
            return;
        }

        var classification = await classifier.ClassifyAsync(poll, candidates, cancellationToken);
        proposal.ClassifierConfidence = classification.Confidence;
        proposal.ClassifierReason = classification.Reason;
        if (!classification.IsVolleyballSignupPoll)
        {
            proposal.Status = ZaloPollSessionProposalStatus.Ignored;
            await store.UpsertProposalAsync(proposal, cancellationToken);
            return;
        }

        candidates = await FilterCandidatesMissingWebsiteMatchAsync(tracked, candidates, cancellationToken);
        proposal.CandidatesJson = JsonSerializer.Serialize(candidates, JsonOptions);
        if (candidates.Count == 0)
        {
            proposal.Status = ZaloPollSessionProposalStatus.Ignored;
            proposal.ClassifierReason = "website_matches_already_exist";
            await store.UpsertProposalAsync(proposal, cancellationToken);
            logger.LogInformation(
                "Auto-session skipped organizer prompt because website matches already exist Group={GroupId} Poll={PollId}",
                tracked.GroupId,
                poll.Id);
            return;
        }

        var targetIds = new[] { pollCreatorId };
        var names = await ResolveNamesAsync(credentials, targetIds);
        var body = BuildOrganizerPreview(
            poll,
            candidates,
            3,
            Math.Max(2, tracked.DefaultTeamSize),
            Math.Max(1, tracked.DefaultTotalSets),
            tracked.DefaultLocation,
            rollout);
        var outgoing = BuildMentionMessage(targetIds, names, body);

        proposal.Status = rollout == ZaloAutoSessionRolloutMode.Live
            ? ZaloPollSessionProposalStatus.AwaitingApproval
            : ZaloPollSessionProposalStatus.Ignored;
        if (rollout == ZaloAutoSessionRolloutMode.PreviewOnly)
            proposal.ClassifierReason = $"preview_only:{classification.Reason}";
        proposal = await store.UpsertProposalAsync(proposal, cancellationToken);

        try
        {
            var sent = await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                tracked.GroupId,
                outgoing.Message,
                outgoing.Mentions,
                idempotencyKey: $"auto-session-v2:{rollout}:{tracked.Id}:{poll.Id}:{structureHash[..12]}");
            if (!sent.Sent || string.IsNullOrWhiteSpace(sent.MessageId))
                throw new InvalidOperationException("Zalo bridge did not return organizer preview message id.");
            proposal.ProposalMessageId = sent.MessageId.Trim();
            proposal.LastError = null;
            await store.UpsertProposalAsync(proposal, cancellationToken);
            if (configuration.GetValue("AutoSession:ConversationV3Enabled", true))
            {
                await new ZaloAutoSessionConversationStore(db).CreateFromPreviewAsync(
                    proposal,
                    tracked,
                    candidates,
                    sent.MessageId.Trim(),
                    configuration,
                    cancellationToken);
            }
            logger.LogInformation(
                "Auto-session organizer preview sent Group={GroupId} Poll={PollId} Creator={CreatorId} Mode={Mode} Confidence={Confidence}",
                tracked.GroupId,
                poll.Id,
                pollCreatorId,
                rollout,
                classification.Confidence);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            proposal.Status = ZaloPollSessionProposalStatus.Failed;
            proposal.LastError = Truncate(exception.Message, 1000);
            await store.UpsertProposalAsync(proposal, cancellationToken);
            throw;
        }
    }

    internal static bool ShouldRetryIgnoredProposal(
        ZaloPollSessionProposalData existing,
        BridgePoll poll,
        DateTimeOffset now,
        TimeSpan retryAfter)
    {
        if (existing.Status != ZaloPollSessionProposalStatus.Ignored) return false;
        var reason = existing.ClassifierReason ?? string.Empty;
        if (reason.StartsWith("preview_only:", StringComparison.Ordinal) ||
            reason is "anonymous_poll" or
                "closed_poll" or
                "poll_creator_is_not_group_organizer" or
                "all_schedule_options_already_linked" or
                "website_matches_already_exist")
            return false;

        var age = now - existing.UpdatedAt;
        var pollChangedSinceLastDecision = poll.UpdatedAtUnixMs > existing.PollUpdatedAtUnixMs;
        return age >= retryAfter ||
               pollChangedSinceLastDecision && age >= TimeSpan.FromMinutes(2);
    }

    private async Task<List<ZaloAutoSessionCandidate>> FilterCandidatesMissingWebsiteMatchAsync(
        ZaloTrackedGroupData tracked,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];

        var earliest = candidates.Min(candidate => candidate.StartTime).AddMinutes(-5);
        var latest = candidates.Max(candidate => candidate.StartTime).AddMinutes(5);
        var existingStarts = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.AdminUserId == tracked.AdminUserId &&
                session.ZaloGroupId == tracked.GroupId &&
                session.Status != SessionStatus.Cancelled &&
                session.StartTime != null &&
                session.StartTime >= earliest &&
                session.StartTime <= latest)
            .Select(session => session.StartTime!.Value)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(candidate => !existingStarts.Any(start =>
                Math.Abs((start - candidate.StartTime).TotalMinutes) <= 5))
            .ToList();
    }

    private async Task CaptureLearningSignalsAsync(
        IReadOnlyList<ZaloTrackedGroupData> trackedGroups,
        CancellationToken cancellationToken)
    {
        foreach (var tracked in trackedGroups.Take(100))
        {
            try
            {
                var proposals = await observabilityStore.GetProposalsAsync(
                    tracked.AdminUserId,
                    tracked.Id,
                    50,
                    cancellationToken);
                foreach (var proposal in proposals.Where(item =>
                             item.ApprovedByZaloUserId is not null &&
                             item.Status is ZaloPollSessionProposalStatus.Created or ZaloPollSessionProposalStatus.Rejected))
                {
                    if (proposal.Status == ZaloPollSessionProposalStatus.Rejected)
                    {
                        await v2Store.AddLearningSignalAsync(new ZaloAutoSessionLearningSignalData(
                            Guid.NewGuid().ToString("n"),
                            tracked.Id,
                            proposal.Id,
                            proposal.PollId,
                            "__poll__",
                            proposal.ApprovedByZaloUserId ?? string.Empty,
                            "classification_rejection",
                            null,
                            null,
                            null,
                            null,
                            null,
                            ZaloAutoSessionLearningStatus.Pending,
                            null,
                            null,
                            null,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow), cancellationToken);
                        continue;
                    }

                    foreach (var candidate in DeserializeCandidates(proposal.CandidatesJson))
                    {
                        var link = await store.GetLinkAsync(tracked.Id, proposal.PollId, candidate.OptionId, cancellationToken);
                        if (link is null)
                        {
                            await v2Store.AddLearningSignalAsync(new ZaloAutoSessionLearningSignalData(
                                Guid.NewGuid().ToString("n"), tracked.Id, proposal.Id, proposal.PollId,
                                candidate.OptionId, proposal.ApprovedByZaloUserId ?? string.Empty,
                                "selection_override", candidate.DayKey, candidate.StartTime, null,
                                null, null, ZaloAutoSessionLearningStatus.Pending,
                                null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
                            continue;
                        }

                        var actualStart = await db.MatchSessions
                            .AsNoTracking()
                            .Where(session => session.Id == link.SessionId && session.AdminUserId == tracked.AdminUserId)
                            .Select(session => session.StartTime)
                            .SingleOrDefaultAsync(cancellationToken);
                        if (actualStart is null || SameLocalMinute(candidate.StartTime, actualStart.Value)) continue;

                        var promotable = !HasExplicitTime(candidate.OptionContent);
                        var localActual = actualStart.Value.ToOffset(TimeSpan.FromHours(7));
                        await v2Store.AddLearningSignalAsync(new ZaloAutoSessionLearningSignalData(
                            Guid.NewGuid().ToString("n"), tracked.Id, proposal.Id, proposal.PollId,
                            candidate.OptionId, proposal.ApprovedByZaloUserId ?? string.Empty,
                            promotable ? "default_day_time_correction" : "one_off_time_override",
                            candidate.DayKey,
                            candidate.StartTime,
                            actualStart,
                            promotable ? "default_day_time" : null,
                            promotable ? localActual.Hour * 60 + localActual.Minute : null,
                            ZaloAutoSessionLearningStatus.Pending,
                            null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Auto-session learning capture skipped Group={GroupId}", tracked.GroupId);
            }
        }
    }

    internal static IReadOnlyList<ZaloAutoSessionCandidate> ApplyLearnedDayDefaults(
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        IReadOnlyDictionary<string, int> approvedDayMinutes)
    {
        if (approvedDayMinutes.Count == 0) return candidates;
        return candidates.Select(candidate =>
        {
            if (HasExplicitTime(candidate.OptionContent) ||
                !approvedDayMinutes.TryGetValue(candidate.DayKey, out var minutes))
                return candidate;
            minutes = Math.Clamp(minutes, 0, 23 * 60 + 59);
            var local = candidate.StartTime.ToOffset(TimeSpan.FromHours(7));
            var date = local.Date.AddMinutes(minutes);
            return candidate with { StartTime = new DateTimeOffset(date, TimeSpan.FromHours(7)) };
        }).ToList();
    }

    internal static bool HasExplicitTime(string? text) =>
        ExplicitTimeRegex.IsMatch(ZaloPollScheduleParser.NormalizeText(text));

    internal static string BuildOrganizerPreview(
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        int teamCount,
        int teamSize,
        int totalSets,
        string? location,
        ZaloAutoSessionRolloutMode rollout)
    {
        var capacity = Math.Max(1, teamCount) * Math.Max(1, teamSize);
        var lines = candidates.Select(candidate =>
            $"• {candidate.DayKey} {candidate.StartTime.ToOffset(TimeSpan.FromHours(7)):dd/MM HH:mm} — hiện {candidate.VoteCount}/{capacity} người");
        var locationText = string.IsNullOrWhiteSpace(location) ? "chưa đặt sân mặc định" : location.Trim();
        var modeLine = rollout == ZaloAutoSessionRolloutMode.PreviewOnly
            ? "🧪 CANARY PREVIEW: hiện chỉ xem trước, reply sẽ KHÔNG tạo website. Khi test ổn, admin chuyển group sang Live."
            : "✅ Tui đã kiểm tra website: chưa có trận trùng lịch cho các ngày dưới đây. Website CHƯA được tạo. Bạn cứ reply tin này và nói tự nhiên phần muốn tạo/sửa; bot sẽ chốt lại trước khi ghi website.";
        return $"Tui hiểu poll “{Truncate(poll.Question, 180)}” là poll đăng ký lịch bóng chuyền.\n\n" +
               $"PREVIEW WEBSITE\n{string.Join("\n", lines)}\n" +
               $"• Địa điểm: {locationText}\n\n" +
               $"{modeLine}\n\n" +
               "Bạn không cần nhớ câu lệnh. Cứ reply tin này và nói tự nhiên, ví dụ:\n" +
               "• “T6 thôi” hoặc “à thêm CN”\n" +
               "• “T6 6h”, “sân A”, “21 người”\n" +
               "• “tạo đi” khi bản nháp đã đúng\n" +
               "• “bỏ qua” nếu không muốn tạo.";
    }

    private async Task<ZaloConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken) =>
        await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(connection =>
                connection.Id == connectionId && connection.Status == ZaloConnectionStatus.Connected,
                cancellationToken);

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
            logger.LogDebug(exception, "Could not resolve poll creator name for organizer preview");
            return organizerIds.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        }
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

    private static bool SameLocalMinute(DateTimeOffset left, DateTimeOffset right)
    {
        var a = left.ToOffset(TimeSpan.FromHours(7));
        var b = right.ToOffset(TimeSpan.FromHours(7));
        return a.Year == b.Year && a.Month == b.Month && a.Day == b.Day && a.Hour == b.Hour && a.Minute == b.Minute;
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
