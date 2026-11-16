using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBirthdayService(
    VolleyDraftDbContext db,
    ZaloIntegrationService zaloIntegration,
    ZaloMemberActivityService activity,
    ZaloMembershipHistoryService membershipHistory,
    ZaloTrendMusicService trendMusic,
    IMemoryCache memoryCache,
    IConfiguration configuration,
    ILogger<ZaloBirthdayService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:Ambient:Birthday:Enabled", true) ||
            !configuration.GetValue("ZaloBot:Ambient:Birthday:SendEnabled", true))
            return;

        var localNow = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
        var startHour = Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Birthday:StartHour", 8), 0, 23);
        var endHour = Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Birthday:EndHour", 10), 1, 24);
        if (localNow.Hour < startHour || localNow.Hour >= endHour) return;

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var targets = await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken);
        foreach (var target in targets.Take(100))
        {
            var members = await db.ZaloGroupMembers
                .AsNoTracking()
                .Where(item =>
                    item.ZaloConnectionId == target.ConnectionId &&
                    item.GroupId == target.GroupId &&
                    item.IsCurrentMember)
                .Select(item => new BirthdayCurrentMember(
                    item.ZaloUserId,
                    item.DisplayName,
                    item.AvatarUrl,
                    item.FirstSeenAt))
                .ToListAsync(cancellationToken);
            if (members.Count == 0) continue;

            var profiles = await GetProfilesAsync(target, members, today, cancellationToken);
            foreach (var profile in profiles)
            {
                if (!ZaloMemberIntelligenceBotService.TryParseBirthday(profile, out var birthday) ||
                    birthday.Month != today.Month || birthday.Day != today.Day)
                    continue;

                var userId = NormalizeId(profile.ZaloUserId);
                var member = members.FirstOrDefault(item => NormalizeId(item.ZaloUserId) == userId);
                if (member is null) continue;
                var plan = await BuildPlanAsync(target, member, profile, localNow, cancellationToken);
                await trendMusic.TryScheduleBirthdayAsync(target, today, plan, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<BridgeUserProfile>> GetProfilesAsync(
        ZaloProactiveTarget target,
        IReadOnlyList<BirthdayCurrentMember> members,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var key = $"zalo-birthday-profiles:{target.ConnectionId}:{target.GroupId}:{today:yyyyMMdd}";
        if (memoryCache.TryGetValue<IReadOnlyList<BridgeUserProfile>>(key, out var cached) && cached is not null)
            return cached;

        try
        {
            var profiles = await zaloIntegration.GetUserProfilesAsync(
                target.ConnectionId,
                members.Select(item => item.ZaloUserId).ToArray(),
                cancellationToken);
            if (profiles.Count > 0)
            {
                var expectedProfiles = members.Select(item => NormalizeId(item.ZaloUserId)).Distinct(StringComparer.Ordinal).Count();
                var cacheDuration = profiles.Count >= expectedProfiles ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15);
                memoryCache.Set(key, profiles, cacheDuration);
            }
            return profiles;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Birthday profile refresh failed Group={GroupId}; will retry on a later cycle",
                target.GroupId);
            return [];
        }
    }

    private async Task<ZaloBirthdayVideoPlan> BuildPlanAsync(
        ZaloProactiveTarget target,
        BirthdayCurrentMember member,
        BridgeUserProfile profile,
        DateTimeOffset localNow,
        CancellationToken cancellationToken)
    {
        var userId = NormalizeId(member.ZaloUserId);
        var displayName = string.IsNullOrWhiteSpace(member.DisplayName) ? profile.DisplayName : member.DisplayName.Trim();
        var join = await membershipHistory.GetJoinDateAsync(
            target.ConnectionId,
            target.GroupId,
            userId,
            cancellationToken);

        var activityStart = localNow.AddDays(-365);
        var memberActivity = await activity.GetMemberActivityAsync(
            target.ConnectionId,
            target.GroupId,
            userId,
            new ZaloActivityPeriod(activityStart, localNow.AddDays(1), "12 tháng gần nhất"),
            cancellationToken);

        var sessionRows = await db.SessionPlayers
            .AsNoTracking()
            .Where(item =>
                item.IsPresent &&
                item.PlayerProfile != null &&
                item.PlayerProfile.ZaloUserId == userId &&
                item.Session.ZaloConnectionId == target.ConnectionId &&
                item.Session.ZaloGroupId == target.GroupId)
            .Select(item => new { item.SessionId, item.Session.StartTime })
            .ToListAsync(cancellationToken);
        var rosterCount = sessionRows.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count();
        var memory = await FindBirthdayMemoryAsync(
            target.ConnectionId,
            target.GroupId,
            userId,
            localNow.AddDays(-365),
            cancellationToken);
        var interactionPeerCount = await CountInteractionPeersAsync(
            target,
            userId,
            localNow.AddDays(-365),
            cancellationToken);

        var facts = new List<string>();
        if (join?.HasVerifiedEvidence == true && join.JoinedAt is { } joinedAt)
            facts.Add($"{DescribeDuration(joinedAt, localNow)} cùng nhóm");
        else if (member.FirstSeenAt != default)
            facts.Add($"Bott ghi nhận từ {member.FirstSeenAt.ToOffset(VietnamOffset):dd/MM/yyyy}");
        if (rosterCount > 0)
            facts.Add($"có tên roster {rosterCount} buổi");
        if (!string.IsNullOrWhiteSpace(memory))
            facts.Add(memory);
        if (interactionPeerCount > 0)
            facts.Add($"tương tác qua reply với {interactionPeerCount} thành viên");
        if (memberActivity is { MessageCount: > 0 })
            facts.Add($"{memberActivity.MessageCount} tin nhắn/12 tháng");
        if (memberActivity is { VotedPollCount: > 0 })
            facts.Add($"{memberActivity.VotedPollCount} poll");

        var detail = facts.Count == 0
            ? "Cảm ơn bạn đã là một phần của nhóm."
            : $"Bott nhớ: {string.Join(" • ", facts.Take(3))}.";
        var endings = new[]
        {
            "Tuổi mới vui khỏe và còn quẩy nhiều kèo cùng anh em nha!",
            "Chúc tuổi mới nhiều năng lượng, nhiều niềm vui và tiếp tục cháy sân cùng ZYRO nha!",
            "Mong tuổi mới đầy pin, toàn kèo vui và có thêm thật nhiều kỷ niệm với nhóm nha!"
        };
        var ending = endings[userId.Aggregate(0, (sum, character) => sum + character) % endings.Length];
        var greeting = $"Chúc mừng sinh nhật {displayName}! {detail} {ending}";
        var songQuery = configuration["ZaloBot:Ambient:Birthday:SongQuery"]?.Trim();
        return new ZaloBirthdayVideoPlan(
            userId,
            displayName,
            greeting,
            profile.AvatarUrl ?? member.AvatarUrl,
            string.IsNullOrWhiteSpace(songQuery) ? null : songQuery);
    }

    private async Task<string?> FindBirthdayMemoryAsync(
        string connectionId,
        string groupId,
        string userId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var messages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderId == userId &&
                !item.IsFromBot &&
                item.SentAt >= since &&
                item.Content != null)
            .OrderByDescending(item => item.SentAt)
            .Select(item => item.Content)
            .Take(80)
            .ToListAsync(cancellationToken);
        return TrySelectBirthdayMemory(messages, out var memory) ? memory : null;
    }

    internal static bool TrySelectBirthdayMemory(IEnumerable<string?> messages, out string memory)
    {
        memory = string.Empty;
        foreach (var raw in messages)
        {
            var text = string.Join(' ', (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length < 8 || text.StartsWith("@bott", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("www.", StringComparison.OrdinalIgnoreCase))
                continue;

            var normalized = ZaloBotIntelligence.Normalize(text);
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (tokens.Contains("vote"))
                memory = "hay bàn chuyện vote cùng nhóm";
            else if (tokens.Contains("keo") || tokens.Contains("san"))
                memory = "hay nhắc chuyện kèo/sân trong group";
            else if (tokens.Contains("team") || tokens.Contains("tran") || tokens.Contains("set"))
                memory = "hay bàn chuyện team/trận";
            else if (tokens.Contains("bong") || tokens.Contains("choi") || tokens.Contains("danh"))
                memory = "hay nói chuyện bóng chuyền";
            else
                continue;
            return true;
        }
        return false;
    }

    private async Task<int> CountInteractionPeersAsync(
        ZaloProactiveTarget target,
        string userId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var graph = new ZaloMessageGraphStore(db);
        var allRelations = await graph.LoadReplyRelationsAsync(
            target.ConnectionId,
            target.GroupId,
            cancellationToken);
        var peers = allRelations.Values
            .Where(relation => relation.CreatedAt >= since)
            .Where(relation => NormalizeId(relation.QuotedSenderId) == userId)
            .Select(relation => relation.FromMessageId)
            .ToHashSet(StringComparer.Ordinal);

        var inboundSenders = peers.Count == 0
            ? new List<string>()
            : await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(message =>
                    message.ZaloConnectionId == target.ConnectionId &&
                    message.GroupId == target.GroupId &&
                    peers.Contains(message.MessageId))
                .Select(message => message.SenderId)
                .ToListAsync(cancellationToken);
        var outboundPeers = (await graph.LoadReplyRelationsForSenderAsync(
                target.ConnectionId,
                target.GroupId,
                userId,
                cancellationToken))
            .Values
            .Where(relation => relation.CreatedAt >= since)
            .Select(relation => relation.QuotedSenderId);

        return inboundSenders
            .Concat(outboundPeers)
            .Select(NormalizeId)
            .Where(id =>
                id.Length > 0 &&
                id != userId &&
                id != NormalizeId(target.AccountId))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static string DescribeDuration(DateTimeOffset start, DateTimeOffset end)
    {
        var startDate = start.ToOffset(VietnamOffset).Date;
        var endDate = end.ToOffset(VietnamOffset).Date;
        var months = Math.Max(0, (endDate.Year - startDate.Year) * 12 + endDate.Month - startDate.Month);
        if (endDate.Day < startDate.Day && months > 0) months--;
        if (months >= 12)
        {
            var years = months / 12;
            var remaining = months % 12;
            return remaining == 0 ? $"{years} năm" : $"{years} năm {remaining} tháng";
        }
        if (months > 0) return $"{months} tháng";
        var days = Math.Max(1, (endDate - startDate).Days);
        return $"{days} ngày";
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private sealed record BirthdayCurrentMember(
        string ZaloUserId,
        string DisplayName,
        string? AvatarUrl,
        DateTimeOffset FirstSeenAt);
}
