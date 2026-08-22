using System.Text.Json;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloConditionalGuestIntentDraft(
    int Quantity,
    int MinimumMissingSlots,
    int Hour,
    int Minute,
    bool ExplicitEvening,
    IReadOnlyList<ZaloRecruitmentGuestSpec> Guests);

internal static partial class ZaloConditionalGuestIntentPolicy
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [GeneratedRegex(@"(?:^|\s)(?:neu|nếu)\s+(?<hour>\d{1,2})(?:(?:h|:)(?<minute>\d{1,2}))?\s*(?<evening>toi|tối|chieu|chiều)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex IfTimeRegex();

    [GeneratedRegex(@"(?:^|\s)(?<hour>\d{1,2})(?:(?:h|:)(?<minute>\d{1,2}))?\s*(?<evening>toi|tối|chieu|chiều)?\s+(?:ma|mà)\s+(?:van|vẫn)\s+thieu\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeIfMissingRegex();

    [GeneratedRegex(@"(?:\+|cho\s+|them\s+|thêm\s+)(?<quantity>[12])\s*(?:ban|bạn)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"(?:con|còn)\s+thieu\s+(?<missing>[12])\s*(?:slot|cho|chỗ)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MissingCountRegex();

    internal static bool LooksConditional(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length is 0 or > 400) return false;
        var conditional = normalized.Contains("neu ", StringComparison.Ordinal) ||
                          normalized.Contains(" ma van thieu", StringComparison.Ordinal) ||
                          normalized.Contains(" ma con thieu", StringComparison.Ordinal);
        return conditional && normalized.Contains("thieu", StringComparison.Ordinal) &&
               (normalized.Contains("+1", StringComparison.Ordinal) ||
                normalized.Contains("+2", StringComparison.Ordinal) ||
                normalized.Contains("cho 1", StringComparison.Ordinal) ||
                normalized.Contains("cho 2", StringComparison.Ordinal) ||
                normalized.Contains("them 1", StringComparison.Ordinal) ||
                normalized.Contains("them 2", StringComparison.Ordinal));
    }

    internal static ZaloConditionalGuestIntentDraft? TryParse(string? content)
    {
        var text = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (!LooksConditional(text)) return null;

        var time = IfTimeRegex().Match(text);
        if (!time.Success) time = TimeIfMissingRegex().Match(text);
        if (!time.Success || !int.TryParse(time.Groups["hour"].Value, out var hour) || hour is < 0 or > 23)
            return null;
        var minute = 0;
        if (time.Groups["minute"].Success &&
            (!int.TryParse(time.Groups["minute"].Value, out minute) || minute is < 0 or > 59))
            return null;

        var quantityMatch = QuantityRegex().Match(text);
        if (!quantityMatch.Success || !int.TryParse(quantityMatch.Groups["quantity"].Value, out var quantity) || quantity is not (1 or 2))
            return null;

        var missing = 1;
        var missingMatch = MissingCountRegex().Match(text);
        if (missingMatch.Success && int.TryParse(missingMatch.Groups["missing"].Value, out var parsedMissing))
            missing = Math.Clamp(parsedMissing, 1, 2);

        var explicitEvening = time.Groups["evening"].Success;
        var guests = Enumerable.Range(0, quantity).Select(_ => new ZaloRecruitmentGuestSpec()).ToArray();
        return new(quantity, missing, hour, minute, explicitEvening, guests);
    }

    internal static DateTimeOffset? ResolveRequestedTrigger(
        ZaloConditionalGuestIntentDraft draft,
        DateTimeOffset nowUtc,
        DateTimeOffset sessionStartUtc)
    {
        var now = nowUtc.ToOffset(VietnamOffset);
        var start = sessionStartUtc.ToOffset(VietnamOffset);
        var date = start.Date;
        var hours = new List<int>();
        if (draft.ExplicitEvening && draft.Hour <= 11)
        {
            hours.Add(draft.Hour + 12);
        }
        else
        {
            hours.Add(draft.Hour);
            if (draft.Hour <= 11) hours.Add(draft.Hour + 12);
        }

        var candidates = hours.Distinct()
            .Where(hour => hour <= 23)
            .Select(hour => new DateTimeOffset(date.Year, date.Month, date.Day, hour, draft.Minute, 0, VietnamOffset))
            .Where(candidate => candidate > now && candidate < start)
            .OrderByDescending(candidate => candidate)
            .ToArray();
        return candidates.FirstOrDefault() == default ? null : candidates[0].ToUniversalTime();
    }

    internal static DateTimeOffset ResolveExecuteNotBefore(
        DateTimeOffset requestedTriggerUtc,
        DateTimeOffset sessionStartUtc,
        IConfiguration configuration)
    {
        var signupHours = Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:GuestSignupHoursBeforeStart", 2), 1, 6);
        var addWindowStart = sessionStartUtc.AddHours(-signupHours);
        return requestedTriggerUtc > addWindowStart ? requestedTriggerUtc : addWindowStart;
    }

    internal static string SerializeGuests(IReadOnlyList<ZaloRecruitmentGuestSpec> guests) => JsonSerializer.Serialize(guests);

    internal static IReadOnlyList<ZaloRecruitmentGuestSpec> DeserializeGuests(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ZaloRecruitmentGuestSpec[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string FormatLocalTime(DateTimeOffset utc) => utc.ToOffset(VietnamOffset).ToString("HH:mm");
}
