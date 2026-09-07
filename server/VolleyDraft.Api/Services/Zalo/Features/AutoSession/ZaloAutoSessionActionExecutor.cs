using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionActionExecutor(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloIntegrationService integration,
    ZaloOverbookService overbook,
    IConfiguration configuration,
    ILogger<ZaloAutoSessionActionExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ZaloAutoSessionStore store = new(db);

    public static ZaloAutoSessionActionExecutor Create(IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        return new ZaloAutoSessionActionExecutor(
            services.GetRequiredService<VolleyDraftDbContext>(),
            services.GetRequiredService<ZaloBridgeClient>(),
            services.GetRequiredService<ZaloIntegrationService>(),
            services.GetRequiredService<ZaloOverbookService>(),
            services.GetRequiredService<IConfiguration>(),
            loggerFactory.CreateLogger<ZaloAutoSessionActionExecutor>());
    }

    public async Task ExecuteAsync(
        ZaloTrackedGroupData tracked,
        ZaloConnection connection,
        BridgePoll poll,
        ZaloPollSessionProposalData proposal,
        IReadOnlyList<ZaloAutoSessionCandidate> selected,
        IReadOnlyList<string> organizerIds,
        string approvedByZaloUserId,
        string? locationOverride,
        int? teamSizeOverride,
        CancellationToken cancellationToken = default)
    {
        EnsureCandidatesMatchPollSource(poll, selected);

        var effectiveTeamSize = Math.Clamp(teamSizeOverride ?? tracked.DefaultTeamSize, 2, 30);
        var effectiveLocation = locationOverride ?? tracked.DefaultLocation;
        var created = new List<(string SessionId, ZaloAutoSessionCandidate Candidate)>();
        var linked = new List<(string SessionId, ZaloAutoSessionCandidate Candidate)>();

        proposal.Status = ZaloPollSessionProposalStatus.Approved;
        proposal.ApprovedByZaloUserId = NormalizeId(approvedByZaloUserId);
        proposal.ApprovedAt = DateTimeOffset.UtcNow;
        proposal.LastError = null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var candidate in selected
                         .GroupBy(item => item.OptionId, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                var existingLink = await store.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken);
                if (existingLink is not null)
                {
                    linked.Add((existingLink.SessionId, candidate));
                    continue;
                }

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
                if (claimedLink is null || !string.Equals(claimedLink.SessionId, sessionId, StringComparison.Ordinal))
                    continue;

                var session = new MatchSession
                {
                    Id = sessionId,
                    Name = BuildSessionName(candidate),
                    AdminUserId = tracked.AdminUserId,
                    ZaloConnectionId = tracked.ZaloConnectionId,
                    ZaloGroupId = tracked.GroupId,
                    ZaloGroupName = tracked.GroupName,
                    StartTime = candidate.StartTime.ToUniversalTime(),
                    Location = effectiveLocation,
                    BotEnabled = tracked.BotEnabledForCreatedSessions,
                    BotOperatorZaloUserIdsJson = JsonSerializer.Serialize(organizerIds, JsonOptions),
                    TeamCount = 3,
                    TeamSize = effectiveTeamSize,
                    TotalSets = Math.Max(1, tracked.DefaultTotalSets),
                    Status = SessionStatus.Setup,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                foreach (var teamName in new[] { "Team A", "Team B", "Team C" })
                    session.Teams.Add(new Team { SessionId = sessionId, Name = teamName });

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
                linked.Add((sessionId, candidate));
            }

            proposal.Status = ZaloPollSessionProposalStatus.Created;
            proposal.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            await store.UpsertProposalAsync(proposal, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await PersistFailureAfterRollbackAsync(transaction, store, proposal, exception, CancellationToken.None);
            throw;
        }

        await RunCommittedPostCreateAsync(async postCommitToken =>
        {
            var syncFailures = new List<string>();
            var handoffFailures = new List<string>();
            var handoffStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);

            foreach (var item in linked
                         .GroupBy(item => item.SessionId, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                try
                {
                    await handoffStore.HandOffAsync(
                        proposal.Id,
                        tracked.AdminUserId,
                        item.SessionId,
                        postCommitToken);
                }
                catch (Exception exception)
                {
                    handoffFailures.Add($"{item.Candidate.DayKey}: {exception.Message}");
                    logger.LogWarning(exception,
                        "Auto Session V5 lifecycle handoff failed Session={SessionId}", item.SessionId);
                }
            }

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
                    var state = await overbookStore.GetAsync(item.SessionId, postCommitToken)
                                ?? new ZaloOverbookStateData { SessionId = item.SessionId };
                    state.Enabled = true;
                    state.GraceMinutes = Math.Clamp(configuration.GetValue("AutoSession:OverbookGraceMinutes", 5), 0, 120);
                    state.ReminderIntervalMinutes = Math.Clamp(configuration.GetValue("AutoSession:OverbookReminderMinutes", 30), 5, 240);
                    state.MaxReminders = Math.Clamp(configuration.GetValue("AutoSession:OverbookMaxReminders", 5), 1, 20);
                    await overbookStore.SaveAsync(state, postCommitToken);
                    await overbook.ObserveAsync(item.SessionId, null, postCommitToken);
                }
                catch (Exception exception)
                {
                    syncFailures.Add($"{item.Candidate.DayKey}: {exception.Message}");
                    logger.LogWarning(exception, "Auto Session V3 post-create sync failed Session={SessionId}", item.SessionId);
                }
            }

            var createdNames = created.Count == 0
                ? "không có lịch mới (các option này đã được tạo trước đó)"
                : string.Join(", ", created.Select(item => BuildSessionName(item.Candidate)));
            var message = $"Đã tạo trên website: {createdNames}. Poll đã được liên kết theo từng option và roster sẽ tiếp tục sync theo vote.";
            if (handoffFailures.Count == 0 && linked.Count > 0)
                message += " Match Lifecycle đã nhận trạng thái authoritative để tiếp tục recruiting/waitlist/pass-slot/profile/draft readiness.";
            if (handoffFailures.Count > 0)
                message += $" Có {handoffFailures.Count} lifecycle handoff chưa đạt gate an toàn: {string.Join(" | ", handoffFailures.Select(item => Truncate(item, 180)))}";
            if (syncFailures.Count > 0)
                message += $" Có {syncFailures.Count} lỗi sync cần kiểm tra: {string.Join(" | ", syncFailures.Select(item => Truncate(item, 180)))}";

            await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                tracked.GroupId,
                message,
                [],
                idempotencyKey: $"auto-session-v3-created:{proposal.Id}");
        });
    }

    internal static async Task RunCommittedPostCreateAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(CancellationToken.None);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException("auto_session_post_commit_cancelled", exception);
        }
    }

    internal static async Task PersistFailureAfterRollbackAsync(
        IDbContextTransaction transaction,
        ZaloAutoSessionStore store,
        ZaloPollSessionProposalData proposal,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        await transaction.RollbackAsync(cancellationToken);
        await transaction.DisposeAsync();
        proposal.Status = ZaloPollSessionProposalStatus.Failed;
        proposal.LastError = Truncate(exception.Message, 1000);
        await store.UpsertProposalAsync(proposal, cancellationToken);
    }

    internal static void EnsureCandidatesMatchPollSource(
        BridgePoll poll,
        IReadOnlyList<ZaloAutoSessionCandidate> selected)
    {
        foreach (var candidate in selected
                     .GroupBy(item => item.OptionId, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (ZaloPollScheduleParser.ValidateCandidateConsistency(poll, candidate, out var reason))
                continue;

            throw new InvalidOperationException(
                $"auto_session_candidate_source_mismatch:{candidate.OptionId}:{reason ?? "unknown"}");
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
