using System.Globalization;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public static class ZaloNaturalCommandParser
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static ZaloReminderCommand EnrichReminder(
        string question,
        ZaloReminderCommand basic,
        DateTimeOffset? currentVietnamTime = null)
    {
        var normalized = ZaloBotIntelligence.Normalize(question);
        var now = (currentVietnamTime ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);
        var localTime = TryReadClock(normalized);
        var explicitDate = TryReadDate(normalized, now);
        var audience = Regex.IsMatch(
            normalized,
            @"(?:nguoi\s+(?:trong\s+)?(?:team|doi)|nguoi\s+(?:(?:da|tham\s+gia)\s+)?vote(?:\s+va\s+share\s+slot)?|nguoi\s+(?:da\s+)?tham\s+gia|thanh\s+vien\s+(?:da\s+)?tham\s+gia|nguoi\s+co\s+ten\s+trong\s+danh\s+sach|nguoi\s+(?:da\s+)?dang\s+ky|18\s+nguoi|danh\s+sach\s+(?:choi|vote)|cac\s+voter)",
            RegexOptions.CultureInvariant)
            ? ZaloReminderAudience.Roster
            : ZaloReminderAudience.All;
        var explicitlyOnlyIfMissing = Regex.IsMatch(
            normalized,
            @"(?:thieu\s+(?:nguoi|slot)|chua\s+du|con\s+thieu|rut\s+vote|du\s+(?:vote|slot|nguoi).*(?:thoi|dung|ngung))",
            RegexOptions.CultureInvariant);
        var stopWhenFull = RequestsStopWhenFull(normalized);
        var customMessage = ExtractReminderMessageCleanV2(question);
        if (basic.Kind == ZaloReminderCommandKind.Update && IsAudienceOnlyInstruction(customMessage))
        {
            customMessage = null;
        }
        if (explicitlyOnlyIfMissing && customMessage is not null &&
            Regex.IsMatch(ZaloBotIntelligence.Normalize(customMessage), @"^(?:neu\s+)?(?:con\s+)?(?:thieu|chua\s+du).*$"))
        {
            customMessage = null;
        }
        var onlyIfMissing = explicitlyOnlyIfMissing || customMessage is null;

        var sessionReferences = Regex.Matches(
                normalized,
                @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s+(?:hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?![a-z0-9])",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var useSessionDate = localTime is not null && sessionReferences.Count > 0;

        return basic with
        {
            DelayMinutes = localTime is null ? basic.DelayMinutes : null,
            Repeats = localTime is null && basic.Repeats,
            LocalTime = localTime,
            ExplicitLocalDate = explicitDate,
            UseSessionDate = useSessionDate,
            CustomMessage = customMessage,
            Audience = audience,
            OnlyIfMissingSlots = onlyIfMissing,
            SessionReferences = sessionReferences,
            StopWhenFull = stopWhenFull
        };
    }

    public static string? SanitizeReminderMessage(string? candidate, string originalQuestion)
    {
        var cleaned = candidate?.Trim(' ', ',', '.', ':', ';', '"', '\'', '“', '”');
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        var normalized = ZaloBotIntelligence.Normalize(cleaned);
        var containsSchedulingControls = Regex.IsMatch(
            normalized,
            @"(?:cu|cach|moi)\s+\d+\s*(?:h|gio|tieng|phut)|khi\s+nao.*(?:du|qua\s+ngay).*(?:thoi|dung|ngung)|qua\s+(?:ngay|buoi|tran).*(?:thoi|dung|ngung)|(?:tao|dat|hen|len)\s+lich",
            RegexOptions.CultureInvariant);
        var looksLikeAudienceInstruction = Regex.IsMatch(
            normalized,
            @"^(?:tag|mention|nhac)\s+(?:moi\s+nguoi|ca\s+nhom|thanh\s+vien|nguoi\s+vote)",
            RegexOptions.CultureInvariant);
        if (!containsSchedulingControls && !looksLikeAudienceInstruction)
            return cleaned;

        var source = ZaloBotIntelligence.Normalize(originalQuestion);
        if (Regex.IsMatch(source, @"\bvote\b", RegexOptions.CultureInvariant))
            return "Mọi người vào vote giúp để buổi chơi sớm đủ người nhé!";
        if (source.Contains("mang nuoc", StringComparison.Ordinal) &&
            source.Contains("dung gio", StringComparison.Ordinal))
            return "Mọi người nhớ mang nước và đến đúng giờ nhé!";
        if (source.Contains("mang nuoc", StringComparison.Ordinal))
            return "Mọi người nhớ mang nước nhé!";
        if (Regex.IsMatch(source, @"(?:len\s+san|tham\s+gia|co\s+mat)", RegexOptions.CultureInvariant))
            return "Mọi người nhớ có mặt đúng giờ nhé!";
        return null;
    }

    public static bool RequestsStopWhenFull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Regex.IsMatch(
            ZaloBotIntelligence.Normalize(value),
            @"du\s+(?:vote|slot|nguoi).*(?:thoi|dung|ngung)",
            RegexOptions.CultureInvariant);
    }

    public static bool TryParseShareSlot(string question, out ZaloShareSlotCommand command)
    {
        command = new ZaloShareSlotCommand(string.Empty, [], 0);
        var value = question.Trim();
        var match = Regex.Match(
            value,
            @"^(?<anchor>.+?)\s+(?:muốn|muon|xin)\s+(?:(?:share|chung|đánh\s+chung|danh\s+chung|chơi\s+chung|choi\s+chung)\s*(?:một\s+|mot\s+)?slot\s+(?:với|voi)|\+(?<count>[12])\s+(?:cho|với|voi))\s+(?<partners>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                value,
                @"^(?<anchor>.+?)\s+(?:muốn\s+|muon\s+)?(?:(?:share|chung|đánh\s+chung|danh\s+chung|chơi\s+chung|choi\s+chung)\s+(?:một\s+|mot\s+)?slot|thay\s+phiên|thay\s+phien)\s+(?:với|voi)\s+(?<partners>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success) return false;

        var partners = SplitPeople(match.Groups["partners"].Value);
        var requestedCount = int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : partners.Count;
        command = new ZaloShareSlotCommand(
            CleanPerson(match.Groups["anchor"].Value),
            partners,
            requestedCount);
        return command.Anchor.Length > 0 && requestedCount is >= 1 and <= 2;
    }

    public static IReadOnlyList<string> SplitPeople(string value)
    {
        var cleaned = Regex.Replace(
            value,
            @"\s+(?:ở|o|cho|trận|tran|buổi|buoi)\s+(?:t[2-7]|cn|thứ\s+\S+|thu\s+\S+).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Split(cleaned, @"\s*(?:,|;|&|\+|\bvà\b|\bva\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(CleanPerson)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static TimeOnly? TryReadClock(string normalized)
    {
        var match = Regex.Match(
            normalized,
            @"(?<!\d)(?<hour>[01]?\d|2[0-3])(?:(?:\s*:\s*(?<minute>[0-5]?\d))|(?:\s*h\s*(?<minute2>[0-5]?\d)?))?\s*(?<period>gio\s+chieu|chieu|toi|sang|trua|pm|am)(?![a-z])",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                normalized,
                @"(?:luc|vao|khoang)\s+(?<hour>[01]?\d|2[0-3])(?:(?:\s*:\s*(?<minute>[0-5]?\d))|(?:\s*h\s*(?<minute2>[0-5]?\d)?))?(?!\s*(?:gio|tieng))",
                RegexOptions.CultureInvariant);
        }
        if (!match.Success) return null;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minuteGroup = match.Groups["minute"].Success ? match.Groups["minute"] : match.Groups["minute2"];
        var minute = minuteGroup.Success && minuteGroup.Value.Length > 0
            ? int.Parse(minuteGroup.Value, CultureInfo.InvariantCulture)
            : 0;
        var period = match.Groups["period"].Value;
        if ((period.Contains("chieu", StringComparison.Ordinal) || period == "toi" || period == "pm") && hour < 12)
            hour += 12;
        if (period == "trua" && hour < 11) hour += 12;
        if (period == "am" && hour == 12) hour = 0;
        return new TimeOnly(hour, minute);
    }

    private static DateOnly? TryReadDate(string normalized, DateTimeOffset now)
    {
        if (Regex.IsMatch(normalized, @"(?<![a-z])ngay\s+mai(?![a-z])|(?<![a-z])mai(?![a-z])"))
            return DateOnly.FromDateTime(now.Date.AddDays(1));
        if (Regex.IsMatch(normalized, @"hom\s+nay")) return DateOnly.FromDateTime(now.Date);
        var match = Regex.Match(normalized, @"(?<!\d)(?<day>[0-3]?\d)[/-](?<month>[01]?\d)(?:[/-](?<year>\d{4}))?(?!\d)");
        if (!match.Success) return null;
        var year = match.Groups["year"].Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : now.Year;
        return DateOnly.TryParseExact(
            $"{match.Groups["day"].Value}/{match.Groups["month"].Value}/{year}",
            "d/M/yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? ExtractReminderMessage(string question)
    {
        var matches = Regex.Matches(
            question,
            @"(?:nhắc\s+nhở|nhac\s+nho|nhắc|nhac|nhắn|nhan)\s+(?:cho\s+)?(?:(?:mọi\s+người|moi\s+nguoi|cả\s+nhóm|ca\s+nhom|những\s+người\s+trong\s+(?:team|đội)|nhung\s+nguoi\s+trong\s+(?:team|doi)|(?:18|các|cac)\s+người\s+(?:đã\s+)?vote)\s+)?(?<message>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) return null;
        var message = matches[^1].Groups["message"].Value.Trim(' ', ',', '.', ':', ';');
        if (message.Length is < 2 or > 2000) return null;
        return message;
    }

    private static string? ExtractReminderMessageClean(string question)
    {
        var normalized = ZaloBotIntelligence.Normalize(question);
        var match = Regex.Match(
            normalized,
            @"(?:nhac\s+nho|nhac|nhan)\s+(?:cho\s+)?(?:(?:moi\s+nguoi|ca\s+nhom|nhung\s+nguoi(?:\s+da)?\s+vote(?:\s+va\s+share\s+slot)?|nguoi(?:\s+da)?\s+vote(?:\s+va\s+share\s+slot)?|nhung\s+nguoi\s+trong\s+(?:team|doi|danh\s+sach)|nguoi\s+trong\s+(?:team|doi|danh\s+sach)|18\s+nguoi(?:\s+da)?\s+vote)\s+)?(?<message>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);
        if (!match.Success) return null;
        var normalizedMessage = match.Groups["message"].Value.Trim(' ', ',', '.', ':', ';');
        if (normalizedMessage.Length is < 2 or > 2000) return null;

        var originalMessage = RestoreOriginalSegment(question, normalized, match.Groups["message"].Index, match.Groups["message"].Length);
        return originalMessage.Trim(' ', ',', '.', ':', ';');
    }

    private static string RestoreOriginalSegment(
        string original,
        string normalized,
        int normalizedStart,
        int normalizedLength)
    {
        var map = BuildNormalizedSourceMap(original);
        if (normalized.Length != map.Count || normalizedLength <= 0 || normalizedStart < 0 || normalizedStart + normalizedLength > map.Count)
            return normalized.Substring(normalizedStart, normalizedLength);

        var originalStart = map[normalizedStart];
        var originalEnd = map[normalizedStart + normalizedLength - 1] + 1;
        return original[originalStart..originalEnd];
    }

    private static IReadOnlyList<int> BuildNormalizedSourceMap(string value)
    {
        var normalized = new System.Text.StringBuilder();
        var sourceMap = new List<int>();
        for (var index = 0; index < value.Length; index += 1)
        {
            var chunk = value[index].ToString().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            foreach (var character in chunk)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                var output = character == 'đ' ? 'd' : character;
                if (char.IsWhiteSpace(output))
                {
                    if (normalized.Length == 0 || normalized[^1] == ' ') continue;
                    output = ' ';
                }
                normalized.Append(output);
                sourceMap.Add(index);
            }
        }
        while (normalized.Length > 0 && normalized[0] == ' ')
        {
            normalized.Remove(0, 1);
            sourceMap.RemoveAt(0);
        }
        while (normalized.Length > 0 && normalized[^1] == ' ')
        {
            normalized.Length -= 1;
            sourceMap.RemoveAt(sourceMap.Count - 1);
        }
        return sourceMap;
    }

    private static string? ExtractReminderMessageCleanV2(string question)
    {
        var quoted = Regex.Match(
            question,
            @"[""“](?<message>[^""”]{2,2000})[""”]",
            RegexOptions.CultureInvariant);
        if (quoted.Success)
            return quoted.Groups["message"].Value.Trim(' ', ',', '.', ':', ';', '"', '\'', '“', '”');

        var normalized = ZaloBotIntelligence.Normalize(question);
        var markers = Regex.Matches(
            normalized,
            @"(?<![a-z])(?:nhac\s+nho|nhac|nhan)(?![a-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        const string targetPattern =
            @"(?:moi\s+nguoi|ca\s+nhom|nhung\s+nguoi(?:(?:\s+da|\s+tham\s+gia))?\s+vote(?:\s+va\s+share\s+slot)?|nguoi(?:(?:\s+da|\s+tham\s+gia))?\s+vote(?:\s+va\s+share\s+slot)?|nguoi\s+(?:da\s+)?tham\s+gia|thanh\s+vien\s+(?:da\s+)?tham\s+gia|nguoi\s+co\s+ten\s+trong\s+danh\s+sach|nhung\s+nguoi\s+trong\s+(?:team|doi|danh\s+sach)|nguoi\s+trong\s+(?:team|doi|danh\s+sach)|18\s+nguoi(?:\s+da)?\s+vote)";

        for (var index = markers.Count - 1; index >= 0; index -= 1)
        {
            var candidate = normalized[markers[index].Index..];
            var match = Regex.Match(
                candidate,
                $@"^(?:nhac\s+nho|nhac|nhan)\s+(?:cho\s+)?(?:{targetPattern}\s+)?(?<message>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var normalizedMessage = match.Groups["message"].Value.Trim(' ', ',', '.', ':', ';');
            if (normalizedMessage.Length is < 2 or > 2000) continue;

            for (var originalIndex = 0; originalIndex < question.Length; originalIndex += 1)
            {
                var originalCandidate = question[originalIndex..].Trim(' ', ',', '.', ':', ';');
                if (ZaloBotIntelligence.Normalize(originalCandidate) == normalizedMessage)
                    return originalCandidate.Trim(' ', ',', '.', ':', ';', '"', '\'', '“', '”');
            }
            return normalizedMessage.Trim(' ', ',', '.', ':', ';', '"', '\'', '“', '”');
        }
        return null;
    }

    private static bool IsAudienceOnlyInstruction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = ZaloBotIntelligence.Normalize(value);
        return Regex.IsMatch(
            normalized,
            @"^(?:chi\s+)?(?:nhac\s+)?(?:cho\s+)?(?:nhung\s+)?(?:nguoi\s+(?:(?:da|tham\s+gia)\s+)?vote|nguoi\s+(?:da\s+)?tham\s+gia|thanh\s+vien\s+(?:da\s+)?tham\s+gia|nguoi\s+trong\s+(?:team|doi|danh\s+sach)|nguoi\s+co\s+ten\s+trong\s+danh\s+sach|ca\s+nhom|moi\s+nguoi)$",
            RegexOptions.CultureInvariant);
    }

    private static string CleanPerson(string value) =>
        value.Trim(' ', ',', '.', ':', ';', '@');
}
