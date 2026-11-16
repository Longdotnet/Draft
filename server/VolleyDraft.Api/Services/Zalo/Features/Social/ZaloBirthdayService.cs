using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBirthdayService(
    VolleyDraftDbContext db,
    ZaloIntegrationService zaloIntegration,
    ZaloBridgeClient bridge,
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
            var memberById = members
                .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var birthdays = profiles
                .Select(profile =>
                {
                    if (!ZaloMemberIntelligenceBotService.TryParseBirthday(profile, out var birthday))
                        return null;
                    var userId = NormalizeId(profile.ZaloUserId);
                    if (!memberById.TryGetValue(userId, out var member))
                        return null;
                    var occurrence = ZaloMemberIntelligenceBotService.GetNextBirthdayOccurrence(today, birthday);
                    return new BirthdayProfile(member, profile, birthday, occurrence, occurrence.DayNumber - today.DayNumber);
                })
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.DaysUntil)
                .ThenBy(item => item.Member.DisplayName)
                .ToList();

            foreach (var birthday in birthdays.Where(item => item.DaysUntil == 0))
            {
                var plan = await BuildPlanAsync(target, birthday.Member, birthday.Profile, localNow, cancellationToken);
                await trendMusic.TryScheduleBirthdayAsync(target, today, plan, cancellationToken);
            }

            await TrySendDigestAsync(target, today, localNow, birthdays, cancellationToken);
        }
    }

    private async Task TrySendDigestAsync(
        ZaloProactiveTarget target,
        DateOnly today,
        DateTimeOffset localNow,
        IReadOnlyList<BirthdayProfile> birthdays,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("ZaloBot:Ambient:Birthday:DigestEnabled", true))
            return;

        var digestHour = Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Birthday:DigestHour", 8), 0, 23);
        if (localNow.Hour < digestHour) return;

        var upcomingDays = Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Birthday:UpcomingDays", 7), 1, 14);
        var soonDays = Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Birthday:SoonDays", 30), upcomingDays + 1, 60);
        var todayBirthdays = birthdays.Where(item => item.DaysUntil == 0).ToArray();
        var upcoming = birthdays.Where(item => item.DaysUntil >= 1 && item.DaysUntil <= upcomingDays).ToArray();
        var soon = birthdays.Where(item => item.DaysUntil > upcomingDays && item.DaysUntil <= soonDays).ToArray();
        var hasTomorrow = upcoming.Any(item => item.DaysUntil == 1);
        var weeklyDigestDay = localNow.DayOfWeek == DayOfWeek.Monday;
        if (todayBirthdays.Length == 0 && !hasTomorrow && !weeklyDigestDay)
            return;
        if (todayBirthdays.Length == 0 && upcoming.Length == 0 && soon.Length == 0)
            return;

        var message = BuildDigestText(todayBirthdays, upcoming, soon, upcomingDays, soonDays);
        var localDate = $"{today:yyyy-MM-dd}";
        var idempotencyKey = $"birthday-digest:{target.ConnectionId}:{target.GroupId}:{today:yyyyMMdd}";
        var proactiveStore = new ZaloProactiveMessageStore(db);
        var history = await proactiveStore.GetHistoryAsync(target.ConnectionId, target.GroupId, 120, cancellationToken);
        if (history.Any(item => string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)))
            return;

        var now = DateTimeOffset.UtcNow;
        if (!await proactiveStore.TryAcquireLeaseAsync(
                target.ConnectionId,
                target.GroupId,
                idempotencyKey,
                now,
                TimeSpan.FromMinutes(2),
                cancellationToken))
            return;

        var accepted = false;
        try
        {
            var response = await bridge.SendGroupMessageAsync(
                target.AccountId,
                target.GroupId,
                message,
                [],
                idempotencyKey: idempotencyKey);
            if (!response.Sent)
            {
                await proactiveStore.ReleaseLeaseAsync(target.ConnectionId, target.GroupId, idempotencyKey, cancellationToken);
                return;
            }

            accepted = true;
            await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                Guid.NewGuid().ToString("N"),
                target.ConnectionId,
                target.GroupId,
                localDate,
                ZaloProactiveLane.Birthday,
                "BirthdayDigest",
                $"birthday:{localDate}",
                null,
                null,
                message,
                now,
                response.MessageId,
                idempotencyKey), cancellationToken);
            await proactiveStore.CommitCooldownAsync(
                target.ConnectionId,
                target.GroupId,
                idempotencyKey,
                now.AddMinutes(2),
                cancellationToken);
            logger.LogInformation(
                "Birthday digest sent Group={GroupId} Today={TodayCount} Upcoming={UpcomingCount} Soon={SoonCount}",
                target.GroupId,
                todayBirthdays.Length,
                upcoming.Length,
                soon.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!accepted)
            {
                try
                {
                    await proactiveStore.ReleaseLeaseAsync(target.ConnectionId, target.GroupId, idempotencyKey, cancellationToken);
                }
                catch
                {
                    // Best-effort release; the short lease expires on its own.
                }
            }
            logger.LogWarning(exception, "Birthday digest send failed Group={GroupId}", target.GroupId);
        }
    }

    internal static string BuildDigestText(
        IReadOnlyList<BirthdayProfile> today,
        IReadOnlyList<BirthdayProfile> upcoming,
        IReadOnlyList<BirthdayProfile> soon,
        int upcomingDays,
        int soonDays)
    {
        var sections = new List<string> { "🎂 Lịch sinh nhật trong nhóm" };
        if (today.Count > 0)
        {
            sections.Add("🎉 Hôm nay\n" + string.Join('\n', today.Select(item => $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM}")));
        }
        if (upcoming.Count > 0)
        {
            sections.Add($"📅 {upcomingDays} ngày tới\n" + string.Join('\n', upcoming.Select(item =>
                $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM} (còn {item.DaysUntil} ngày)")));
        }
        if (soon.Count > 0)
        {
            sections.Add($"🔜 Sắp tới ({soonDays} ngày)\n" + string.Join('\n', soon.Select(item =>
                $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM}")));
        }
        sections.Add("Bott sẽ tự chúc đúng ngày, không cần nhắc.");
        return string.Join("\n\n", sections);
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

        var activityStart = localNow.AddDays(-365).ToUniversalTime();
        var activityEnd = localNow.AddDays(1).ToUniversalTime();
        var memberActivity = await activity.GetMemberActivityAsync(
            target.ConnectionId,
            target.GroupId,
            userId,
            new ZaloActivityPeriod(activityStart, activityEnd, "12 tháng gần nhất"),
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
            activityStart,
            cancellationToken);
        var interactionPeerCount = await CountInteractionPeersAsync(
            target,
            userId,
            activityStart,
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
        var songArtist = configuration["ZaloBot:Ambient:Birthday:SongArtist"]?.Trim();
        var songTrackId = configuration["ZaloBot:Ambient:Birthday:SongTrackId"]?.Trim();
        return new ZaloBirthdayVideoPlan(
            userId,
            displayName,
            greeting,
            NormalizeArtworkUrl(profile.AvatarUrl ?? member.AvatarUrl),
            string.IsNullOrWhiteSpace(songQuery) ? null : songQuery,
            string.IsNullOrWhiteSpace(songArtist) ? null : songArtist,
            string.IsNullOrWhiteSpace(songTrackId) ? null : songTrackId);
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

    internal static string? NormalizeArtworkUrl(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{normalized}"
            : normalized;
    }

    internal sealed record BirthdayCurrentMember(
        string ZaloUserId,
        string DisplayName,
        string? AvatarUrl,
        DateTimeOffset FirstSeenAt);

    internal sealed record BirthdayProfile(
        BirthdayCurrentMember Member,
        BridgeUserProfile Profile,
        DateOnly Birthday,
        DateOnly Occurrence,
        int DaysUntil);
}
