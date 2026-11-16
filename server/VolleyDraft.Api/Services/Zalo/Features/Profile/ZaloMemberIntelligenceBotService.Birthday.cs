using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloMemberIntelligenceBotService
{
    private static readonly TimeSpan BirthdayVietnamOffset = TimeSpan.FromHours(7);

    public async Task<ZaloMemberBotAnswer?> TryHandleBirthdayQueryAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken)
    {
        var hasWindow = TryParseBirthdayWindow(question, out var windowDays);
        var month = 0;
        var hasMonth = !hasWindow && TryParseBirthdayMonth(question, out month);
        if (!hasWindow && !hasMonth) return null;
        var denial = await GetOperatorDenialAsync(
            connectionId,
            groupId,
            incoming.SenderId,
            ZaloBotIntent.GeneralChat,
            cancellationToken);
        if (denial is not null)
        {
            return new ZaloMemberBotAnswer(
                "Danh sách sinh nhật của cả nhóm chỉ dành cho trưởng nhóm, phó nhóm và bot operator.",
                ZaloBotIntent.GeneralChat);
        }

        var localToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(BirthdayVietnamOffset).DateTime);
        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsCurrentMember)
            .Select(item => new { item.ZaloUserId, item.DisplayName })
            .ToListAsync(cancellationToken);
        if (members.Count == 0)
            return new ZaloMemberBotAnswer("Chưa có danh sách thành viên hiện tại để kiểm tra sinh nhật.", ZaloBotIntent.GeneralChat);

        IReadOnlyList<BridgeUserProfile> profiles;
        try
        {
            profiles = await zaloIntegration.GetUserProfilesAsync(
                connectionId,
                members.Select(item => item.ZaloUserId).ToArray(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Birthday profile lookup failed Group={GroupId} WindowDays={WindowDays} Month={Month}",
                groupId,
                hasWindow ? windowDays : null,
                hasMonth ? month : null);
            return new ZaloMemberBotAnswer(
                "Zalo đang giới hạn việc đọc hồ sơ thành viên nên tui chưa kiểm tra sinh nhật được. Thử lại sau nha.",
                ZaloBotIntent.GeneralChat);
        }

        var nameById = members.ToDictionary(item => NormalizeId(item.ZaloUserId), item => item.DisplayName, StringComparer.Ordinal);
        var birthdays = profiles
            .Select(profile => TryParseBirthday(profile, out var birthday)
                ? BuildBirthdayMember(profile, birthday, localToday, nameById)
                : null)
            .Where(item => item is not null &&
                           (hasWindow
                               ? item.DaysUntil >= 0 && item.DaysUntil <= windowDays
                               : item.Birthday.Month == month))
            .Select(item => item!)
            .OrderBy(item => hasWindow ? item.DaysUntil : item.Birthday.Day)
            .ThenBy(item => item.DisplayName)
            .ToList();

        if (birthdays.Count == 0)
        {
            var emptyText = hasWindow
                ? windowDays switch
                {
                    0 => "Hôm nay tui chưa thấy thành viên nào có sinh nhật trong phần hồ sơ Zalo mà Bott được phép xem.",
                    7 => "Trong 7 ngày tới tui chưa thấy thành viên nào có sinh nhật trong phần hồ sơ Zalo mà Bott được phép xem.",
                    _ => $"Trong {windowDays} ngày sắp tới tui chưa thấy thành viên nào có sinh nhật trong phần hồ sơ Zalo mà Bott được phép xem."
                }
                : $"Tui chưa thấy thành viên nào có ngày sinh tháng {month} trong phần hồ sơ Zalo mà Bott được phép xem.";
            return new ZaloMemberBotAnswer(
                emptyText + " Người ẩn ngày sinh sẽ không hiện trong danh sách này.",
                ZaloBotIntent.GeneralChat);
        }

        var lines = birthdays.Select(item => hasWindow && item.DaysUntil > 0
            ? $"- {item.Occurrence:dd/MM}: {item.DisplayName} (còn {item.DaysUntil} ngày)"
            : $"- {item.Occurrence:dd/MM}: {item.DisplayName}");
        var heading = hasWindow
            ? windowDays switch
            {
                0 => "Sinh nhật hôm nay tui đọc được trong nhóm:",
                7 => "Sinh nhật trong 7 ngày tới tui đọc được trong nhóm:",
                _ => $"Sinh nhật sắp tới trong {windowDays} ngày tui đọc được trong nhóm:"
            }
            : $"Sinh nhật tháng {month} tui đọc được trong nhóm:";
        var text =
            heading + "\n" +
            string.Join('\n', lines) +
            "\n\nDanh sách này có cả người Bott chưa kết bạn; người ẩn ngày sinh trên Zalo sẽ không hiện.";
        return new ZaloMemberBotAnswer(text, ZaloBotIntent.GeneralChat);
    }

    private static BirthdayMember BuildBirthdayMember(
        BridgeUserProfile profile,
        DateOnly birthday,
        DateOnly localToday,
        IReadOnlyDictionary<string, string> nameById)
    {
        var userId = NormalizeId(profile.ZaloUserId);
        var occurrence = GetNextBirthdayOccurrence(localToday, birthday);
        return new BirthdayMember(
            userId,
            nameById.GetValueOrDefault(userId) ?? profile.DisplayName,
            birthday,
            occurrence,
            occurrence.DayNumber - localToday.DayNumber,
            profile.IsFriend);
    }

    internal static bool TryParseBirthdayWindow(string? question, out int days)
    {
        days = 0;
        var normalized = ZaloBotIntelligence.Normalize(question ?? string.Empty);
        var birthdayIntent = normalized.Contains("sinh nhat", StringComparison.Ordinal) ||
                             normalized.Contains("birthday", StringComparison.Ordinal);
        if (!birthdayIntent) return false;

        if (normalized.Contains("hom nay", StringComparison.Ordinal))
        {
            days = 0;
            return true;
        }
        if (normalized.Contains("sap toi", StringComparison.Ordinal))
        {
            days = 30;
            return true;
        }
        if (Regex.IsMatch(normalized, @"\b7\s*ngay\s*(?:toi|nua)\b", RegexOptions.CultureInvariant) ||
            normalized.Contains("tuan toi", StringComparison.Ordinal))
        {
            days = 7;
            return true;
        }
        return false;
    }

    internal static bool TryParseBirthdayMonth(string? question, out int month)
    {
        month = 0;
        var normalized = ZaloBotIntelligence.Normalize(question ?? string.Empty);
        var birthdayIntent = normalized.Contains("sinh nhat", StringComparison.Ordinal) ||
                             normalized.Contains("birthday", StringComparison.Ordinal) ||
                             Regex.IsMatch(normalized, @"\bsinh\s+thang\s*(?:1[0-2]|[1-9]|nay)\b", RegexOptions.CultureInvariant);
        if (!birthdayIntent)
            return false;

        if (normalized.Contains("thang nay", StringComparison.Ordinal))
        {
            month = DateTimeOffset.UtcNow.ToOffset(BirthdayVietnamOffset).Month;
            return true;
        }

        var match = Regex.Match(normalized, @"\bthang\s*(1[0-2]|[1-9])\b", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out month);
    }

    internal static bool TryParseBirthday(BridgeUserProfile profile, out DateOnly birthday)
    {
        birthday = default;
        var raw = profile.BirthDate?.Trim();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var format in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy" })
            {
                if (DateOnly.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out birthday))
                    return true;
            }
        }

        if (profile.BirthDateUnix is not > 0) return false;
        try
        {
            var value = profile.BirthDateUnix.Value;
            var instant = value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            birthday = DateOnly.FromDateTime(instant.ToOffset(BirthdayVietnamOffset).DateTime);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static DateOnly GetNextBirthdayOccurrence(DateOnly today, DateOnly birthday)
    {
        for (var year = today.Year; year <= today.Year + 8; year++)
        {
            if (birthday.Day > DateTime.DaysInMonth(year, birthday.Month))
                continue;
            var candidate = new DateOnly(year, birthday.Month, birthday.Day);
            if (candidate >= today)
                return candidate;
        }

        return new DateOnly(today.Year + 1, birthday.Month, Math.Min(birthday.Day, DateTime.DaysInMonth(today.Year + 1, birthday.Month)));
    }

    internal sealed record BirthdayMember(
        string ZaloUserId,
        string DisplayName,
        DateOnly Birthday,
        DateOnly Occurrence,
        int DaysUntil,
        bool? IsFriend);
}
