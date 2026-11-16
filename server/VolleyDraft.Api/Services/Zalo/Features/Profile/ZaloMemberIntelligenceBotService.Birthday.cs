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
        if (!TryParseBirthdayMonth(question, out var month)) return null;
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
            logger.LogWarning(exception, "Birthday profile lookup failed Group={GroupId} Month={Month}", groupId, month);
            return new ZaloMemberBotAnswer(
                "Zalo đang giới hạn việc đọc hồ sơ thành viên nên tui chưa kiểm tra sinh nhật được. Thử lại sau nha.",
                ZaloBotIntent.GeneralChat);
        }

        var nameById = members.ToDictionary(item => NormalizeId(item.ZaloUserId), item => item.DisplayName, StringComparer.Ordinal);
        var birthdays = profiles
            .Select(profile => TryParseBirthday(profile, out var birthday)
                ? new BirthdayMember(
                    NormalizeId(profile.ZaloUserId),
                    nameById.GetValueOrDefault(NormalizeId(profile.ZaloUserId)) ?? profile.DisplayName,
                    birthday,
                    profile.IsFriend)
                : null)
            .Where(item => item is not null && item.Birthday.Month == month)
            .Select(item => item!)
            .OrderBy(item => item.Birthday.Day)
            .ThenBy(item => item.DisplayName)
            .ToList();

        if (birthdays.Count == 0)
        {
            return new ZaloMemberBotAnswer(
                $"Tui chưa thấy thành viên nào có ngày sinh tháng {month} trong phần hồ sơ Zalo mà Bott được phép xem. Người ẩn ngày sinh sẽ không hiện trong danh sách này.",
                ZaloBotIntent.GeneralChat);
        }

        var lines = birthdays.Select(item => $"- {item.Birthday:dd/MM}: {item.DisplayName}");
        var text =
            $"Sinh nhật tháng {month} tui đọc được trong nhóm:\n" +
            string.Join('\n', lines) +
            "\n\nDanh sách này có cả người Bott chưa kết bạn; người ẩn ngày sinh trên Zalo sẽ không hiện.";
        return new ZaloMemberBotAnswer(text, ZaloBotIntent.GeneralChat);
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

    internal sealed record BirthdayMember(
        string ZaloUserId,
        string DisplayName,
        DateOnly Birthday,
        bool? IsFriend);
}
