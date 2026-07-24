using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloActivityBackfillCoordinator(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector credentialProtector,
    IConfiguration configuration,
    ILogger<ZaloActivityBackfillCoordinator> logger)
{
    private readonly int boardPageSize = ReadBounded(configuration, "ZaloActivitySync:BoardPageSize", 50, 1, 100);
    private readonly int maxBoardPages = ReadBounded(configuration, "ZaloActivitySync:MaxBoardPages", 100, 1, 1000);
    private readonly int incrementalBoardPages = ReadBounded(configuration, "ZaloActivitySync:IncrementalBoardPages", 5, 1, 100);
    private readonly int messageHistoryCount = ReadBounded(configuration, "ZaloActivitySync:MessageHistoryCount", 2000, 1, 5000);
    private readonly int maxRetries = ReadBounded(configuration, "ZaloActivitySync:RetryCount", 4, 0, 10);
    private readonly int retryDelayMs = ReadBounded(configuration, "ZaloActivitySync:RetryDelayMs", 750, 100, 30_000);
    private readonly int pauseBetweenRequestsMs = ReadBounded(configuration, "ZaloActivitySync:PauseBetweenRequestsMs", 150, 0, 10_000);
    private readonly int incrementalMinutes = ReadBounded(configuration, "ZaloActivitySync:IncrementalMinutes", 60, 5, 1440);
    private readonly int leaseMinutes = ReadBounded(configuration, "ZaloActivitySync:LeaseMinutes", 10, 1, 60);

    public async Task<ServiceResult<ZaloActivityBackfillStatusResponse>> QueueForSessionAsync(
        string adminUserId,
        string sessionId,
        bool full,
        CancellationToken cancellationToken)
    {
        var linked = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.ZaloConnectionId, session.ZaloGroupId })
            .SingleOrDefaultAsync(cancellationToken);
        if (linked is null)
            return ServiceResult<ZaloActivityBackfillStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy buổi đấu.");
        if (string.IsNullOrWhiteSpace(linked.ZaloConnectionId) || string.IsNullOrWhiteSpace(linked.ZaloGroupId))
            return ServiceResult<ZaloActivityBackfillStatusResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Buổi đấu chưa liên kết nhóm Zalo.");

        var job = await QueueGroupAsync(
            linked.ZaloConnectionId,
            linked.ZaloGroupId,
            full,
            cancellationToken);
        return ServiceResult<ZaloActivityBackfillStatusResponse>.Success(ToStatus(job));
    }

    public async Task<ZaloActivityBackfillJob> QueueGroupAsync(
        string connectionId,
        string groupId,
        bool full,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await db.ZaloActivityBackfillJobs
            .SingleOrDefaultAsync(
                item => item.ZaloConnectionId == connectionId && item.GroupId == groupId,
                cancellationToken);

        if (job is null)
        {
            job = new ZaloActivityBackfillJob
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                IsFullBackfill = true,
                Status = ZaloActivityBackfillStatus.Queued,
                Stage = ZaloActivityBackfillStage.Queued,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.ZaloActivityBackfillJobs.Add(job);
        }
        else if (job.Status != ZaloActivityBackfillStatus.Running)
        {
            job.Status = ZaloActivityBackfillStatus.Queued;
            job.Stage = ZaloActivityBackfillStage.Queued;
            job.IsFullBackfill = full || job.BackfillCompletedAt is null;
            job.NextAttemptAt = null;
            job.LastErrorSummary = null;
            job.RetryCount = 0;
            job.LeaseToken = null;
            job.LeaseUntil = null;
            job.UpdatedAt = now;
            if (full)
            {
                ResetScanCheckpoint(job);
                job.BackfillStartedAt = null;
                job.BackfillCompletedAt = null;
            }
            else
            {
                job.BoardPage = 1;
                job.LastBoardPageFingerprint = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Queued Zalo activity sync JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} Full={Full}",
            job.Id,
            connectionId,
            groupId,
            job.IsFullBackfill);
        return job;
    }

    public async Task<int> QueueMissingLinkedGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var linkedGroups = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.BotEnabled &&
                session.ZaloConnectionId != null &&
                session.ZaloGroupId != null)
            .Select(session => new
            {
                ConnectionId = session.ZaloConnectionId!,
                GroupId = session.ZaloGroupId!
            })
            .Distinct()
            .ToListAsync(cancellationToken);
        var existingJobs = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .Select(job => new { job.ZaloConnectionId, job.GroupId })
            .ToListAsync(cancellationToken);
        var existingKeys = existingJobs
            .Select(item => $"{item.ZaloConnectionId}\u001f{item.GroupId}")
            .ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var linked in linkedGroups)
        {
            if (existingKeys.Contains($"{linked.ConnectionId}\u001f{linked.GroupId}"))
                continue;
            await QueueGroupAsync(
                linked.ConnectionId,
                linked.GroupId,
                true,
                cancellationToken);
            queued++;
        }

        if (queued > 0)
            logger.LogInformation("Queued initial Zalo activity backfill for {Count} existing linked groups.", queued);
        return queued;
    }

    public async Task<ServiceResult<ZaloActivityBackfillStatusResponse>> GetStatusForSessionAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var linked = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId && session.AdminUserId == adminUserId)
            .Select(session => new { session.ZaloConnectionId, session.ZaloGroupId })
            .SingleOrDefaultAsync(cancellationToken);
        if (linked is null)
            return ServiceResult<ZaloActivityBackfillStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy buổi đấu.");
        if (string.IsNullOrWhiteSpace(linked.ZaloConnectionId) || string.IsNullOrWhiteSpace(linked.ZaloGroupId))
            return ServiceResult<ZaloActivityBackfillStatusResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Buổi đấu chưa liên kết nhóm Zalo.");

        var job = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ZaloConnectionId == linked.ZaloConnectionId &&
                        item.GroupId == linked.ZaloGroupId,
                cancellationToken);
        return job is null
            ? ServiceResult<ZaloActivityBackfillStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Nhóm này chưa có tiến trình đồng bộ hoạt động.")
            : ServiceResult<ZaloActivityBackfillStatusResponse>.Success(ToStatus(job));
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddMinutes(-incrementalMinutes);
        // DateTimeOffset comparisons are translated by PostgreSQL but not by
        // Microsoft.EntityFrameworkCore.Sqlite. Load only eligible statuses
        // first, then apply time/lease checks in memory so local SQLite uses
        // the same scheduling semantics.
        var candidateJobs = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .Where(job =>
                job.Status == ZaloActivityBackfillStatus.Queued ||
                job.Status == ZaloActivityBackfillStatus.FailedRetryable ||
                job.Status == ZaloActivityBackfillStatus.Completed ||
                job.Status == ZaloActivityBackfillStatus.CompletedWithLimitations)
            .ToListAsync(cancellationToken);
        var candidates = candidateJobs
            .Where(job =>
                (job.LeaseUntil == null || job.LeaseUntil < now) &&
                (
                    job.Status == ZaloActivityBackfillStatus.Queued ||
                    (job.Status == ZaloActivityBackfillStatus.FailedRetryable &&
                     (job.NextAttemptAt == null || job.NextAttemptAt <= now)) ||
                    ((job.Status == ZaloActivityBackfillStatus.Completed ||
                      job.Status == ZaloActivityBackfillStatus.CompletedWithLimitations) &&
                     (job.LastIncrementalSyncAt == null || job.LastIncrementalSyncAt < staleBefore))
                ))
            .OrderBy(job => job.Status == ZaloActivityBackfillStatus.Queued ? 0 : 1)
            .ThenBy(job => job.NextAttemptAt)
            .ThenBy(job => job.UpdatedAt)
            .Select(job => job.Id)
            .Take(5)
            .ToList();

        foreach (var candidateId in candidates)
        {
            var leaseToken = Guid.NewGuid().ToString("n");
            int claimed;
            if (db.Database.IsSqlite())
            {
                var sqliteCandidate = await db.ZaloActivityBackfillJobs
                    .SingleOrDefaultAsync(job => job.Id == candidateId, cancellationToken);
                if (sqliteCandidate is null ||
                    sqliteCandidate.LeaseUntil is not null &&
                    sqliteCandidate.LeaseUntil >= now)
                {
                    claimed = 0;
                }
                else
                {
                    sqliteCandidate.LeaseToken = leaseToken;
                    sqliteCandidate.LeaseUntil = now.AddMinutes(leaseMinutes);
                    sqliteCandidate.Status = ZaloActivityBackfillStatus.Running;
                    sqliteCandidate.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    claimed = 1;
                }
            }
            else
            {
                claimed = await db.ZaloActivityBackfillJobs
                    .Where(job => job.Id == candidateId &&
                                  (job.LeaseUntil == null || job.LeaseUntil < now))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.LeaseToken, leaseToken)
                        .SetProperty(job => job.LeaseUntil, now.AddMinutes(leaseMinutes))
                        .SetProperty(job => job.Status, ZaloActivityBackfillStatus.Running)
                        .SetProperty(job => job.UpdatedAt, now), cancellationToken);
            }
            if (claimed == 0)
                continue;

            db.ChangeTracker.Clear();
            var job = await db.ZaloActivityBackfillJobs
                .SingleAsync(
                    item => item.Id == candidateId && item.LeaseToken == leaseToken,
                    cancellationToken);
            if (job.BackfillCompletedAt is not null && job.Stage == ZaloActivityBackfillStage.Completed)
            {
                job.IsFullBackfill = false;
                job.BoardPage = 1;
                job.LastBoardPageFingerprint = null;
            }

            await ProcessClaimedAsync(job, leaseToken, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task ProcessClaimedAsync(
        ZaloActivityBackfillJob job,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var limitations = new List<string>();
        try
        {
            var connection = await db.ZaloConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == job.ZaloConnectionId, cancellationToken);
            var credentials = ReadCredentials(connection);
            var now = DateTimeOffset.UtcNow;
            job.BackfillStartedAt ??= now;
            job.LastErrorSummary = null;
            await SaveStageAsync(job, ZaloActivityBackfillStage.SyncingMembers, cancellationToken);

            var directory = await WithRetryAsync(
                () => bridge.GetGroupMemberDirectoryAsync(credentials, job.GroupId, cancellationToken),
                "group members",
                cancellationToken);
            job.GroupCreatedAtFromZalo = FromUnixMs(directory.GroupCreatedAtUnixMs);
            job.MembersSynchronized = await SynchronizeMembersAsync(
                job,
                directory,
                cancellationToken);
            if (!directory.IsComplete)
                limitations.Add(
                    $"Danh sách thành viên Zalo chưa đầy đủ ({directory.Members.Count}/{directory.ExpectedMemberCount}).");

            await SaveStageAsync(job, ZaloActivityBackfillStage.ScanningBoard, cancellationToken);
            var pageLimit = job.IsFullBackfill ? maxBoardPages : incrementalBoardPages;
            var pagesProcessed = 0;
            var totalSeenInRun = 0;
            var boardCompleted = false;
            var pollFailures = 0;

            while (pagesProcessed < pageLimit && !boardCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageNumber = Math.Max(1, job.BoardPage);
                var page = await WithRetryAsync(
                    () => bridge.GetBoardPageAsync(
                        credentials,
                        job.GroupId,
                        pageNumber,
                        boardPageSize,
                        cancellationToken),
                    $"board page {pageNumber}",
                    cancellationToken);
                pagesProcessed++;
                totalSeenInRun += page.Items.Count;
                job.DiscoveredTotal = page.TotalCount > 0 ? page.TotalCount : job.DiscoveredTotal;

                var fingerprint = Fingerprint(page.Items.Select(item => item.StableId));
                if (page.Items.Count == 0 ||
                    (!string.IsNullOrWhiteSpace(job.LastBoardPageFingerprint) &&
                     string.Equals(job.LastBoardPageFingerprint, fingerprint, StringComparison.Ordinal)))
                {
                    boardCompleted = true;
                    break;
                }

                job.TotalBoardItemsScanned += page.Items.Count;
                job.ProcessedCount += page.Items.Count;
                await SaveStageAsync(job, ZaloActivityBackfillStage.SyncingPollDetails, cancellationToken);

                foreach (var boardItem in page.Items.Where(item => item.IsPoll && !string.IsNullOrWhiteSpace(item.PollId)))
                {
                    try
                    {
                        var poll = await WithRetryAsync(
                            () => bridge.GetPollAsync(credentials, boardItem.PollId!),
                            $"poll {boardItem.PollId}",
                            cancellationToken);
                        await SynchronizePollAsync(job, poll, cancellationToken);
                    }
                    catch (Exception exception) when (IsTransient(exception))
                    {
                        pollFailures++;
                        limitations.Add($"Không đọc được poll {boardItem.PollId}: {SafeError(exception)}");
                        logger.LogWarning(
                            exception,
                            "Zalo poll sync failed JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} PollId={PollId}",
                            job.Id,
                            job.ZaloConnectionId,
                            job.GroupId,
                            boardItem.PollId);
                    }

                    await PauseAsync(cancellationToken);
                }

                job.LastBoardPageFingerprint = fingerprint;
                job.BoardPage = pageNumber + 1;
                job.Stage = ZaloActivityBackfillStage.ScanningBoard;
                await RenewAndSaveAsync(job, leaseToken, cancellationToken);

                boardCompleted =
                    page.Items.Count < page.PageSize ||
                    (page.TotalCount > 0 && totalSeenInRun >= page.TotalCount);
                await PauseAsync(cancellationToken);
            }

            if (job.IsFullBackfill && !boardCompleted)
                limitations.Add($"Đã dừng ở ngưỡng an toàn {pageLimit} trang board; có thể vẫn còn dữ liệu cũ.");
            if (pollFailures > 0)
                limitations.Add($"{pollFailures} poll lỗi tạm thời và sẽ được thử lại ở lần đồng bộ sau.");

            await SaveStageAsync(job, ZaloActivityBackfillStage.ProbingMessageHistory, cancellationToken);
            await ProbeAndImportMessagesAsync(job, credentials, limitations, cancellationToken);

            await SaveStageAsync(job, ZaloActivityBackfillStage.RebuildingMetrics, cancellationToken);
            await RefreshCoverageAsync(job, cancellationToken);

            now = DateTimeOffset.UtcNow;
            job.Stage = ZaloActivityBackfillStage.Completed;
            job.Status = limitations.Count == 0
                ? ZaloActivityBackfillStatus.Completed
                : ZaloActivityBackfillStatus.CompletedWithLimitations;
            job.BackfillCompletedAt ??= now;
            job.LastIncrementalSyncAt = now;
            job.LastErrorSummary = limitations.Count == 0
                ? null
                : string.Join(" ", limitations.Distinct().Take(5));
            job.RetryCount = 0;
            job.NextAttemptAt = null;
            job.LeaseToken = null;
            job.LeaseUntil = null;
            job.IsFullBackfill = false;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Completed Zalo activity sync JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} Status={Status} Members={Members} BoardItems={BoardItems} Polls={Polls} PollsWithVoters={PollsWithVoters} Messages={Messages} MessageCapability={MessageCapability}",
                job.Id,
                job.ZaloConnectionId,
                job.GroupId,
                job.Status,
                job.MembersSynchronized,
                job.TotalBoardItemsScanned,
                job.TotalPollsDiscovered,
                job.TotalPollsWithVoterIdentities,
                job.MessagesImported,
                job.MessageHistoryCapability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseOnCancellationAsync(job.Id, leaseToken);
            throw;
        }
        catch (Exception exception)
        {
            var now = DateTimeOffset.UtcNow;
            db.ChangeTracker.Clear();
            var failed = await db.ZaloActivityBackfillJobs
                .SingleOrDefaultAsync(
                    item => item.Id == job.Id && item.LeaseToken == leaseToken,
                    CancellationToken.None);
            if (failed is not null)
            {
                failed.RetryCount++;
                failed.Status = failed.RetryCount > maxRetries
                    ? ZaloActivityBackfillStatus.FailedPermanent
                    : ZaloActivityBackfillStatus.FailedRetryable;
                failed.NextAttemptAt = failed.Status == ZaloActivityBackfillStatus.FailedRetryable
                    ? now.AddSeconds(Math.Min(300, Math.Pow(2, failed.RetryCount) * 10))
                    : null;
                failed.LastErrorSummary = SafeError(exception);
                failed.LeaseToken = null;
                failed.LeaseUntil = null;
                failed.UpdatedAt = now;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            logger.LogError(
                exception,
                "Zalo activity sync failed JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} Stage={Stage} RetryCount={RetryCount}",
                job.Id,
                job.ZaloConnectionId,
                job.GroupId,
                job.Stage,
                failed?.RetryCount ?? job.RetryCount);
        }
    }

    private async Task<int> SynchronizeMembersAsync(
        ZaloActivityBackfillJob job,
        BridgeGroupMemberDirectory directory,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.ZaloGroupMembers
            .Where(member =>
                member.ZaloConnectionId == job.ZaloConnectionId &&
                member.GroupId == job.GroupId)
            .ToDictionaryAsync(member => member.ZaloUserId, StringComparer.Ordinal, cancellationToken);
        var returnedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in directory.Members.Where(member => !string.IsNullOrWhiteSpace(member.ZaloUserId)))
        {
            returnedIds.Add(source.ZaloUserId);
            var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? source.ZaloName ?? source.ZaloUserId
                : source.DisplayName;
            if (!existing.TryGetValue(source.ZaloUserId, out var member))
            {
                member = new ZaloGroupMember
                {
                    ZaloConnectionId = job.ZaloConnectionId,
                    GroupId = job.GroupId,
                    ZaloUserId = source.ZaloUserId,
                    DisplayName = displayName,
                    AvatarUrl = source.AvatarUrl,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    LastSyncedAt = now,
                    IsCurrentMember = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ZaloGroupMembers.Add(member);
                existing[source.ZaloUserId] = member;
            }
            else
            {
                member.DisplayName = displayName;
                member.AvatarUrl = source.AvatarUrl ?? member.AvatarUrl;
                member.LastSeenAt = now;
                member.LastSyncedAt = now;
                member.IsCurrentMember = true;
                member.LeftAt = null;
                member.UpdatedAt = now;
            }
        }

        // Only mark departures when Zalo proves that the returned directory is complete.
        if (directory.IsComplete)
        {
            foreach (var member in existing.Values.Where(member =>
                         member.IsCurrentMember && !returnedIds.Contains(member.ZaloUserId)))
            {
                member.IsCurrentMember = false;
                member.LeftAt = now;
                member.LastSyncedAt = now;
                member.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Synchronized Zalo members JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} Returned={Returned} Expected={Expected} Complete={Complete}",
            job.Id,
            job.ZaloConnectionId,
            job.GroupId,
            returnedIds.Count,
            directory.ExpectedMemberCount,
            directory.IsComplete);
        return returnedIds.Count;
    }

    private async Task SynchronizePollAsync(
        ZaloActivityBackfillJob job,
        BridgePoll source,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var poll = await db.ZaloPollSnapshots
            .Include(item => item.Options)
            .ThenInclude(option => option.Votes)
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == job.ZaloConnectionId &&
                item.GroupId == job.GroupId &&
                item.PollId == source.Id, cancellationToken);

        if (poll is null)
        {
            poll = new ZaloPollSnapshot
            {
                ZaloConnectionId = job.ZaloConnectionId,
                GroupId = job.GroupId,
                PollId = source.Id,
                FirstObservedAt = now,
                CreatedAt = now
            };
            db.ZaloPollSnapshots.Add(poll);
        }

        var distinctVoterCount = source.Options
            .SelectMany(option => option.VoterIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var hasVoterIdentities = !source.IsAnonymous &&
                                 (source.UniqueVoteCount == 0 || distinctVoterCount > 0);
        var createdAt = FromUnixMs(source.CreatedAtUnixMs);
        poll.Question = string.IsNullOrWhiteSpace(source.Question) ? "(Poll không có tiêu đề)" : source.Question;
        poll.CreatorZaloUserId = source.CreatorId;
        poll.CreatedAtFromZalo = createdAt;
        poll.UpdatedAtFromZalo = FromUnixMs(source.UpdatedAtUnixMs);
        poll.LastObservedAt = now;
        poll.IsClosed = source.IsClosed;
        poll.IsAnonymous = source.IsAnonymous;
        poll.AllowsMultipleChoices = source.AllowMultipleChoices;
        poll.HasVoterIdentities = hasVoterIdentities;
        poll.IsAnalyticsEligible = createdAt is not null && hasVoterIdentities;
        poll.ExclusionReason = source.IsAnonymous
            ? "AnonymousPoll"
            : source.UniqueVoteCount > 0 && distinctVoterCount == 0
                ? "VoterIdentitiesUnavailable"
                : createdAt is null
                    ? "MissingPollCreatedAt"
                    : null;
        poll.UpdatedAt = now;

        var optionsById = poll.Options.ToDictionary(option => option.ZaloOptionId, StringComparer.Ordinal);
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceOption in source.Options)
        {
            if (!optionsById.TryGetValue(sourceOption.Id, out var option))
            {
                option = new ZaloPollOptionSnapshot
                {
                    PollSnapshot = poll,
                    ZaloOptionId = sourceOption.Id,
                    Content = sourceOption.Content,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                poll.Options.Add(option);
                optionsById[sourceOption.Id] = option;
            }
            else
            {
                option.Content = sourceOption.Content;
                option.LastObservedAt = now;
                option.UpdatedAt = now;
            }

            var votesByUser = option.Votes.ToDictionary(vote => vote.ZaloUserId, StringComparer.Ordinal);
            foreach (var voterId in sourceOption.VoterIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.Ordinal))
            {
                selectedKeys.Add($"{sourceOption.Id}\u001f{voterId}");
                if (!votesByUser.TryGetValue(voterId, out var vote))
                {
                    vote = new ZaloPollVoteActivity
                    {
                        PollSnapshot = poll,
                        PollOptionSnapshot = option,
                        ZaloUserId = voterId,
                        FirstObservedAt = now,
                        LastObservedAt = now,
                        IsCurrentlySelected = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    option.Votes.Add(vote);
                }
                else
                {
                    vote.LastObservedAt = now;
                    vote.IsCurrentlySelected = true;
                    vote.RemovedObservedAt = null;
                    vote.UpdatedAt = now;
                }
            }
        }

        foreach (var vote in poll.Options.SelectMany(option => option.Votes))
        {
            var optionId = vote.PollOptionSnapshot?.ZaloOptionId ??
                           poll.Options.First(option => option.Id == vote.PollOptionSnapshotId).ZaloOptionId;
            if (selectedKeys.Contains($"{optionId}\u001f{vote.ZaloUserId}"))
                continue;
            if (vote.IsCurrentlySelected)
            {
                vote.IsCurrentlySelected = false;
                vote.RemovedObservedAt = now;
                vote.LastObservedAt = now;
                vote.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        job.LastSuccessfulPollSyncAt = now;
    }

    private async Task ProbeAndImportMessagesAsync(
        ZaloActivityBackfillJob job,
        JsonElement credentials,
        List<string> limitations,
        CancellationToken cancellationToken)
    {
        BridgeMessageHistoryProbe history;
        try
        {
            history = await WithRetryAsync(
                () => bridge.GetGroupMessageHistoryAsync(
                    credentials,
                    job.GroupId,
                    messageHistoryCount,
                    cancellationToken),
                "message history",
                cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            job.MessageHistoryCapability = ZaloMessageHistoryCapability.RealtimeOnly;
            limitations.Add(
                "Zalo không trả được lịch sử chat cũ; thống kê tin nhắn hiện chỉ dùng dữ liệu listener.");
            logger.LogWarning(
                exception,
                "Zalo message history probe failed JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId}",
                job.Id,
                job.ZaloConnectionId,
                job.GroupId);
            return;
        }

        await SaveStageAsync(job, ZaloActivityBackfillStage.ImportingMessages, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var ids = history.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message.MessageId))
            .Select(message => message.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var existing = ids.Count == 0
            ? new Dictionary<string, ZaloGroupMessage>(StringComparer.Ordinal)
            : await db.ZaloGroupMessages
                .Where(message =>
                    message.ZaloConnectionId == job.ZaloConnectionId &&
                    ids.Contains(message.MessageId))
                .ToDictionaryAsync(message => message.MessageId, StringComparer.Ordinal, cancellationToken);

        foreach (var source in history.Messages.Where(message =>
                     !string.IsNullOrWhiteSpace(message.MessageId) &&
                     !string.IsNullOrWhiteSpace(message.SenderId)))
        {
            var sentAt = FromUnixMs(source.SentAtUnixMs);
            if (sentAt is null)
                continue;

            if (!existing.TryGetValue(source.MessageId, out var message))
            {
                message = new ZaloGroupMessage
                {
                    ZaloConnectionId = job.ZaloConnectionId,
                    GroupId = job.GroupId,
                    MessageId = source.MessageId,
                    SenderId = source.SenderId,
                    SenderName = source.SenderName,
                    Content = source.Content,
                    MessageType = source.MessageType,
                    ObservationSource = "HistoricalBackfill",
                    IsFromBot = source.IsFromBot,
                    SentAt = sentAt.Value,
                    ReceivedAt = now,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    ReplyOutcome = "HistoricalOnly"
                };
                db.ZaloGroupMessages.Add(message);
                existing[source.MessageId] = message;
            }
            else
            {
                message.SenderId = source.SenderId;
                message.SenderName = source.SenderName;
                message.MessageType = source.MessageType;
                message.IsFromBot = source.IsFromBot;
                message.SentAt = sentAt.Value;
                message.LastObservedAt = now;
                if (string.IsNullOrWhiteSpace(message.Content))
                    message.Content = source.Content;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        job.MessageCursor = JsonSerializer.Serialize(new
        {
            history.LastActionId,
            history.LastActionIdOther,
            history.More
        });
        job.OldestRetrievableMessageAt = FromUnixMs(history.OldestMessageAtUnixMs);
        job.NewestRetrievableMessageAt = FromUnixMs(history.NewestMessageAtUnixMs);

        var reachesGroupStart =
            job.GroupCreatedAtFromZalo is not null &&
            job.OldestRetrievableMessageAt is not null &&
            job.OldestRetrievableMessageAt <= job.GroupCreatedAtFromZalo.Value.AddDays(1);
        job.MessageHistoryCapability = history.ReturnedCount == 0
            ? ZaloMessageHistoryCapability.PartialHistoricalBackfill
            : history.More == 0 && reachesGroupStart
                ? ZaloMessageHistoryCapability.FullHistoricalBackfill
                : ZaloMessageHistoryCapability.PartialHistoricalBackfill;

        if (job.MessageHistoryCapability != ZaloMessageHistoryCapability.FullHistoricalBackfill)
        {
            limitations.Add(history.More > 0
                ? $"Zalo trả {history.ReturnedCount} tin và báo vẫn còn lịch sử cũ, nhưng thư viện hiện không có cursor để lấy tiếp."
                : "Đã lấy được một phần lịch sử chat nhưng chưa chứng minh được dữ liệu bắt đầu từ lúc nhóm được tạo.");
        }
    }

    private async Task RefreshCoverageAsync(
        ZaloActivityBackfillJob job,
        CancellationToken cancellationToken)
    {
        var polls = db.ZaloPollSnapshots
            .Where(poll =>
                poll.ZaloConnectionId == job.ZaloConnectionId &&
                poll.GroupId == job.GroupId);
        job.TotalPollsDiscovered = await polls.CountAsync(cancellationToken);
        job.TotalPollsWithVoterIdentities = await polls
            .CountAsync(poll => poll.HasVoterIdentities, cancellationToken);
        job.TotalPollsExcluded = await polls
            .CountAsync(poll => !poll.IsAnalyticsEligible, cancellationToken);
        var pollDates = await polls
            .Where(poll => poll.CreatedAtFromZalo != null)
            .Select(poll => poll.CreatedAtFromZalo!.Value)
            .ToListAsync(cancellationToken);
        job.OldestRetrievablePollAt = pollDates.Count == 0 ? null : pollDates.Min();
        job.NewestRetrievablePollAt = pollDates.Count == 0 ? null : pollDates.Max();
        job.MembersSynchronized = await db.ZaloGroupMembers
            .CountAsync(member =>
                member.ZaloConnectionId == job.ZaloConnectionId &&
                member.GroupId == job.GroupId &&
                member.IsCurrentMember, cancellationToken);
        job.MessagesImported = await db.ZaloGroupMessages
            .CountAsync(message =>
                message.ZaloConnectionId == job.ZaloConnectionId &&
                message.GroupId == job.GroupId &&
                message.ObservationSource == "HistoricalBackfill", cancellationToken);
    }

    private async Task SaveStageAsync(
        ZaloActivityBackfillJob job,
        ZaloActivityBackfillStage stage,
        CancellationToken cancellationToken)
    {
        job.Stage = stage;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Zalo activity sync stage JobId={JobId} ConnectionId={ConnectionId} GroupId={GroupId} Stage={Stage} BoardPage={BoardPage} Processed={Processed}",
            job.Id,
            job.ZaloConnectionId,
            job.GroupId,
            stage,
            job.BoardPage,
            job.ProcessedCount);
    }

    private async Task RenewAndSaveAsync(
        ZaloActivityBackfillJob job,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Zalo activity sync lease was lost.");
        job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(leaseMinutes);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<T> WithRetryAsync<T>(
        Func<Task<T>> action,
        string category,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < maxRetries)
            {
                lastError = exception;
                var delay = Math.Min(30_000, retryDelayMs * (int)Math.Pow(2, attempt));
                logger.LogWarning(
                    "Transient Zalo sync failure Category={Category} Attempt={Attempt} DelayMs={DelayMs} Error={Error}",
                    category,
                    attempt + 1,
                    delay,
                    SafeError(exception));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException($"Zalo sync failed for {category}.");
    }

    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (pauseBetweenRequestsMs > 0)
            await Task.Delay(pauseBetweenRequestsMs, cancellationToken);
    }

    private async Task ReleaseLeaseOnCancellationAsync(string jobId, string leaseToken)
    {
        try
        {
            db.ChangeTracker.Clear();
            await db.ZaloActivityBackfillJobs
                .Where(job => job.Id == jobId && job.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, ZaloActivityBackfillStatus.FailedRetryable)
                    .SetProperty(job => job.NextAttemptAt, DateTimeOffset.UtcNow)
                    .SetProperty(job => job.LeaseToken, (string?)null)
                    .SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(job => job.UpdatedAt, DateTimeOffset.UtcNow));
        }
        catch
        {
            // The lease expires by itself if the host is shutting down abruptly.
        }
    }

    private JsonElement ReadCredentials(ZaloConnection connection)
    {
        using var document = JsonDocument.Parse(
            credentialProtector.Unprotect(connection.EncryptedCredentials));
        return document.RootElement.Clone();
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException;

    private static string SafeError(Exception exception)
    {
        var text = exception.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private static DateTimeOffset? FromUnixMs(long? unixMs)
    {
        if (unixMs is null or <= 0)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string Fingerprint(IEnumerable<string> ids)
    {
        var joined = string.Join('\u001f', ids);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private static int ReadBounded(
        IConfiguration configuration,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        return int.TryParse(configuration[key], out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private static void ResetScanCheckpoint(ZaloActivityBackfillJob job)
    {
        job.BoardPage = 1;
        job.BoardCursor = null;
        job.MessageCursor = null;
        job.LastBoardPageFingerprint = null;
        job.ProcessedCount = 0;
        job.DiscoveredTotal = null;
        job.TotalBoardItemsScanned = 0;
    }

    internal static ZaloActivityBackfillStatusResponse ToStatus(ZaloActivityBackfillJob job) =>
        new(
            job.Id,
            job.Stage,
            job.Status,
            job.MembersSynchronized,
            job.TotalBoardItemsScanned,
            job.TotalPollsDiscovered,
            job.TotalPollsWithVoterIdentities,
            job.TotalPollsExcluded,
            job.MessagesImported,
            job.ProcessedCount,
            job.DiscoveredTotal,
            job.MessageHistoryCapability,
            job.OldestRetrievablePollAt,
            job.NewestRetrievablePollAt,
            job.OldestRetrievableMessageAt,
            job.NewestRetrievableMessageAt,
            job.LastIncrementalSyncAt,
            job.BackfillStartedAt,
            job.BackfillCompletedAt,
            job.LastErrorSummary);
}

public sealed class ZaloActivityBackfillWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloActivityBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextLegacyDiscoveryAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider
                    .GetRequiredService<ZaloActivityBackfillCoordinator>();
                if (DateTimeOffset.UtcNow >= nextLegacyDiscoveryAt)
                {
                    await coordinator.QueueMissingLinkedGroupsAsync(stoppingToken);
                    nextLegacyDiscoveryAt = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                var processed = await coordinator.ProcessNextAsync(stoppingToken);
                if (!processed)
                    await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled Zalo activity backfill worker failure.");
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
        }
    }
}
