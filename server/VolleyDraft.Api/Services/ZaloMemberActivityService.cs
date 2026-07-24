using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public enum ZaloMemberActivityFilter
{
    All,
    NoVote,
    NoMessage,
    Inactive,
    AtRisk
}

public sealed record ZaloActivityPeriod(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Description);

public sealed partial class ZaloMemberActivityService(
    VolleyDraftDbContext db,
    IConfiguration configuration,
    ILogger<ZaloMemberActivityService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private readonly int newMemberDays = ReadBounded(configuration, "ZaloActivityRules:NewMemberDays", 14, 1, 90);
    private readonly int activeDays = ReadBounded(configuration, "ZaloActivityRules:ActiveDays", 14, 1, 90);
    private readonly int regularDays = ReadBounded(configuration, "ZaloActivityRules:RegularDays", 30, 7, 180);
    private readonly int inactiveDays = ReadBounded(configuration, "ZaloActivityRules:InactiveDays", 90, 30, 730);
    private readonly int atRiskMissedPolls = ReadBounded(configuration, "ZaloActivityRules:AtRiskMissedPolls", 3, 1, 20);

    public async Task<ServiceResult<ZaloMemberActivityPageResponse>> QueryForSessionAsync(
        string adminUserId,
        string sessionId,
        ZaloActivityPeriod period,
        ZaloMemberActivityFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var linked = await ResolveOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (!linked.IsSuccess)
            return ServiceResult<ZaloMemberActivityPageResponse>.Failure(
                linked.StatusCode,
                linked.Error!);

        var response = await QueryGroupAsync(
            linked.ConnectionId!,
            linked.GroupId!,
            period,
            filter,
            page,
            pageSize,
            cancellationToken);
        return ServiceResult<ZaloMemberActivityPageResponse>.Success(response);
    }

    public async Task<ServiceResult<ZaloGroupEngagementResponse>> GetGroupEngagementForSessionAsync(
        string adminUserId,
        string sessionId,
        ZaloActivityPeriod period,
        CancellationToken cancellationToken)
    {
        var linked = await ResolveOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (!linked.IsSuccess)
            return ServiceResult<ZaloGroupEngagementResponse>.Failure(linked.StatusCode, linked.Error!);

        var all = await QueryGroupAsync(
            linked.ConnectionId!,
            linked.GroupId!,
            period,
            ZaloMemberActivityFilter.All,
            1,
            5000,
            cancellationToken);
        var items = all.Items;
        return ServiceResult<ZaloGroupEngagementResponse>.Success(
            new ZaloGroupEngagementResponse(
                all.TotalItems,
                items.Count(item => item.VotedPollCount > 0),
                items.Count(item => item.MessageCount > 0),
                items.Count(item => item.VotedPollCount == 0),
                items.Count(item => item.MessageCount == 0),
                items.Count(item => item.EngagementStatus == ZaloEngagementStatus.InsufficientData),
                period.Start,
                period.End,
                all.Coverage));
    }

    public async Task<ZaloMemberActivityPageResponse> QueryGroupAsync(
        string connectionId,
        string groupId,
        ZaloActivityPeriod period,
        ZaloMemberActivityFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);
        var now = DateTimeOffset.UtcNow;

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member =>
                member.ZaloConnectionId == connectionId &&
                member.GroupId == groupId &&
                member.IsCurrentMember)
            .OrderBy(member => member.DisplayName)
            .ToListAsync(cancellationToken);

        var allPolls = await db.ZaloPollSnapshots
            .AsNoTracking()
            .Where(poll =>
                poll.ZaloConnectionId == connectionId &&
                poll.GroupId == groupId &&
                poll.IsAnalyticsEligible &&
                poll.CreatedAtFromZalo != null)
            .Select(poll => new PollFact(
                poll.Id,
                poll.Question,
                poll.CreatedAtFromZalo!.Value,
                poll.UpdatedAtFromZalo,
                poll.FirstObservedAt))
            .ToListAsync(cancellationToken);
        allPolls = allPolls
            .OrderBy(poll => poll.CreatedAt)
            .ToList();
        var periodPolls = allPolls
            .Where(poll => poll.CreatedAt >= period.Start && poll.CreatedAt < period.End)
            .ToList();
        var periodPollIds = periodPolls.Select(poll => poll.Id).ToList();
        var allPollIds = allPolls.Select(poll => poll.Id).ToList();

        var allVotes = allPollIds.Count == 0
            ? []
            : await db.ZaloPollVoteActivities
                .AsNoTracking()
                .Where(vote =>
                    allPollIds.Contains(vote.PollSnapshotId) &&
                    vote.IsCurrentlySelected)
                .Select(vote => new VoteFact(
                    vote.PollSnapshotId,
                    vote.ZaloUserId,
                    vote.PollOptionSnapshotId,
                    vote.FirstObservedAt))
                .ToListAsync(cancellationToken);
        var votesByMember = allVotes
            .GroupBy(vote => vote.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var messageFacts = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                !message.IsFromBot)
            .Select(message => new { message.SenderId, message.SentAt })
            .ToListAsync(cancellationToken);
        var messages = messageFacts
            .GroupBy(message => message.SenderId, StringComparer.Ordinal)
            .Select(group => new MessageAggregate(
                group.Key,
                group.Max(message => (DateTimeOffset?)message.SentAt),
                group.Count(message => message.SentAt >= period.Start && message.SentAt < period.End),
                group.Where(message => message.SentAt >= period.Start && message.SentAt < period.End)
                    .Select(message => message.SentAt.ToOffset(VietnamOffset).Date)
                    .Distinct()
                    .Count()))
            .ToList();
        var messagesByMember = messages.ToDictionary(message => message.UserId, StringComparer.Ordinal);

        var midpoint = period.Start + TimeSpan.FromTicks((period.End - period.Start).Ticks / 2);
        var previousPolls = periodPolls.Where(poll => poll.CreatedAt < midpoint).ToList();
        var recentPolls = periodPolls.Where(poll => poll.CreatedAt >= midpoint).ToList();
        var pollById = allPolls.ToDictionary(poll => poll.Id, StringComparer.Ordinal);
        var results = new List<ZaloMemberActivityResponse>(members.Count);

        foreach (var member in members)
        {
            votesByMember.TryGetValue(member.ZaloUserId, out var memberVotes);
            memberVotes ??= [];
            var distinctVotedPollIds = memberVotes
                .Select(vote => vote.PollId)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var votedInPeriod = periodPolls
                .Where(poll => distinctVotedPollIds.Contains(poll.Id))
                .ToList();
            var lastPoll = allPolls
                .Where(poll => distinctVotedPollIds.Contains(poll.Id))
                .OrderByDescending(poll => poll.CreatedAt)
                .FirstOrDefault();
            var lastPollFirstObservedAt = lastPoll is null
                ? null
                : memberVotes
                    .Where(vote => vote.PollId == lastPoll.Id)
                    .Min(vote => (DateTimeOffset?)vote.FirstObservedAt);
            messagesByMember.TryGetValue(member.ZaloUserId, out var message);
            var participation = Rate(votedInPeriod.Count, periodPolls.Count);
            var previousRate = Rate(
                previousPolls.Count(poll => distinctVotedPollIds.Contains(poll.Id)),
                previousPolls.Count);
            var recentRate = Rate(
                recentPolls.Count(poll => distinctVotedPollIds.Contains(poll.Id)),
                recentPolls.Count);
            var trend = DetermineTrend(previousRate, recentRate);
            var consecutiveMissed = CountConsecutiveMissed(periodPolls, distinctVotedPollIds);
            var lastActivity = Newest(message?.LastMessageAt, lastPoll?.CreatedAt);
            var lastActivitySource = lastActivity is null
                ? null
                : message?.LastMessageAt == lastActivity
                    ? "Message"
                    : "Poll";
            var isNew = member.FirstSeenAt >= now.AddDays(-newMemberDays);
            var status = DetermineStatus(
                isNew,
                lastActivity,
                participation,
                consecutiveMissed,
                trend,
                periodPolls.Count,
                message?.PeriodMessageCount ?? 0,
                now);
            var selectedOptions = memberVotes
                .Where(vote => periodPollIds.Contains(vote.PollId))
                .Select(vote => $"{vote.PollId}\u001f{vote.OptionId}")
                .Distinct(StringComparer.Ordinal)
                .Count();

            results.Add(new ZaloMemberActivityResponse(
                member.ZaloUserId,
                member.DisplayName,
                member.AvatarUrl,
                member.IsCurrentMember,
                isNew,
                member.FirstSeenAt,
                message?.LastMessageAt,
                message?.PeriodMessageCount ?? 0,
                message?.ActiveDays ?? 0,
                lastPoll?.CreatedAt,
                lastPoll?.UpdatedAt,
                lastPollFirstObservedAt,
                null, // zca-js does not provide an exact per-user vote timestamp.
                lastPoll?.Question,
                periodPolls.Count,
                votedInPeriod.Count,
                selectedOptions,
                participation,
                previousRate,
                recentRate,
                consecutiveMissed,
                trend,
                lastActivity,
                lastActivitySource,
                status,
                DetermineConfidence(periodPolls.Count, message?.PeriodMessageCount ?? 0)));
        }

        IEnumerable<ZaloMemberActivityResponse> filtered = filter switch
        {
            ZaloMemberActivityFilter.NoVote => results.Where(item => item.VotedPollCount == 0),
            ZaloMemberActivityFilter.NoMessage => results.Where(item => item.MessageCount == 0),
            ZaloMemberActivityFilter.Inactive => results.Where(item =>
                item.VotedPollCount == 0 && item.MessageCount == 0),
            ZaloMemberActivityFilter.AtRisk => results.Where(item =>
                item.EngagementStatus is ZaloEngagementStatus.AtRisk or ZaloEngagementStatus.Inactive),
            _ => results
        };
        filtered = filter is ZaloMemberActivityFilter.Inactive or ZaloMemberActivityFilter.AtRisk
            ? filtered
                .OrderBy(item => EngagementOrder(item.EngagementStatus))
                .ThenBy(item => item.LastActivityAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.DisplayName)
            : filtered.OrderBy(item => item.DisplayName);

        var materialized = filtered.ToList();
        var total = materialized.Count;
        var items = materialized
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        var coverage = await BuildCoverageAsync(
            connectionId,
            groupId,
            period,
            periodPolls.Count,
            cancellationToken);

        logger.LogInformation(
            "Queried Zalo member activity ConnectionId={ConnectionId} GroupId={GroupId} Filter={Filter} PeriodStart={PeriodStart} PeriodEnd={PeriodEnd} ResultCount={ResultCount} Page={Page} PageSize={PageSize}",
            connectionId,
            groupId,
            filter,
            period.Start,
            period.End,
            total,
            page,
            pageSize);
        return new ZaloMemberActivityPageResponse(
            items,
            page,
            pageSize,
            total,
            Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
            period.Start,
            period.End,
            coverage);
    }

    public async Task<ZaloMemberActivityResponse?> GetMemberActivityAsync(
        string connectionId,
        string groupId,
        string zaloUserId,
        ZaloActivityPeriod period,
        CancellationToken cancellationToken)
    {
        var page = await QueryGroupAsync(
            connectionId,
            groupId,
            period,
            ZaloMemberActivityFilter.All,
            1,
            5000,
            cancellationToken);
        return page.Items.SingleOrDefault(item =>
            string.Equals(item.ZaloUserId, zaloUserId, StringComparison.Ordinal));
    }

    public static ZaloActivityPeriod ParsePeriod(
        string? text,
        DateTimeOffset? currentVietnamTime = null)
    {
        var localNow = (currentVietnamTime ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);
        var normalized = ZaloBotIntelligence.Normalize(text ?? string.Empty);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var endDate = today.AddDays(1);
        DateOnly startDate;
        string description;

        var explicitRange = ExplicitRangeRegex().Match(normalized);
        if (explicitRange.Success &&
            TryDate(explicitRange.Groups[1].Value, today.Year, out var explicitStart) &&
            TryDate(explicitRange.Groups[2].Value, today.Year, out var explicitEnd))
        {
            startDate = explicitStart;
            endDate = explicitEnd.AddDays(1);
            description = $"từ {explicitStart:dd/MM/yyyy} đến {explicitEnd:dd/MM/yyyy}";
        }
        else if (normalized.Contains("tuan nay", StringComparison.Ordinal))
        {
            var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
            startDate = today.AddDays(-daysFromMonday);
            description = "tuần này";
        }
        else if (normalized.Contains("thang nay", StringComparison.Ordinal))
        {
            startDate = new DateOnly(today.Year, today.Month, 1);
            description = "tháng này";
        }
        else if (normalized.Contains("nam nay", StringComparison.Ordinal))
        {
            startDate = new DateOnly(today.Year, 1, 1);
            description = "năm nay";
        }
        else if (FromMonthRegex().Match(normalized) is { Success: true } fromMonth &&
                 int.TryParse(fromMonth.Groups[1].Value, out var month) &&
                 month is >= 1 and <= 12)
        {
            var year = month <= today.Month ? today.Year : today.Year - 1;
            startDate = new DateOnly(year, month, 1);
            description = $"từ tháng {month}";
        }
        else if (DurationRegex().Match(normalized) is { Success: true } duration &&
                 TryVietnameseNumber(duration.Groups[1].Value, out var amount))
        {
            amount = Math.Clamp(amount, 1, 3650);
            var unit = duration.Groups[2].Value;
            if (unit.StartsWith("thang", StringComparison.Ordinal))
            {
                startDate = today.AddMonths(-Math.Min(amount, 120));
                description = $"{amount} tháng gần đây";
            }
            else if (unit.StartsWith("tuan", StringComparison.Ordinal))
            {
                startDate = today.AddDays(-Math.Min(amount, 520) * 7);
                description = $"{amount} tuần gần đây";
            }
            else
            {
                startDate = today.AddDays(-amount);
                description = $"{amount} ngày gần đây";
            }
        }
        else
        {
            startDate = today.AddDays(-90);
            description = "90 ngày gần đây";
        }

        return new ZaloActivityPeriod(
            AtVietnamMidnight(startDate),
            AtVietnamMidnight(endDate),
            description);
    }

    private async Task<ZaloActivityCoverageResponse> BuildCoverageAsync(
        string connectionId,
        string groupId,
        ZaloActivityPeriod period,
        int eligiblePollCount,
        CancellationToken cancellationToken)
    {
        var job = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId, cancellationToken);
        // SQLite cannot translate DateTimeOffset range comparisons reliably.
        // Keep the stable group/eligibility predicate in SQL and apply the
        // timestamp boundary in memory so PostgreSQL and local SQLite agree.
        var excludedPollDates = await db.ZaloPollSnapshots
            .AsNoTracking()
            .Where(poll =>
                poll.ZaloConnectionId == connectionId &&
                poll.GroupId == groupId &&
                !poll.IsAnalyticsEligible &&
                poll.CreatedAtFromZalo != null)
            .Select(poll => poll.CreatedAtFromZalo!.Value)
            .ToListAsync(cancellationToken);
        var excludedInRange = excludedPollDates.Count(createdAt =>
            createdAt >= period.Start && createdAt < period.End);
        var warnings = new List<string>();
        if (job is null)
            warnings.Add("Nhóm chưa chạy đồng bộ dữ liệu hoạt động.");
        else
        {
            if (job.Status is ZaloActivityBackfillStatus.Queued or
                ZaloActivityBackfillStatus.Running or
                ZaloActivityBackfillStatus.FailedRetryable)
                warnings.Add($"Đồng bộ dữ liệu Zalo chưa hoàn tất ({job.Stage}, {job.ProcessedCount}/{job.DiscoveredTotal?.ToString() ?? "?"}).");
            if (job.OldestRetrievablePollAt is not null && period.Start < job.OldestRetrievablePollAt)
                warnings.Add($"Dữ liệu poll cũ nhất Zalo trả về là {job.OldestRetrievablePollAt:dd/MM/yyyy}.");
            if (excludedInRange > 0)
                warnings.Add($"{excludedInRange} poll trong khoảng này bị loại vì ẩn danh hoặc thiếu UID người vote.");
            if (job.MessageHistoryCapability != ZaloMessageHistoryCapability.FullHistoricalBackfill)
                warnings.Add("Lịch sử tin nhắn chưa được chứng minh là đầy đủ; số liệu chat có thể chỉ là một phần.");
        }

        return new ZaloActivityCoverageResponse(
            job?.Status is ZaloActivityBackfillStatus.Completed or
                ZaloActivityBackfillStatus.CompletedWithLimitations,
            job?.Status,
            job?.MessageHistoryCapability ?? ZaloMessageHistoryCapability.Unsupported,
            job?.OldestRetrievablePollAt,
            job?.NewestRetrievablePollAt,
            job?.OldestRetrievableMessageAt,
            job?.NewestRetrievableMessageAt,
            eligiblePollCount,
            excludedInRange,
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private async Task<LinkedGroupResult> ResolveOwnedSessionAsync(
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
            return LinkedGroupResult.Failure(StatusCodes.Status404NotFound, "Không tìm thấy buổi đấu.");
        if (string.IsNullOrWhiteSpace(linked.ZaloConnectionId) || string.IsNullOrWhiteSpace(linked.ZaloGroupId))
            return LinkedGroupResult.Failure(StatusCodes.Status400BadRequest, "Buổi đấu chưa liên kết nhóm Zalo.");
        return LinkedGroupResult.Success(linked.ZaloConnectionId, linked.ZaloGroupId);
    }

    private ZaloEngagementStatus DetermineStatus(
        bool isNew,
        DateTimeOffset? lastActivity,
        double? participation,
        int consecutiveMissed,
        string trend,
        int eligiblePollCount,
        int messageCount,
        DateTimeOffset now)
    {
        if (isNew)
            return ZaloEngagementStatus.New;
        var days = lastActivity is null
            ? int.MaxValue
            : Math.Max(0, (int)Math.Floor((now - lastActivity.Value).TotalDays));
        if (days >= inactiveDays)
            return ZaloEngagementStatus.Inactive;
        if (eligiblePollCount == 0 && messageCount == 0)
            return ZaloEngagementStatus.InsufficientData;
        if (consecutiveMissed >= atRiskMissedPolls ||
            trend == "Decreasing" ||
            days > regularDays)
            return ZaloEngagementStatus.AtRisk;
        if (days <= activeDays && (participation ?? 0) >= 0.6)
            return ZaloEngagementStatus.Active;
        if (days <= regularDays && (participation ?? 0) >= 0.35)
            return ZaloEngagementStatus.Regular;
        return ZaloEngagementStatus.Occasional;
    }

    private static int CountConsecutiveMissed(
        IReadOnlyList<PollFact> polls,
        IReadOnlySet<string> votedPollIds)
    {
        var missed = 0;
        foreach (var poll in polls.OrderByDescending(item => item.CreatedAt))
        {
            if (votedPollIds.Contains(poll.Id))
                break;
            missed++;
        }

        return missed;
    }

    private static string DetermineTrend(double? previous, double? recent)
    {
        if (previous is null || recent is null)
            return "InsufficientData";
        var difference = recent.Value - previous.Value;
        if (difference <= -0.15)
            return "Decreasing";
        if (difference >= 0.15)
            return "Increasing";
        return "Stable";
    }

    private static string DetermineConfidence(int eligiblePolls, int messages) =>
        eligiblePolls >= 5 || messages >= 10
            ? "High"
            : eligiblePolls > 0 || messages > 0
                ? "Medium"
                : "Insufficient";

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round(numerator / (double)denominator, 4);

    private static DateTimeOffset? Newest(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first >= second ? first : second;
    }

    private static int EngagementOrder(ZaloEngagementStatus status) => status switch
    {
        ZaloEngagementStatus.Inactive => 0,
        ZaloEngagementStatus.AtRisk => 1,
        ZaloEngagementStatus.Occasional => 2,
        ZaloEngagementStatus.InsufficientData => 3,
        ZaloEngagementStatus.New => 4,
        ZaloEngagementStatus.Regular => 5,
        _ => 6
    };

    private static DateTimeOffset AtVietnamMidnight(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();

    private static bool TryDate(string value, int defaultYear, out DateOnly date)
    {
        var formats = new[] { "d/M/yyyy", "dd/MM/yyyy", "d/M", "dd/MM" };
        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(
                    value,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
                continue;
            var year = format.Contains("yyyy", StringComparison.Ordinal) ? parsed.Year : defaultYear;
            date = new DateOnly(year, parsed.Month, parsed.Day);
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryVietnameseNumber(string value, out int number)
    {
        if (int.TryParse(value, out number))
            return true;
        number = value switch
        {
            "mot" => 1,
            "hai" => 2,
            "ba" => 3,
            "bon" or "tu" => 4,
            "nam" => 5,
            "sau" => 6,
            "bay" => 7,
            "tam" => 8,
            "chin" => 9,
            "muoi" => 10,
            _ => 0
        };
        return number > 0;
    }

    private static int ReadBounded(
        IConfiguration configuration,
        string key,
        int fallback,
        int minimum,
        int maximum) =>
        int.TryParse(configuration[key], out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    [GeneratedRegex(@"tu\s+(\d{1,2}/\d{1,2}(?:/\d{4})?)\s+den\s+(\d{1,2}/\d{1,2}(?:/\d{4})?)", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitRangeRegex();

    [GeneratedRegex(@"tu\s+thang\s+(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex FromMonthRegex();

    [GeneratedRegex(@"(\d+|mot|hai|ba|bon|tu|nam|sau|bay|tam|chin|muoi)\s*(ngay|tuan|thang)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    private sealed record PollFact(
        string Id,
        string Question,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset FirstObservedAt);

    private sealed record VoteFact(
        string PollId,
        string UserId,
        string OptionId,
        DateTimeOffset FirstObservedAt);

    private sealed record MessageAggregate(
        string UserId,
        DateTimeOffset? LastMessageAt,
        int PeriodMessageCount,
        int ActiveDays);

    private sealed record LinkedGroupResult(
        bool IsSuccess,
        string? ConnectionId,
        string? GroupId,
        int StatusCode,
        string? Error)
    {
        public static LinkedGroupResult Success(string connectionId, string groupId) =>
            new(true, connectionId, groupId, StatusCodes.Status200OK, null);

        public static LinkedGroupResult Failure(int statusCode, string error) =>
            new(false, null, null, statusCode, error);
    }
}
