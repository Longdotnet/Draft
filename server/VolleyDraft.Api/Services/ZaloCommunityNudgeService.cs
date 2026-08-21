using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloCommunityNudgeCandidate(
    string Type,
    string Text,
    string? SubjectName = null,
    string? SubjectUserId = null);

internal sealed record ZaloCommunityVoteMember(
    string UserId,
    string DisplayName,
    int VoteCount);

/// <summary>
/// Proactive, group-scoped member discovery and community-engagement messages.
/// The service only advertises self-service capabilities available to ordinary members,
/// and activity nudges are grounded in eligible Zalo poll votes from the last 30 days.
/// Low-activity nudges stay encouraging: analytics are used to select one member, but
/// the message never publishes a ranking of who is least active.
/// </summary>
public sealed class ZaloCommunityNudgeService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ILogger<ZaloCommunityNudgeService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private const int ActivityWindowDays = 30;
    private const int SubjectCooldownDays = 21;
    private const int MinimumPollsForActivityNudge = 4;
    private readonly ZaloCommunityNudgeStore store = new(db);

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        await store.EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var localNow = now.ToOffset(VietnamOffset);
        if (localNow.TimeOfDay < TimeSpan.FromHours(9.5) || localNow.TimeOfDay > TimeSpan.FromHours(21))
            return 0;

        var rows = await db.MatchSessions
            .AsNoTracking()
            .Where(item => item.BotEnabled &&
                           item.ZaloConnectionId != null &&
                           item.ZaloGroupId != null &&
                           item.ZaloConnection != null &&
                           item.ZaloConnection.Status == ZaloConnectionStatus.Connected)
            .Select(item => new
            {
                ConnectionId = item.ZaloConnectionId!,
                GroupId = item.ZaloGroupId!,
                AccountId = item.ZaloConnection!.AccountZaloId,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var targets = rows
            .GroupBy(item => $"{CleanId(item.ConnectionId)}\n{CleanId(item.GroupId)}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .Take(100)
            .ToList();

        var sent = 0;
        foreach (var target in targets)
        {
            var connectionId = CleanId(target.ConnectionId);
            var groupId = CleanId(target.GroupId);
            var accountId = CleanId(target.AccountId);
            if (connectionId.Length == 0 || groupId.Length == 0 || accountId.Length == 0) continue;

            var dailyCount = await store.GetDailyCountAsync(connectionId, groupId, cancellationToken);
            var localDate = localNow.ToString("yyyy-MM-dd");
            var history = await store.GetHistoryAsync(connectionId, groupId, 300, cancellationToken);
            var today = history
                .Where(item => string.Equals(item.LocalDate, localDate, StringComparison.Ordinal))
                .OrderBy(item => item.SlotNumber)
                .ToList();
            if (today.Count >= dailyCount) continue;

            var nextSlot = Enumerable.Range(1, dailyCount)
                .FirstOrDefault(slot => today.All(item => item.SlotNumber != slot));
            if (nextSlot <= 0) continue;

            var scheduled = GetScheduledLocalTime(localNow.Date, dailyCount, nextSlot, connectionId, groupId);
            if (localNow < scheduled) continue;

            var last = history.OrderByDescending(item => item.SentAt).FirstOrDefault();
            var minimumGap = TimeSpan.FromMinutes(Math.Max(90, 540 / dailyCount));
            if (last is not null && now - last.SentAt < minimumGap) continue;

            // Do not interrupt a live member conversation or stack this on top of a
            // recent bot message. The next worker pass may send later in the day.
            var activeCutoff = now.AddMinutes(-8);
            var botCutoff = now.AddMinutes(-45);
            if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                    item.ZaloConnectionId == connectionId && item.GroupId == groupId &&
                    !item.IsFromBot && item.SentAt >= activeCutoff,
                    cancellationToken))
                continue;
            if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                    item.ZaloConnectionId == connectionId && item.GroupId == groupId &&
                    item.IsFromBot && item.SentAt >= botCutoff,
                    cancellationToken))
                continue;

            var candidate = await BuildCandidateAsync(
                connectionId,
                groupId,
                localDate,
                nextSlot,
                history,
                cancellationToken);
            if (candidate is null) continue;

            var idempotencyKey = $"community-nudge:{connectionId}:{groupId}:{localDate}:{nextSlot}";
            try
            {
                var response = await bridge.SendGroupMessageAsync(
                    accountId,
                    groupId,
                    candidate.Text,
                    BuildMentions(candidate),
                    idempotencyKey: idempotencyKey);
                if (!response.Sent) continue;

                await store.RecordAsync(new ZaloCommunityNudgeHistoryData(
                    Guid.NewGuid().ToString("n"),
                    connectionId,
                    groupId,
                    localDate,
                    nextSlot,
                    candidate.Type,
                    candidate.SubjectName,
                    candidate.Text,
                    now,
                    NormalizeProviderMessageId(response.MessageId)),
                    cancellationToken);
                sent += 1;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not send community nudge Connection={ConnectionId} Group={GroupId} Slot={Slot}",
                    connectionId,
                    groupId,
                    nextSlot);
            }
        }

        return sent;
    }

    internal async Task<ZaloCommunityNudgeCandidate?> BuildCandidateAsync(
        string connectionId,
        string groupId,
        string localDate,
        int slotNumber,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<ZaloCommunityNudgeCandidate>
        {
            new(
                "team_preference_discovery",
                "🤝 Có thể bạn chưa biết: muốn được xếp chung team với ai thì cứ nói tự nhiên với tui, ví dụ “tối thứ 6 tui muốn chơi chung team với Minh”. Nếu còn thiếu buổi nào tui sẽ hỏi tiếp nha."),
            new(
                "share_slot_discovery",
                "💡 Có thể bạn chưa biết: nếu hai người muốn thay nhau dùng chung một suất thì cứ nói “tui muốn share slot với Minh”. Tui sẽ hỏi phần còn thiếu rồi đưa vào đúng flow share slot.")
        };

        candidates.AddRange(await BuildVoteActivityCandidatesAsync(
            connectionId,
            groupId,
            history,
            DateTimeOffset.UtcNow,
            cancellationToken));

        return SelectRotatedCandidate(
            candidates,
            history,
            connectionId,
            groupId,
            localDate,
            slotNumber);
    }

    internal async Task<IReadOnlyList<ZaloCommunityNudgeCandidate>> BuildVoteActivityCandidatesAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var pollRows = await db.ZaloPollSnapshots
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsAnalyticsEligible &&
                item.CreatedAtFromZalo != null)
            .Select(item => new
            {
                item.Id,
                CreatedAt = item.CreatedAtFromZalo!.Value
            })
            .ToListAsync(cancellationToken);

        // SQLite cannot reliably translate DateTimeOffset range comparisons. Apply the
        // rolling 30-day boundary in memory after the stable group predicate.
        var windowStart = now.AddDays(-ActivityWindowDays);
        var pollIds = pollRows
            .Where(item => item.CreatedAt >= windowStart && item.CreatedAt <= now)
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (pollIds.Count < MinimumPollsForActivityNudge) return [];

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsCurrentMember)
            .Select(item => new
            {
                item.ZaloUserId,
                item.DisplayName
            })
            .ToListAsync(cancellationToken);
        if (members.Count == 0) return [];

        var botUserId = CleanId(await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.Id == connectionId)
            .Select(item => item.AccountZaloId)
            .FirstOrDefaultAsync(cancellationToken));

        var votes = await db.ZaloPollVoteActivities
            .AsNoTracking()
            .Where(item =>
                pollIds.Contains(item.PollSnapshotId) &&
                item.IsCurrentlySelected)
            .Select(item => new
            {
                item.ZaloUserId,
                item.PollSnapshotId
            })
            .ToListAsync(cancellationToken);

        var voteCounts = votes
            .GroupBy(item => CleanId(item.ZaloUserId), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.PollSnapshotId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                StringComparer.Ordinal);

        var stats = members
            .Select(item =>
            {
                var userId = CleanId(item.ZaloUserId);
                var displayName = CleanDisplayName(item.DisplayName);
                return new ZaloCommunityVoteMember(
                    userId,
                    displayName,
                    voteCounts.GetValueOrDefault(userId));
            })
            .Where(item =>
                item.UserId.Length > 0 &&
                item.DisplayName.Length >= 2 &&
                !string.Equals(item.UserId, botUserId, StringComparison.Ordinal))
            .GroupBy(item => item.UserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (stats.Count == 0) return [];

        var localDate = now.ToOffset(VietnamOffset).ToString("yyyy-MM-dd");
        var recentSubjects = history
            .Where(item =>
                item.SubjectName is not null &&
                item.SentAt >= now.AddDays(-SubjectCooldownDays))
            .Select(item => NormalizeName(item.SubjectName!))
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<ZaloCommunityNudgeCandidate>();

        var top = stats
            .Where(item => item.VoteCount > 0)
            .OrderByDescending(item => item.VoteCount)
            .ThenBy(item => StableIndex($"{groupId}:{item.UserId}:{localDate}:top-vote", 10000))
            .Take(3)
            .ToList();
        if (top.Count >= 2)
        {
            var summary = string.Join(
                ", ",
                top.Select(item => $"{item.DisplayName} ({item.VoteCount}/{pollIds.Count} kèo)"));
            result.Add(new ZaloCommunityNudgeCandidate(
                "group_top_voters_30d",
                $"💡 Có thể bạn chưa biết: trong 30 ngày gần đây, những người vote tham gia kèo đều nhất là {summary} 🔥 Cảm ơn mấy ông giữ nhiệt cho group nha."));
        }

        var praiseMinimum = Math.Max(3, (int)Math.Ceiling(pollIds.Count * 0.60));
        var praise = stats
            .Where(item =>
                item.VoteCount >= praiseMinimum &&
                !recentSubjects.Contains(NormalizeName(item.DisplayName)))
            .OrderByDescending(item => item.VoteCount)
            .ThenBy(item => StableIndex($"{groupId}:{item.UserId}:{localDate}:praise", 10000))
            .FirstOrDefault();
        if (praise is not null)
        {
            result.Add(new ZaloCommunityNudgeCandidate(
                "member_vote_spotlight",
                $"🌟 @{praise.DisplayName} 30 ngày gần đây vote tham gia kèo đều ghê 😄 Ông đang thuộc nhóm giữ nhịp tốt nhất đó, cảm ơn vì kéo nhiệt cho group nha.",
                praise.DisplayName,
                praise.UserId));
        }

        var reengagementMaximum = Math.Max(1, (int)Math.Floor(pollIds.Count * 0.25));
        var reengage = stats
            .Where(item =>
                item.VoteCount <= reengagementMaximum &&
                !recentSubjects.Contains(NormalizeName(item.DisplayName)))
            .OrderBy(item => item.VoteCount)
            .ThenBy(item => StableIndex($"{groupId}:{item.UserId}:{localDate}:reengage", 10000))
            .FirstOrDefault();
        if (reengage is not null)
        {
            result.Add(new ZaloCommunityNudgeCandidate(
                "member_vote_reengagement",
                $"👋 @{reengage.DisplayName} dạo này ít thấy ông nhập hội cùng anh em 😄 Có kèo nào hợp lịch thì vào vote chơi nha, group luôn welcome ông quay lại.",
                reengage.DisplayName,
                reengage.UserId));
        }

        return result;
    }

    internal static ZaloCommunityNudgeCandidate? SelectRotatedCandidate(
        IReadOnlyList<ZaloCommunityNudgeCandidate> candidates,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        string connectionId,
        string groupId,
        string localDate,
        int slotNumber)
    {
        var unique = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.Text))
            .GroupBy(item => item.Type, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (unique.Count == 0) return null;

        var lastType = history
            .OrderByDescending(item => item.SentAt)
            .Select(item => item.NudgeType)
            .FirstOrDefault();

        // A type that was just sent is ineligible when any alternative exists.
        // Among the remaining types, prefer the least-used one so every eligible
        // content lane gets a turn before a lane accumulates another repeat.
        var withoutLast = unique
            .Where(item => !string.Equals(item.Type, lastType, StringComparison.Ordinal))
            .ToList();
        var pool = withoutLast.Count > 0 ? withoutLast : unique;

        var usage = pool.ToDictionary(
            item => item.Type,
            item => history.Count(row =>
                string.Equals(row.NudgeType, item.Type, StringComparison.Ordinal)),
            StringComparer.Ordinal);
        var minimumUsage = usage.Values.Min();
        var leastUsed = pool
            .Where(item => usage[item.Type] == minimumUsage)
            .OrderBy(item => StableIndex(
                $"{connectionId}:{groupId}:{localDate}:{slotNumber}:{item.Type}",
                10000))
            .ToList();

        return leastUsed[0];
    }

    internal static IReadOnlyList<BridgeOutgoingMention> BuildMentions(
        ZaloCommunityNudgeCandidate candidate)
    {
        var userId = CleanId(candidate.SubjectUserId);
        var displayName = CleanDisplayName(candidate.SubjectName);
        if (userId.Length == 0 || displayName.Length == 0) return [];

        var label = $"@{displayName}";
        var position = candidate.Text.IndexOf(label, StringComparison.Ordinal);
        return position < 0
            ? []
            : [new BridgeOutgoingMention(userId, position, label.Length)];
    }

    private static DateTimeOffset GetScheduledLocalTime(
        DateTime localDate,
        int dailyCount,
        int slotNumber,
        string connectionId,
        string groupId)
    {
        var startMinutes = 9 * 60 + 30;
        var endMinutes = 21 * 60;
        var span = endMinutes - startMinutes;
        var baseMinutes = startMinutes + (int)Math.Round(span * (slotNumber / (double)(dailyCount + 1)));
        var jitter = StableIndex($"{connectionId}:{groupId}:{localDate:yyyy-MM-dd}:{slotNumber}:jitter", 41) - 20;
        var minute = Math.Clamp(baseMinutes + jitter, startMinutes, endMinutes);
        return new DateTimeOffset(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            minute / 60,
            minute % 60,
            0,
            VietnamOffset);
    }

    private static int StableIndex(string value, int modulo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)Math.Max(1, modulo));
    }

    private static string CleanDisplayName(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('@');
        return text.Length <= 60 ? text : text[..60].TrimEnd();
    }

    private static string NormalizeName(string value) =>
        ZaloBotIntelligence.Normalize(CleanDisplayName(value));

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static string? NormalizeProviderMessageId(string? value)
    {
        var id = CleanId(value);
        return id.Length == 0 ? null : id;
    }
}
