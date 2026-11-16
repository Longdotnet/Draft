using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloBirthdayService(
    VolleyDraftDbContext db,
    ZaloIntegrationService zaloIntegration,
    ZaloBridgeClient bridge,
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
                var plan = BuildPlan(birthday.Member, birthday.Profile);
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
        var sections = new List<string>();
        if (today.Count > 0)
        {
            var wish = today.Count == 1
                ? $"Chúc {today[0].Member.DisplayName} có một ngày sinh nhật thật vui nha ❤️"
                : "Chúc mọi người có một ngày sinh nhật thật vui nha ❤️";
            sections.Add("🎂 Hôm nay có sinh nhật nè 🎉\n" + string.Join('\n', today.Select(item =>
                $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM}")) + $"\n{wish}");
        }
        if (upcoming.Count > 0)
        {
            sections.Add($"📅 Trong {upcomingDays} ngày tới\n" + string.Join('\n', upcoming.Select(item =>
                $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM} (còn {item.DaysUntil} ngày)")));
        }
        if (soon.Count > 0)
        {
            sections.Add("🔜 Sắp tới\n" + string.Join('\n', soon.Select(item =>
                $"• {item.Member.DisplayName} — {item.Occurrence:dd/MM}")));
        }
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

    private ZaloBirthdayVideoPlan BuildPlan(
        BirthdayCurrentMember member,
        BridgeUserProfile profile)
    {
        var userId = NormalizeId(member.ZaloUserId);
        var displayName = string.IsNullOrWhiteSpace(member.DisplayName) ? profile.DisplayName : member.DisplayName.Trim();
        var greeting = BuildFriendlyGreeting(displayName, userId);
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

    internal static string BuildFriendlyGreeting(string displayName, string userId)
    {
        var endings = new[]
        {
            $"Chúc mừng sinh nhật {displayName} nha ❤️ Tuổi mới chúc nhiều sức khỏe, vui vẻ, gặp nhiều may mắn. Hôm nay cứ vui hết mình nha!",
            $"Sinh nhật vui vẻ nha {displayName} 🎉 Thêm tuổi mới rồi, mong mọi chuyện nhẹ nhàng, thuận lợi, ngày nào cũng có tiếng cười. Có dịp lại quẩy cùng anh em nha ❤️",
            $"Happy birthday {displayName} nha 🥳 Chúc tuổi mới khỏe mạnh, vui vẻ, làm gì cũng suôn sẻ và gặp toàn người thương mình. Có một ngày thật vui nha!"
        };
        var ending = endings[userId.Aggregate(0, (sum, character) => sum + character) % endings.Length];
        return ending;
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
