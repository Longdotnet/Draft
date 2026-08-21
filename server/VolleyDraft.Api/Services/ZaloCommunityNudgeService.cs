using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloCommunityNudgeCandidate(
    string Type,
    string Text,
    string? SubjectName = null);

/// <summary>
/// Proactive, group-scoped discovery/positive-community messages. The engine never
/// mutates roster/team state: share-slot remains owned by the existing deterministic
/// bot flow. Member spotlights are derived only from observed SessionPlayer presence.
/// </summary>
public sealed class ZaloCommunityNudgeService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ILogger<ZaloCommunityNudgeService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
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
            .Include(item => item.ZaloConnection)
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
            var history = await store.GetHistoryAsync(connectionId, groupId, 80, cancellationToken);
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
                    [],
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
        var rotation = StableIndex($"{connectionId}:{groupId}:{localDate}:{slotNumber}", 4);
        if (rotation >= 2)
        {
            var spotlight = await BuildMemberSpotlightAsync(connectionId, groupId, history, cancellationToken);
            if (spotlight is not null) return spotlight;
        }

        return rotation % 2 == 0
            ? new ZaloCommunityNudgeCandidate(
                "share_slot_discovery",
                "💡 Có thể bạn chưa biết: muốn được xếp chung team với ai thì không cần nhắn riêng quản lý nha. Cứ nói với tui kiểu: ‘tui muốn share slot với Minh’. Nếu chưa rõ trận nào tui sẽ hỏi tiếp rồi hướng dẫn bạn làm tới nơi.")
            : new ZaloCommunityNudgeCandidate(
                "play_together_discovery",
                "🤝 Muốn chơi chung với ai cứ nói tự nhiên với tui nha. Ví dụ: ‘tối thứ 6 tui muốn chơi chung với Nam’ hoặc ‘tui muốn share slot với Nam’. Tui sẽ hỏi thêm phần còn thiếu rồi đưa vào đúng flow share slot.");
    }

    private async Task<ZaloCommunityNudgeCandidate?> BuildMemberSpotlightAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloCommunityNudgeHistoryData> history,
        CancellationToken cancellationToken)
    {
        var rows = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.Players)
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.ZaloGroupId == groupId &&
                           item.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var sessions = rows
            .OrderByDescending(item => item.StartTime ?? item.CreatedAt)
            .Take(12)
            .ToList();
        if (sessions.Count < 4) return null;

        var recentSubjects = history
            .Where(item => item.SubjectName is not null && item.SentAt >= DateTimeOffset.UtcNow.AddDays(-21))
            .Select(item => NormalizeName(item.SubjectName!))
            .ToHashSet(StringComparer.Ordinal);
        var appearances = new Dictionary<string, (string DisplayName, List<int> SessionIndexes)>(StringComparer.Ordinal);
        for (var index = 0; index < sessions.Count; index += 1)
        {
            foreach (var player in sessions[index].Players.Where(player => player.IsPresent))
            {
                var display = CleanDisplayName(player.DisplayName);
                var key = NormalizeName(display);
                if (key.Length < 2) continue;
                if (!appearances.TryGetValue(key, out var entry))
                    entry = (display, []);
                entry.SessionIndexes.Add(index);
                appearances[key] = entry;
            }
        }

        var eligible = appearances
            .Where(item => item.Value.SessionIndexes.Count >= 3 && !recentSubjects.Contains(item.Key))
            .OrderBy(item => StableIndex($"{groupId}:{item.Key}:{DateTimeOffset.UtcNow:yyyy-MM-dd}", 10000))
            .ToList();
        foreach (var item in eligible)
        {
            var name = item.Value.DisplayName;
            var indexes = item.Value.SessionIndexes;
            var recentFour = indexes.Count(index => index < 4);
            var previousFour = indexes.Count(index => index is >= 4 and < 8);

            if (indexes.Contains(0) && indexes.Skip(1).Any(index => index >= 4))
            {
                return new ZaloCommunityNudgeCandidate(
                    "member_returning",
                    $"✨ Dạo này lại thấy {name} xuất hiện trên sân rồi nha. Có thêm người quay lại nhập hội là group vui hơn hẳn 😄",
                    name);
            }

            if (recentFour >= 2 && recentFour > previousFour)
            {
                return new ZaloCommunityNudgeCandidate(
                    "member_more_active",
                    $"📈 Dạo này {name} lên sân đều hơn trước nha 😄 Giữ nhịp này là đẹp, group có thêm người tham gia đều lúc nào cũng dễ gom kèo hơn.",
                    name);
            }

            if (recentFour >= 2)
            {
                return new ZaloCommunityNudgeCandidate(
                    "member_regular",
                    $"🌟 Gần đây {name} tham gia khá đều nha. Có mặt ổn định vậy là góp thêm nhịp vui cho group rồi 😄",
                    name);
            }

            if (!indexes.Any(index => index < 3) && indexes.Any(index => index >= 3))
            {
                return new ZaloCommunityNudgeCandidate(
                    "member_invite_back",
                    $"👋 Lâu rồi chưa thấy {name} lên sân á 😄 Hôm nào rảnh quay lại quẩy với anh em nha, group vẫn luôn welcome.",
                    name);
            }
        }

        return null;
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
