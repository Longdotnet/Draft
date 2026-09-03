using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector protector,
    ZaloIntegrationService integration,
    ZaloOverbookService overbook,
    ZaloPollClassifierService classifier,
    IConfiguration configuration,
    ILogger<ZaloAutoSessionService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ZaloAutoSessionStore store = new(db);

    public static ZaloAutoSessionService Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var classifier = new ZaloPollClassifierService(
            httpClientFactory.CreateClient(),
            configuration,
            loggerFactory.CreateLogger<ZaloPollClassifierService>());
        return new ZaloAutoSessionService(
            services.GetRequiredService<VolleyDraftDbContext>(),
            services.GetRequiredService<ZaloBridgeClient>(),
            services.GetRequiredService<ZaloCredentialProtector>(),
            services.GetRequiredService<ZaloIntegrationService>(),
            services.GetRequiredService<ZaloOverbookService>(),
            classifier,
            configuration,
            loggerFactory.CreateLogger<ZaloAutoSessionService>());
    }

    public async Task EnsureTrackedGroupsAsync(CancellationToken cancellationToken = default)
    {
        await store.EnsureAsync(cancellationToken);
        await store.SeedFromExistingSessionsAsync(cancellationToken);
    }

    public async Task ObservePollBoardEventAsync(
        ZaloPollBoardEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(incoming.EventType, "update_board", StringComparison.OrdinalIgnoreCase)) return;
        if (!configuration.GetValue("AutoSession:Enabled", true)) return;

        await EnsureTrackedGroupsAsync(cancellationToken);
        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        if (accountId.Length == 0 || groupId.Length == 0) return;

        var trackedGroups = await store.GetActiveTrackedGroupsForAccountAsync(accountId, groupId, cancellationToken);
        foreach (var tracked in trackedGroups)
        {
            var connection = await GetConnectionAsync(tracked.ZaloConnectionId, cancellationToken);
            if (connection is null) continue;
            using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
            var credentials = document.RootElement.Clone();
            BridgePoll? poll = null;

            if (!string.IsNullOrWhiteSpace(incoming.BoardId))
            {
                try
                {
                    poll = await bridge.GetPollAsync(credentials, incoming.BoardId.Trim());
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    logger.LogDebug(exception, "Board id {BoardId} was not directly resolvable as a poll id", incoming.BoardId);
                }
            }

            if (poll is null)
            {
                var polls = await bridge.GetPollsAsync(credentials, tracked.GroupId);
                poll = polls
                    .Where(item => !item.IsClosed)
                    .OrderByDescending(item => item.UpdatedAtUnixMs)
                    .FirstOrDefault();
            }
            if (poll is not null)
                await ProcessPollAsync(tracked, connection, credentials, poll, cancellationToken);
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("AutoSession:Enabled", true)) return;
        await EnsureTrackedGroupsAsync(cancellationToken);
        var trackedGroups = await store.GetActiveTrackedGroupsAsync(cancellationToken);
        var maxPolls = Math.Clamp(configuration.GetValue("AutoSession:ReconcilePollLimit", 20), 3, 50);
        var maxAgeDays = Math.Clamp(configuration.GetValue("AutoSession:PollMaxAgeDays", 21), 3, 90);
        var oldestCreatedAt = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeMilliseconds();

        foreach (var tracked in trackedGroups.Take(100))
        {
            try
            {
                var connection = await GetConnectionAsync(tracked.ZaloConnectionId, cancellationToken);
                if (connection is null) continue;
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var polls = await bridge.GetPollsAsync(credentials, tracked.GroupId);
                foreach (var poll in polls
                             .Where(item => item.CreatedAtUnixMs >= oldestCreatedAt)
                             .OrderByDescending(item => item.CreatedAtUnixMs)
                             .Take(maxPolls))
                {
                    await ProcessPollAsync(tracked, connection, credentials, poll, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Auto-session poll reconciliation failed for Group={GroupId}", tracked.GroupId);
            }
        }

        await ProcessPendingConfirmationsAsync(cancellationToken);
    }

    public async Task ProcessPendingConfirmationsAsync(CancellationToken cancellationToken = default)
    {
        var proposals = await store.GetPendingProposalsAsync(cancellationToken);
        foreach (var proposal in proposals)
        {
            if (DateTimeOffset.UtcNow - proposal.CreatedAt > TimeSpan.FromDays(3))
            {
                proposal.Status = ZaloPollSessionProposalStatus.Superseded;
                proposal.LastError = "proposal_expired";
                await store.UpsertProposalAsync(proposal, cancellationToken);
                continue;
            }

            var tracked = await store.GetTrackedGroupAsync(proposal.TrackedGroupId, cancellationToken);
            if (tracked is null || !tracked.AutoSessionEnabled) continue;
            var connection = await GetConnectionAsync(tracked.ZaloConnectionId, cancellationToken);
            if (connection is null) continue;

            try
            {
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var history = await bridge.GetGroupMessageHistoryAsync(
                    credentials,
                    tracked.GroupId,
                    Math.Clamp(configuration.GetValue("AutoSession:ConfirmationHistoryCount", 200), 50, 500),
                    cancellationToken);
                if (!history.IsSupported || string.IsNullOrWhiteSpace(proposal.ProposalMessageId)) continue;

                var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
                var operators = GetOrganizerIds(roles);
                var candidates = DeserializeCandidates(proposal.CandidatesJson);
                if (candidates.Count == 0) continue;

                var confirmation = history.Messages
                    .Where(message => !message.IsFromBot)
                    .Where(message => operators.Contains(NormalizeId(message.SenderId), StringComparer.Ordinal))
                    .Where(message => message.SentAtUnixMs >= proposal.CreatedAt.AddMinutes(-1).ToUnixTimeMilliseconds())
                    .Where(message => string.Equals(
                        message.Quote?.MessageId?.Trim(),
                        proposal.ProposalMessageId.Trim(),
                        StringComparison.Ordinal))
                    .OrderBy(message => message.SentAtUnixMs)
                    .FirstOrDefault(message =>
                        ZaloPollScheduleParser.IsRejection(message.Content) ||
                        ZaloPollScheduleParser.IsApproval(message.Content, candidates));
                if (confirmation is null) continue;

                if (ZaloPollScheduleParser.IsRejection(confirmation.Content))
                {
                    proposal.Status = ZaloPollSessionProposalStatus.Rejected;
                    proposal.ApprovedByZaloUserId = NormalizeId(confirmation.SenderId);
                    proposal.ApprovedAt = DateTimeOffset.UtcNow;
                    proposal.LastError = null;
                    await store.UpsertProposalAsync(proposal, cancellationToken);
                    await bridge.SendGroupMessageAsync(
                        connection.AccountZaloId,
                        tracked.GroupId,
                        "Ok, tui bỏ qua poll này và sẽ không tạo trận tự động từ lịch đó.",
                        [],
                        idempotencyKey: $"auto-session-reject:{proposal.Id}");
                    continue;
                }

                var currentPoll = await bridge.GetPollAsync(credentials, proposal.PollId);
                var currentHash = ZaloPollScheduleParser.ComputeStructureHash(currentPoll);
                if (!string.Equals(currentHash, proposal.PollStructureHash, StringComparison.Ordinal))
                {
                    proposal.Status = ZaloPollSessionProposalStatus.Superseded;
                    proposal.LastError = "poll_structure_changed_before_confirmation";
                    await store.UpsertProposalAsync(proposal, cancellationToken);
                    await bridge.SendGroupMessageAsync(
                        connection.AccountZaloId,
                        tracked.GroupId,
                        "Poll vừa đổi lịch/option nên xác nhận cũ không còn hợp lệ. Tui sẽ đọc lại poll và gửi đề xuất mới.",
                        [],
                        idempotencyKey: $"auto-session-superseded:{proposal.Id}:{currentPoll.UpdatedAtUnixMs}");
                    await ProcessPollAsync(tracked, connection, credentials, currentPoll, cancellationToken);
                    continue;
                }

                var selected = ZaloPollScheduleParser.SelectFromApproval(confirmation.Content, candidates);
                if (selected.Count == 0) continue;

                // Approved is deliberately transient. We only persist Created in the same
                // transaction as the sessions. A crash before CreateSessionsAsync therefore
                // leaves AwaitingApproval intact, so the exact reply can be replayed safely.
                proposal.Status = ZaloPollSessionProposalStatus.Approved;
                proposal.ApprovedByZaloUserId = NormalizeId(confirmation.SenderId);
                proposal.ApprovedAt = DateTimeOffset.UtcNow;
                proposal.LastError = null;
                await CreateSessionsAsync(
                    tracked,
                    connection,
                    currentPoll,
                    proposal,
                    selected,
                    operators,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Auto-session proposal confirmation failed Proposal={ProposalId}", proposal.Id);
            }
        }
    }

    private async Task ProcessPollAsync(
        ZaloTrackedGroupData tracked,
        ZaloConnection connection,
        JsonElement credentials,
        BridgePoll poll,
        CancellationToken cancellationToken)
    {
        var structureHash = ZaloPollScheduleParser.ComputeStructureHash(poll);
        var existing = await store.GetProposalAsync(tracked.Id, poll.Id, cancellationToken);
        if (existing is not null && string.Equals(existing.PollStructureHash, structureHash, StringComparison.Ordinal))
        {
            if (existing.Status == ZaloPollSessionProposalStatus.Failed)
            {
                if (DateTimeOffset.UtcNow - existing.UpdatedAt < TimeSpan.FromMinutes(10)) return;
            }
            else if (existing.Status == ZaloPollSessionProposalStatus.AwaitingApproval &&
                     string.IsNullOrWhiteSpace(existing.ProposalMessageId))
            {
                // The process may have died after persisting AwaitingApproval but before the
                // provider message id was stored. Re-send using the same idempotency key.
            }
            else if (existing.Status == ZaloPollSessionProposalStatus.Approved)
            {
                // Recover legacy/interrupted transient state by rebuilding a fresh proposal.
                existing.Status = ZaloPollSessionProposalStatus.Failed;
                existing.LastError = "recovering_approved_without_created";
            }
            else
            {
                return;
            }
        }

        var parsedCandidates = ZaloPollScheduleParser.ExtractCandidates(poll, tracked);
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

        if (poll.IsAnonymous || poll.IsClosed || parsedCandidates.Count == 0)
        {
            proposal.CandidatesJson = JsonSerializer.Serialize(parsedCandidates, JsonOptions);
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
        foreach (var candidate in parsedCandidates)
        {
            var linked = await store.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken);
            if (linked is null) candidates.Add(candidate);
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
        if (!organizerIds.Contains(NormalizeId(poll.CreatorId), StringComparer.Ordinal))
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

        if (!tracked.RequireOrganizerApproval)
        {
            proposal.Status = ZaloPollSessionProposalStatus.Approved;
            proposal.ApprovedByZaloUserId = NormalizeId(poll.CreatorId);
            proposal.ApprovedAt = DateTimeOffset.UtcNow;
            await CreateSessionsAsync(
                tracked,
                connection,
                poll,
                proposal,
                candidates,
                organizerIds,
                cancellationToken);
            return;
        }

        proposal.Status = ZaloPollSessionProposalStatus.AwaitingApproval;
        proposal = await store.UpsertProposalAsync(proposal, cancellationToken);
        var memberNames = await ResolveNamesAsync(credentials, organizerIds);
        var body = BuildProposalBody(poll, candidates, 3 * Math.Max(2, tracked.DefaultTeamSize));
        var outgoing = BuildMentionMessage(organizerIds, memberNames, body);
        try
        {
            var sent = await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                tracked.GroupId,
                outgoing.Message,
                outgoing.Mentions,
                idempotencyKey: $"auto-session-proposal:{tracked.Id}:{poll.Id}:{structureHash[..12]}");
            if (!sent.Sent || string.IsNullOrWhiteSpace(sent.MessageId))
                throw new InvalidOperationException("Zalo bridge did not return the proposal message id.");
            proposal.ProposalMessageId = sent.MessageId.Trim();
            proposal.LastError = null;
            await store.UpsertProposalAsync(proposal, cancellationToken);
            logger.LogInformation(
                "Auto-session proposal sent Group={GroupId} Poll={PollId} Candidates={Count} Confidence={Confidence}",
                tracked.GroupId,
                poll.Id,
                candidates.Count,
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

    private async Task CreateSessionsAsync(
        ZaloTrackedGroupData tracked,
        ZaloConnection connection,
        BridgePoll poll,
        ZaloPollSessionProposalData proposal,
        IReadOnlyList<ZaloAutoSessionCandidate> selected,
        IReadOnlyList<string> organizerIds,
        CancellationToken cancellationToken)
    {
        var created = new List<(string SessionId, ZaloAutoSessionCandidate Candidate)>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var candidate in selected
                         .GroupBy(item => item.OptionId, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                var existingLink = await store.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken);
                if (existingLink is not null) continue;

                var sessionId = Guid.NewGuid().ToString("n");
                await store.AddLinkAsync(
                    new ZaloAutoSessionLinkData(
                        Guid.NewGuid().ToString("n"),
                        tracked.Id,
                        poll.Id,
                        candidate.OptionId,
                        sessionId,
                        DateTimeOffset.UtcNow),
                    cancellationToken);
                var claimedLink = await store.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken);
                if (claimedLink is null || !string.Equals(claimedLink.SessionId, sessionId, StringComparison.Ordinal)) continue;

                var session = new MatchSession
                {
                    Id = sessionId,
                    Name = BuildSessionName(candidate),
                    AdminUserId = tracked.AdminUserId,
                    ZaloConnectionId = tracked.ZaloConnectionId,
                    ZaloGroupId = tracked.GroupId,
                    ZaloGroupName = tracked.GroupName,
                    StartTime = candidate.StartTime,
                    Location = tracked.DefaultLocation,
                    BotEnabled = tracked.BotEnabledForCreatedSessions,
                    BotOperatorZaloUserIdsJson = JsonSerializer.Serialize(organizerIds, JsonOptions),
                    TeamCount = 3,
                    TeamSize = Math.Max(2, tracked.DefaultTeamSize),
                    TotalSets = Math.Max(1, tracked.DefaultTotalSets),
                    Status = SessionStatus.Setup,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                foreach (var teamName in new[] { "Team A", "Team B", "Team C" })
                {
                    session.Teams.Add(new Team
                    {
                        SessionId = sessionId,
                        Name = teamName
                    });
                }
                session.PollImports.Add(new PollImport
                {
                    SessionId = sessionId,
                    ImportedByUserId = tracked.AdminUserId,
                    ZaloGroupId = tracked.GroupId,
                    PollId = poll.Id,
                    PollQuestion = poll.Question,
                    SelectedOptionIdsJson = JsonSerializer.Serialize(new[] { candidate.OptionId }, JsonOptions),
                    ImportedPlayerCount = 0,
                    ImportedAt = DateTimeOffset.UtcNow
                });
                db.MatchSessions.Add(session);
                created.Add((sessionId, candidate));
            }

            proposal.Status = ZaloPollSessionProposalStatus.Created;
            proposal.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            await store.UpsertProposalAsync(proposal, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            proposal.Status = ZaloPollSessionProposalStatus.Failed;
            proposal.LastError = Truncate(exception.Message, 1000);
            await store.UpsertProposalAsync(proposal, cancellationToken);
            throw;
        }

        var syncFailures = new List<string>();
        foreach (var item in created)
        {
            try
            {
                var sync = await integration.SyncLatestPollAsync(
                    tracked.AdminUserId,
                    item.SessionId,
                    item.Candidate.OptionContent);
                if (!sync.IsSuccess) syncFailures.Add($"{item.Candidate.DayKey}: {sync.Error}");

                var overbookStore = new ZaloOverbookStateStore(db);
                var state = await overbookStore.GetAsync(item.SessionId, cancellationToken)
                            ?? new ZaloOverbookStateData { SessionId = item.SessionId };
                state.Enabled = true;
                state.GraceMinutes = Math.Clamp(configuration.GetValue("AutoSession:OverbookGraceMinutes", 5), 0, 120);
                state.ReminderIntervalMinutes = Math.Clamp(configuration.GetValue("AutoSession:OverbookReminderMinutes", 30), 5, 240);
                state.MaxReminders = Math.Clamp(configuration.GetValue("AutoSession:OverbookMaxReminders", 5), 1, 20);
                await overbookStore.SaveAsync(state, cancellationToken);
                await overbook.ObserveAsync(item.SessionId, null, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                syncFailures.Add($"{item.Candidate.DayKey}: {exception.Message}");
                logger.LogWarning(exception, "Auto-created session post-sync failed Session={SessionId}", item.SessionId);
            }
        }

        var createdNames = created.Count == 0
            ? "không có kèo mới (các option này đã được tạo trước đó)"
            : string.Join(", ", created.Select(item => BuildSessionName(item.Candidate)));
        var message = $"Đã tạo tự động: {createdNames}. Poll đã được liên kết theo từng option; roster sẽ tiếp tục sync theo vote và overbook dùng capacity {3 * Math.Max(2, tracked.DefaultTeamSize)}.";
        if (syncFailures.Count > 0)
            message += $" Có {syncFailures.Count} lỗi sync cần kiểm tra: {string.Join(" | ", syncFailures.Select(item => Truncate(item, 180)))}";
        await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            tracked.GroupId,
            message,
            [],
            idempotencyKey: $"auto-session-created:{proposal.Id}");
    }

    private async Task<ZaloConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken) =>
        await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Id == connectionId && connection.Status == ZaloConnectionStatus.Connected, cancellationToken);

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
            logger.LogDebug(exception, "Could not resolve organizer names for auto-session proposal");
            return organizerIds.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<string> GetOrganizerIds(BridgeGroupRoles roles) =>
        new[] { NormalizeId(roles.CreatorId) }
            .Concat(roles.AdminIds.Select(NormalizeId))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string BuildProposalBody(
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        int capacity)
    {
        var lines = candidates.Select(candidate =>
            $"• {candidate.DayKey} {candidate.StartTime.ToOffset(TimeSpan.FromHours(7)):dd/MM HH:mm} — {candidate.VoteCount}/{capacity} vote");
        return $"Tui thấy poll “{Truncate(poll.Question, 180)}” có vẻ là lịch bóng tuần này:\n{string.Join("\n", lines)}\nMỗi kèo tối đa {capacity} slot. Reply đúng tin này bằng “tạo cả {candidates.Count}”, “chỉ T6 CN”, “T4 đổi 18h”, hoặc “bỏ qua”.";
    }

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
        try
        {
            return JsonSerializer.Deserialize<List<ZaloAutoSessionCandidate>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildSessionName(ZaloAutoSessionCandidate candidate)
    {
        var local = candidate.StartTime.ToOffset(TimeSpan.FromHours(7));
        var value = $"{candidate.DayKey} {local:dd/MM HH:mm} - {candidate.OptionContent}".Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static string NormalizeId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
