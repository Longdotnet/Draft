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
    string? SubjectUserId = null,
    string? ContentKey = null);

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
///
/// Targeted activity nudges are preferred over generic feature tips whenever an eligible
/// member exists. Rotation is keyed by immutable Zalo UID through ZaloProactiveMessageStore,
/// so renames and same-display-name members do not break anti-repeat behavior.
/// </summary>
public sealed class ZaloCommunityNudgeService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ILogger<ZaloCommunityNudgeService> logger,
    IConfiguration? configuration = null)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan SendLeaseDuration = TimeSpan.FromMinutes(2);
    private const int ActivityWindowDays = 30;
    private const int LegacySubjectCooldownDays = 21;
    private const int MinimumPollsForActivityNudge = 4;
    private readonly ZaloCommunityNudgeStore store = new(db);
    private readonly ZaloProactiveMessageStore proactiveStore = new(db);

    private static readonly (string Key, string Text)[] SpotlightTemplates =
    [
        (
            "spotlight-1",
            "🌟 @{name} 30 ngày gần đây vote tham gia kèo đều ghê 😄 Ông đang thuộc nhóm giữ nhịp tốt nhất đó, cảm ơn vì kéo nhiệt cho group nha."),
        (
            "spotlight-2",
            "🔥 @{name} dạo này thấy kèo là có mặt đều nha 😄 Cảm ơn ông giữ lửa cho group, cứ phong độ này là đẹp."),
        (
            "spotlight-3",
            "🏐 Gọi tên @{name} cái coi 😄 30 ngày qua vote kèo rất đều, đúng kiểu mem giữ nhiệt. Cảm ơn ông nha.")
    ];

    private static readonly (string Key, string Text)[] ReengagementTemplates =
    [
        (
            "reengage-1",
            "👋 @{name} dạo này ít thấy ông nhập hội cùng anh em 😄 Có kèo nào hợp lịch thì vào vote chơi nha, group luôn welcome ông quay lại."),
        (
            "reengage-2",
            "😄 @{name} lâu rồi ít thấy xuất hiện trong mấy kèo gần đây nha. Rảnh buổi nào thì nhảy vào vote với anh em cho vui ông ơi."),
        (
            "reengage-3",
            "🏐 @{name} NPC réo nhẹ cái nè =)) dạo này ít thấy vào kèo. Có hôm nào khớp lịch thì quay lại quẩy với group nha.")
    ];

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        await store.EnsureAsync(cancellationToken);
        await proactiveStore.EnsureAsync(cancellationToken);

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
            var proactiveHistory = await proactiveStore.GetHistoryAsync(
                connectionId,
                groupId,
                500,
                cancellationToken);

            var latestProactive = proactiveHistory
                .OrderByDescending(item => item.SentAt)
                .FirstOrDefault();
            if (latestProactive is not null &&
                now - latestProactive.SentAt < TimeSpan.FromMinutes(GetGlobalCooldownMinutes()))
                continue;

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
            // recent bot message. The durable proactive guard above is the authoritative
            // cross-worker cooldown; these message checks are extra social comfort.
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

            var candidate = await BuildCandidateWithProactiveHistoryAsync(
                connectionId,
                groupId,
                localDate,
                nextSlot,
                history,
                proactiveHistory,
                cancellationToken);
            if (candidate is null) continue;

            var idempotencyKey = $"community-nudge:{connectionId}:{groupId}:{localDate}:{nextSlot}";
            if (!await proactiveStore.TryAcquireLeaseAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    now,
                    SendLeaseDuration,
                    cancellationToken))
                continue;

            var accepted = false;
            try
            {
                var response = await bridge.SendGroupMessageAsync(
                    accountId,
                    groupId,
                    candidate.Text,
                    BuildMentions(candidate),
                    idempotencyKey: idempotencyKey);
                if (!response.Sent)
                {
                    await proactiveStore.ReleaseLeaseAsync(
                        connectionId,
                        groupId,
                        idempotencyKey,
                        cancellationToken);
                    continue;
                }

                accepted = true;
                await RememberAcceptedCommunityAsync(
                    connectionId,
                    groupId,
                    localDate,
                    nextSlot,
                    candidate,
                    response.MessageId,
                    idempotencyKey,
                    now,
                    cancellationToken);
                sent += 1;
            }
            catch (Exception exception)
            {
                if (!accepted)
                    await TryReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);

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

    internal Task<ZaloCommunityNudgeCandidate?> BuildCandidateAsync(
        string connectionId,
        string groupId,
        string localDate,
        int slotNumber,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        CancellationToken cancellationToken = default) =>
        BuildCandidateWithProactiveHistoryAsync(
            connectionId,
            groupId,
            localDate,
            slotNumber,
            history,
            [],
            cancellationToken);

    internal async Task<ZaloCommunityNudgeCandidate?> BuildCandidateWithProactiveHistoryAsync(
        string connectionId,
        string groupId,
        string localDate,
        int slotNumber,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        CancellationToken cancellationToken = default)
    {
        var featureCandidates = new List<ZaloCommunityNudgeCandidate>
        {
            new(
                "team_preference_discovery",
                "🤝 Có thể bạn chưa biết: muốn được xếp chung team với ai thì cứ nói tự nhiên với tui, ví dụ “tối thứ 6 tui muốn chơi chung team với Minh”. Nếu còn thiếu buổi nào tui sẽ hỏi tiếp nha.",
                ContentKey: "feature:team-preference"),
            new(
                "share_slot_discovery",
                "💡 Có thể bạn chưa biết: nếu hai người muốn thay nhau dùng chung một suất thì cứ nói “tui muốn share slot với Minh”. Tui sẽ hỏi phần còn thiếu rồi đưa vào đúng flow share slot.",
                ContentKey: "feature:share-slot")
        };

        var activityCandidates = await BuildVoteActivityCandidatesWithProactiveHistoryAsync(
            connectionId,
            groupId,
            history,
            proactiveHistory,
            DateTimeOffset.UtcNow,
            cancellationToken);

        // If real activity data can ground a personal nudge, prefer people over generic
        // capability advertising. This keeps the group warm without turning "có thể bạn
        // chưa biết" into the next repetitive spam lane.
        var targeted = activityCandidates
            .Where(item => !string.IsNullOrWhiteSpace(item.SubjectUserId))
            .ToList();
        if (targeted.Count > 0)
        {
            return SelectRotatedCandidate(
                targeted,
                history,
                connectionId,
                groupId,
                localDate,
                slotNumber);
        }

        featureCandidates.AddRange(activityCandidates);
        return SelectRotatedCandidate(
            featureCandidates,
            history,
            connectionId,
            groupId,
            localDate,
            slotNumber);
    }

    internal Task<IReadOnlyList<ZaloCommunityNudgeCandidate>> BuildVoteActivityCandidatesAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildVoteActivityCandidatesWithProactiveHistoryAsync(
            connectionId,
            groupId,
            history,
            [],
            now,
            cancellationToken);

    internal async Task<IReadOnlyList<ZaloCommunityNudgeCandidate>> BuildVoteActivityCandidatesWithProactiveHistoryAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
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
        var hasDurableSubjectHistory = proactiveHistory.Any(item =>
            string.Equals(item.Lane, ZaloProactiveLane.Community, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.SubjectUserId));
        var legacyRecentNames = hasDurableSubjectHistory
            ? new HashSet<string>(StringComparer.Ordinal)
            : history
                .Where(item =>
                    item.SubjectName is not null &&
                    item.SentAt >= now.AddDays(-LegacySubjectCooldownDays))
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
                $"💡 Có thể bạn chưa biết: trong 30 ngày gần đây, những người vote tham gia kèo đều nhất là {summary} 🔥 Cảm ơn mấy ông giữ nhiệt cho group nha.",
                ContentKey: "activity:top-voters-30d"));
        }

        var praiseMinimum = Math.Max(3, (int)Math.Ceiling(pollIds.Count * 0.60));
        var praisePool = stats
            .Where(item =>
                item.VoteCount >= praiseMinimum &&
                !legacyRecentNames.Contains(NormalizeName(item.DisplayName)))
            .ToList();
        var praise = SelectRotatedMember(
            praisePool,
            "member_vote_spotlight",
            proactiveHistory,
            groupId,
            localDate,
            preferLowerVoteCount: false);
        if (praise is not null)
        {
            result.Add(BuildTargetedCandidate(
                "member_vote_spotlight",
                praise,
                SpotlightTemplates,
                proactiveHistory,
                groupId,
                localDate));
        }

        var reengagementMaximum = Math.Max(1, (int)Math.Floor(pollIds.Count * 0.25));
        var reengagePool = stats
            .Where(item =>
                item.VoteCount <= reengagementMaximum &&
                !legacyRecentNames.Contains(NormalizeName(item.DisplayName)))
            .ToList();
        var reengage = SelectRotatedMember(
            reengagePool,
            "member_vote_reengagement",
            proactiveHistory,
            groupId,
            localDate,
            preferLowerVoteCount: true);
        if (reengage is not null)
        {
            result.Add(BuildTargetedCandidate(
                "member_vote_reengagement",
                reengage,
                ReengagementTemplates,
                proactiveHistory,
                groupId,
                localDate));
        }

        return result;
    }

    internal static ZaloCommunityVoteMember? SelectRotatedMember(
        IReadOnlyList<ZaloCommunityVoteMember> members,
        string nudgeType,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        string groupId,
        string localDate,
        bool preferLowerVoteCount)
    {
        if (members.Count == 0) return null;

        var laneHistory = proactiveHistory
            .Where(item =>
                string.Equals(item.Lane, ZaloProactiveLane.Community, StringComparison.Ordinal) &&
                string.Equals(item.Kind, nudgeType, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.SubjectUserId))
            .ToList();
        var lastSubjectUserId = laneHistory
            .OrderByDescending(item => item.SentAt)
            .Select(item => CleanId(item.SubjectUserId))
            .FirstOrDefault();

        var ranked = members
            .Select(member =>
            {
                var memberHistory = laneHistory
                    .Where(item => string.Equals(
                        CleanId(item.SubjectUserId),
                        member.UserId,
                        StringComparison.Ordinal))
                    .ToList();
                return new
                {
                    Member = member,
                    Usage = memberHistory.Count,
                    LastSentAt = memberHistory.Count == 0
                        ? DateTimeOffset.MinValue
                        : memberHistory.Max(item => item.SentAt)
                };
            })
            .ToList();

        // Never immediately call out the same UID when another eligible person exists.
        var withoutLast = ranked
            .Where(item => !string.Equals(
                item.Member.UserId,
                lastSubjectUserId,
                StringComparison.Ordinal))
            .ToList();
        var pool = withoutLast.Count > 0 ? withoutLast : ranked;
        var minimumUsage = pool.Min(item => item.Usage);

        var leastUsed = pool
            .Where(item => item.Usage == minimumUsage)
            .OrderBy(item => item.LastSentAt)
            .ThenBy(item => preferLowerVoteCount ? item.Member.VoteCount : -item.Member.VoteCount)
            .ThenBy(item => StableIndex(
                $"{groupId}:{localDate}:{nudgeType}:{item.Member.UserId}",
                10000))
            .ToList();

        return leastUsed[0].Member;
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

    private static ZaloCommunityNudgeCandidate BuildTargetedCandidate(
        string nudgeType,
        ZaloCommunityVoteMember member,
        IReadOnlyList<(string Key, string Text)> templates,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        string groupId,
        string localDate)
    {
        var template = SelectTemplate(
            nudgeType,
            templates,
            proactiveHistory,
            groupId,
            localDate,
            member.UserId);
        var text = template.Text.Replace("{name}", member.DisplayName, StringComparison.Ordinal);
        return new ZaloCommunityNudgeCandidate(
            nudgeType,
            text,
            member.DisplayName,
            member.UserId,
            $"community:{nudgeType}:{template.Key}");
    }

    private static (string Key, string Text) SelectTemplate(
        string nudgeType,
        IReadOnlyList<(string Key, string Text)> templates,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        string groupId,
        string localDate,
        string subjectUserId)
    {
        var ranked = templates
            .Select(template =>
            {
                var contentKey = $"community:{nudgeType}:{template.Key}";
                var matching = proactiveHistory
                    .Where(item =>
                        string.Equals(item.Lane, ZaloProactiveLane.Community, StringComparison.Ordinal) &&
                        string.Equals(item.Kind, nudgeType, StringComparison.Ordinal) &&
                        string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal))
                    .ToList();
                return new
                {
                    Template = template,
                    Usage = matching.Count,
                    LastSentAt = matching.Count == 0
                        ? DateTimeOffset.MinValue
                        : matching.Max(item => item.SentAt)
                };
            })
            .OrderBy(item => item.Usage)
            .ThenBy(item => item.LastSentAt)
            .ThenBy(item => StableIndex(
                $"{groupId}:{localDate}:{nudgeType}:{subjectUserId}:{item.Template.Key}",
                10000))
            .ToList();

        return ranked[0].Template;
    }

    private async Task RememberAcceptedCommunityAsync(
        string connectionId,
        string groupId,
        string localDate,
        int slotNumber,
        ZaloCommunityNudgeCandidate candidate,
        string? providerMessageId,
        string idempotencyKey,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await proactiveStore.CommitCooldownAsync(
                connectionId,
                groupId,
                idempotencyKey,
                sentAt.AddMinutes(GetGlobalCooldownMinutes()),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Community proactive cooldown persistence failed after accepted send Group={GroupId} Slot={Slot}",
                groupId,
                slotNumber);
        }

        try
        {
            await store.RecordAsync(new ZaloCommunityNudgeHistoryData(
                Guid.NewGuid().ToString("n"),
                connectionId,
                groupId,
                localDate,
                slotNumber,
                candidate.Type,
                candidate.SubjectName,
                candidate.Text,
                sentAt,
                NormalizeProviderMessageId(providerMessageId)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Community slot history persistence failed after accepted send Group={GroupId} Slot={Slot}",
                groupId,
                slotNumber);
        }

        try
        {
            await proactiveStore.RecordAsync(
                new ZaloProactiveMessageHistoryData(
                    Guid.NewGuid().ToString("n"),
                    connectionId,
                    groupId,
                    localDate,
                    ZaloProactiveLane.Community,
                    candidate.Type,
                    candidate.ContentKey ?? $"community:{candidate.Type}",
                    candidate.SubjectUserId,
                    candidate.SubjectName,
                    candidate.Text,
                    sentAt,
                    NormalizeProviderMessageId(providerMessageId),
                    idempotencyKey),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Community proactive history persistence failed after accepted send Group={GroupId} Slot={Slot}",
                groupId,
                slotNumber);
        }
    }

    private async Task TryReleaseLeaseAsync(
        string connectionId,
        string groupId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not release community proactive lease Group={GroupId} Key={Key}",
                groupId,
                idempotencyKey);
        }
    }

    private int GetGlobalCooldownMinutes() =>
        Math.Clamp(
            configuration is null
                ? 60
                : configuration.GetValue("ZaloBot:Ambient:Presence:MinBotIntervalMinutes", 60),
            15,
            720);

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
