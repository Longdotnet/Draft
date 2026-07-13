using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public enum ZaloBotIntent
{
    Unknown,
    Help,
    SessionSchedule,
    SelfMembership,
    LocationParking,
    MissingSlots,
    UpcomingSessions,
    PaymentQr,
    Roster,
    WeeklySessionCount,
    ModelInfo,
    TeamLineup,
    SyncPoll,
    AutoDraft,
    AutoDraftConfirm,
    Redraft,
    RedraftConfirm,
    SwapTeamPlayers,
    IncompleteProfiles,
    UpdatePlayerProfile,
    AddGuestPlayer,
    ShareSlot,
    TeamImage,
    ScheduleReminder,
    ReminderStatus,
    CancelReminder,
    GeneralChat
}

public enum ZaloReminderCommandKind
{
    Schedule,
    TriggerNow,
    Status,
    Disable
}

public sealed record ZaloReminderCommand(
    ZaloReminderCommandKind Kind,
    int? DelayMinutes,
    bool Repeats);

public sealed record ZaloIntentDecision(
    ZaloBotIntent Intent,
    double Confidence,
    string? SessionReference,
    bool NeedsClarification,
    string? ClarificationQuestion,
    string? Reason);

public sealed record ZaloSessionReference(string Id, string Name, DateTimeOffset? StartTime);

public static class ZaloBotIntelligence
{
    private static readonly Regex ExactCommandRegex = new("^(?:[1-9]|10)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ai", "hoi", "hay", "la", "thi", "cho", "minh", "tui", "toi", "em", "anh", "chi",
        "bot", "npc", "nhe", "nha", "voi", "ve", "o", "dau", "gi", "nao", "xem", "giup",
        "luon", "nua", "cai", "do", "nay", "kia", "duoc", "khong", "phai"
    };

    public static string Normalize(string value)
    {
        var decomposed = (value ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == 'đ' ? 'd' : character);
            }
        }
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    public static bool TryGetExactCommand(string value, out int command)
    {
        var normalized = Normalize(value);
        if (ExactCommandRegex.IsMatch(normalized) && int.TryParse(normalized, out command)) return true;
        command = 0;
        return false;
    }

    public static ZaloBotIntent IntentForCommand(int command) => command switch
    {
        1 => ZaloBotIntent.SessionSchedule,
        2 => ZaloBotIntent.SelfMembership,
        3 => ZaloBotIntent.LocationParking,
        4 => ZaloBotIntent.MissingSlots,
        5 => ZaloBotIntent.UpcomingSessions,
        6 => ZaloBotIntent.PaymentQr,
        7 => ZaloBotIntent.TeamLineup,
        8 => ZaloBotIntent.SyncPoll,
        9 => ZaloBotIntent.AutoDraft,
        10 => ZaloBotIntent.TeamImage,
        _ => ZaloBotIntent.Unknown
    };

    public static bool IsCancel(string value)
    {
        var normalized = Normalize(value);
        return normalized is "huy" or "cancel" or "thoi" or "bo qua" or "khong can nua";
    }

    public static bool IsConfirmation(string value)
    {
        var normalized = Normalize(value);
        return normalized is "xac nhan" or "xac nhan draft" or "dong y" or "ok chay" or "chay di" or "thuc hien di" ||
               normalized.StartsWith("xac nhan draft ", StringComparison.Ordinal);
    }

    public static bool TryParseReminderCommand(string value, out ZaloReminderCommand command)
    {
        command = new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, true);
        var q = Normalize(value);
        var mentionsReminder = Has(q,
            "nhac", "reminder", "tag @all", "tag all", "tag moi nguoi", "bao moi nguoi", "goi moi nguoi");
        if (!mentionsReminder) return false;

        if (Has(q, "xem lich nhac", "lich nhac hien tai", "lich reminder", "khi nao nhac", "bao gio nhac", "con hen nhac"))
        {
            command = new ZaloReminderCommand(ZaloReminderCommandKind.Status, null, true);
            return true;
        }

        if (Has(q, "tat nhac", "dung nhac", "huy lich nhac", "bo lich nhac", "khong nhac nua", "tat reminder"))
        {
            command = new ZaloReminderCommand(ZaloReminderCommandKind.Disable, null, false);
            return true;
        }

        if (Has(q, "nhac ngay", "tag ngay", "bao moi nguoi ngay", "goi moi nguoi ngay") ||
            Regex.IsMatch(q, @"(?:nhac|tag|bao|goi).*\bngay[.!?]*$", RegexOptions.CultureInvariant))
        {
            command = new ZaloReminderCommand(ZaloReminderCommandKind.TriggerNow, 0, true);
            return true;
        }

        var duration = Regex.Match(
            q,
            @"(?<!\d)(?<amount>\d{1,3})\s*(?<unit>phut|p|gio|tieng|h)(?![a-z0-9])",
            RegexOptions.CultureInvariant);
        if (!duration.Success || !int.TryParse(duration.Groups["amount"].Value, out var amount))
        {
            command = new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, true);
            return true;
        }

        var unit = duration.Groups["unit"].Value;
        var minutes = unit is "phut" or "p" ? amount : amount * 60;
        if (minutes is < 5 or > 10_080) return false;
        var recurringPhrase = Regex.IsMatch(q, @"\b(?:cu|moi)\s+(?:moi\s+)?\d", RegexOptions.CultureInvariant);
        var oneTime = !recurringPhrase &&
                      Has(q, "chi mot lan", "mot lan thoi", "chi lan nay", "nhac 1 lan", "tag 1 lan");
        command = new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, minutes, !oneTime);
        return true;
    }

    public static bool TryExtractSwapPlayerNames(string value, out string firstPlayer, out string secondPlayer)
    {
        firstPlayer = string.Empty;
        secondPlayer = string.Empty;
        var match = Regex.Match(
            value ?? string.Empty,
            @"(?:đổi\s+(?:(?:vị\s*trí|chỗ)\s+)?|swap\s+)(?<first>.+?)\s+(?:với|và|cho)\s+(?<second>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        firstPlayer = match.Groups["first"].Value.Trim(' ', ',', '.', ':', ';');
        secondPlayer = match.Groups["second"].Value.Trim(' ', ',', '.', ':', ';');
        return firstPlayer.Length is > 0 and <= 160 && secondPlayer.Length is > 0 and <= 160;
    }

    public static bool TryExtractSharePlayerNames(string value, out string anchorPlayer, out string partnerPlayer)
    {
        anchorPlayer = string.Empty;
        partnerPlayer = string.Empty;
        var match = Regex.Match(
            value ?? string.Empty,
            @"^(?<anchor>.+?)\s+(?:muốn\s+)?(?:(?:share|chung|đánh\s+chung|chơi\s+chung)\s+(?:một\s+)?slot|thay\s+phiên)\s+với\s+(?<partner>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        anchorPlayer = match.Groups["anchor"].Value.Trim(' ', ',', '.', ':', ';');
        partnerPlayer = match.Groups["partner"].Value.Trim(' ', ',', '.', ':', ';');
        return anchorPlayer.Length is > 0 and <= 160 && partnerPlayer.Length is > 0 and <= 160;
    }

    public static bool IsProtectedBusinessFactText(string value)
    {
        var q = Normalize(value);
        return Regex.IsMatch(q, @"(?<!\d)\d{1,2}[:h]\d{0,2}(?!\d)", RegexOptions.CultureInvariant) ||
               Has(q, "danh sach", "doi hinh", "slot", "dia diem", "san ", "gui xe", "thanh toan", "qr", "lich dau", "gio dau", "ngay dau", "co ten", "tham gia");
    }

    public static ZaloIntentDecision ClassifyDeterministically(string value)
    {
        var q = Normalize(value);
        if (TryGetExactCommand(q, out var command))
            return new(IntentForCommand(command), 1, null, false, null, "exact_numeric_command");
        if (TryParseReminderCommand(value, out var reminderCommand))
        {
            var intent = reminderCommand.Kind switch
            {
                ZaloReminderCommandKind.Status => ZaloBotIntent.ReminderStatus,
                ZaloReminderCommandKind.Disable => ZaloBotIntent.CancelReminder,
                _ => ZaloBotIntent.ScheduleReminder
            };
            return new(intent, .99, q, false, null, "reminder_command");
        }
        if (Regex.IsMatch(q, @"^(help|tro giup|huong dan|menu|lenh)$", RegexOptions.CultureInvariant))
            return new(ZaloBotIntent.Help, 1, null, false, null, "exact_help");
        if (Has(q, "mot tuan danh may", "tuan nay danh may tran", "tuan co bao nhieu tran", "1 tuan danh may", "bao nhieu bua trong tuan"))
            return new(ZaloBotIntent.WeeklySessionCount, .98, null, false, null, "weekly_count_phrase");
        if ((q.StartsWith("+1 ", StringComparison.Ordinal) || Has(q, "them 1 nguoi", "cong 1 nguoi", "+1 so luong vote", "+1 slot")) &&
            Has(q, "ban", "nguoi", "vote", "slot"))
            return new(ZaloBotIntent.AddGuestPlayer, .99, q, false, null, "add_guest_player_phrase");
        if (Has(q, "share slot", "chung slot", "danh chung slot", "choi chung slot", "slot thay phien", "thay phien voi"))
            return new(ZaloBotIntent.ShareSlot, .99, q, false, null, "share_slot_phrase");
        if (IsIncompleteProfileQuery(q))
            return new(ZaloBotIntent.IncompleteProfiles, .98, q, false, null, "incomplete_profiles_phrase");
        if (Has(q, "cap nhat thong tin", "cap nhat ho so", "cap nhat trinh do", "cap nhat gioi tinh") ||
            (q.StartsWith("cap nhat ", StringComparison.Ordinal) && Has(q, " nam", " nu", "tan cong", "phong thu", "chuyen 2", "trung binh", "moi choi")))
            return new(ZaloBotIntent.UpdatePlayerProfile, .98, q, false, null, "update_player_profile_phrase");
        if (Has(q, "doi vi tri", "doi cho", "swap ") && Has(q, " voi ", " va ", " cho "))
            return new(ZaloBotIntent.SwapTeamPlayers, .99, q, false, null, "swap_team_players_phrase");
        if (Has(q, "draft lai", "chia lai team", "boc lai team", "khui lai tui", "khui lai", "draft lai tu dau"))
            return new(ZaloBotIntent.Redraft, .99, q, false, null, "redraft_phrase");
        if (Has(q, "tu khui tui", "tu khui", "tu boc team", "tu boc doi", "tu draft", "auto draft", "draft tu dong", "khui het tui", "boc het tui", "chia team tu dong"))
            return new(ZaloBotIntent.AutoDraft, .98, q, false, null, "auto_draft_phrase");
        if (Has(q, "cap nhat so luong da vote", "cap nhat nguoi vote", "dong bo vote", "sync vote", "import poll", "lay nguoi da vote", "cap nhat poll len web"))
            return new(ZaloBotIntent.SyncPoll, .98, q, false, null, "sync_poll_phrase");
        if (Has(q, "chup man hinh 3 team", "anh 3 team", "anh doi hinh", "gui anh team", "gui hinh team", "the doi hinh"))
            return new(ZaloBotIntent.TeamImage, .96, q, false, null, "team_image_phrase");
        if (Has(q, "danh sach 3 team", "danh sach ba team", "danh sach team", "danh sach cac team", "doi hinh 3 team", "3 team hom nay", "team hom nay"))
            return new(ZaloBotIntent.TeamLineup, .96, q, false, null, "team_lineup_phrase");
        if (Has(q, "qr thanh toan", "ma qr", "chuyen khoan", "thanh toan o dau"))
            return new(ZaloBotIntent.PaymentQr, .96, q, false, null, "payment_phrase");
        if (IsRoster(q)) return new(ZaloBotIntent.Roster, .96, q, false, null, "roster_phrase");
        if (IsMembership(q)) return new(ZaloBotIntent.SelfMembership, .96, q, false, null, "membership_phrase");
        if (Has(q, "vi tri", "dia diem", "gui xe", "bai xe", "location"))
            return new(ZaloBotIntent.LocationParking, .94, q, false, null, "location_phrase");
        if (Has(q, "thieu bao nhieu", "con bao nhieu", "bao nhieu slot", "du slot", "du nguoi"))
            return new(ZaloBotIntent.MissingSlots, .94, q, false, null, "slot_phrase");
        if (Has(q, "cac tran sap toi", "tran sap toi", "con bua nao", "con tran nao"))
            return new(ZaloBotIntent.UpcomingSessions, .93, q, false, null, "upcoming_phrase");
        if (Has(q, "may gio", "luc nao", "khi nao", "gio danh", "thoi gian tran"))
            return new(ZaloBotIntent.SessionSchedule, .9, q, false, null, "schedule_phrase");
        if (Has(q, "quota token", "gioi han token", "gioi han model", "model nao", "model gi"))
            return new(ZaloBotIntent.ModelInfo, .98, null, false, null, "model_info_phrase");
        return new(ZaloBotIntent.Unknown, 0, null, false, null, "no_deterministic_match");
    }

    public static bool TryParseClassifierJson(string? content, out ZaloIntentDecision decision)
    {
        decision = new(ZaloBotIntent.Unknown, 0, null, false, null, "invalid_json");
        if (string.IsNullOrWhiteSpace(content)) return false;
        var value = content.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            value = Regex.Replace(value, @"^```(?:json)?\s*|\s*```$", string.Empty, RegexOptions.IgnoreCase);
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (!root.TryGetProperty("intent", out var intentNode) ||
                !Enum.TryParse<ZaloBotIntent>(intentNode.GetString(), true, out var intent) ||
                intent is ZaloBotIntent.Unknown or ZaloBotIntent.Help or ZaloBotIntent.AutoDraftConfirm or ZaloBotIntent.RedraftConfirm) return false;
            var confidence = root.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.TryGetDouble(out var parsed)
                ? Math.Clamp(parsed, 0, 1)
                : 0;
            var reference = ReadString(root, "sessionReference");
            var needsClarification = root.TryGetProperty("needsClarification", out var clarifyNode) && clarifyNode.ValueKind == JsonValueKind.True;
            decision = new(intent, confidence, reference, needsClarification, ReadString(root, "clarificationQuestion"), ReadString(root, "reason"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static double TokenJaccard(string left, string right)
        => TokenSimilarity(left, right);

    public static double TokenSimilarity(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.Ordinal).Count();
        var union = a.Union(b, StringComparer.Ordinal).Count();
        var jaccard = union == 0 ? 0 : (double)intersection / union;
        var containment = (double)intersection / Math.Min(a.Count, b.Count);
        return Math.Max(jaccard, containment);
    }

    public static bool PrefersNearestSession(string value)
    {
        var q = Normalize(value);
        return Has(q,
            "buoi gan nhat",
            "tran gan nhat",
            "session gan nhat",
            "lich gan nhat",
            "sap toi gan nhat",
            "gan nhat luon",
            "thay vi hoi",
            "khong can hoi lai");
    }

    public static IReadOnlyList<string> ResolveSessionReference(
        string value,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var q = Normalize(value);
        var localNow = (now ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(7));
        return candidates.Where(candidate =>
        {
            if (q.Contains(Normalize(candidate.Name), StringComparison.Ordinal)) return true;
            if (candidate.StartTime is null) return false;
            var local = candidate.StartTime.Value.ToOffset(TimeSpan.FromHours(7));
            if ((q.Contains("hom nay", StringComparison.Ordinal) || q.Contains("bua nay", StringComparison.Ordinal)) && local.Date == localNow.Date) return true;
            if ((q.Contains("ngay mai", StringComparison.Ordinal) || q == "mai") && local.Date == localNow.Date.AddDays(1)) return true;
            foreach (Match match in Regex.Matches(q, @"(?<!\d)(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?(?!\d)"))
            {
                if (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == local.Day &&
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) == local.Month) return true;
            }
            var aliases = local.DayOfWeek switch
            {
                DayOfWeek.Monday => new[] { "t2", "thu 2", "thu hai" },
                DayOfWeek.Tuesday => new[] { "t3", "thu 3", "thu ba" },
                DayOfWeek.Wednesday => new[] { "t4", "thu 4", "thu tu" },
                DayOfWeek.Thursday => new[] { "t5", "thu 5", "thu nam" },
                DayOfWeek.Friday => new[] { "t6", "thu 6", "thu sau" },
                DayOfWeek.Saturday => new[] { "t7", "thu 7", "thu bay" },
                _ => new[] { "cn", "chu nhat" }
            };
            return aliases.Any(alias => Regex.IsMatch(q, $@"(?<![a-z0-9]){Regex.Escape(alias)}(?![a-z0-9])"));
        }).Select(candidate => candidate.Id).ToList();
    }

    private static HashSet<string> Tokens(string value)
    {
        var normalized = Normalize(value);
        normalized = Regex.Replace(normalized, @"\b(?:gui xe|bai xe|cho de xe)\b", " parking ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\b(?:dia diem|vi tri|san dau)\b", " location ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\b(?:thanh toan|chuyen khoan|ma qr)\b", " payment ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\b(?:danh sach|doi hinh)\b", " roster ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", " ", RegexOptions.CultureInvariant);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString()?.Trim() : null;

    private static bool IsMembership(string q) => Has(q, "minh co trong", "tui co trong", "toi co trong", "em co trong", "minh co ten", "tui co ten", "toi co ten", "em co ten", "co ten minh", "co ten tui", "co ten toi", "co ten em", "duoc vote", "da vote");
    private static bool IsIncompleteProfileQuery(string q)
    {
        var asksForMissingPeople = Has(q,
            "chua cap nhat", "chua khai", "chua co", "chua ro", "chua biet",
            "chua xac dinh", "chua du thong tin", "chua day du", "chua hoan tat",
            "thieu thong tin", "thieu ho so", "thieu profile", "con thieu thong tin",
            "can cap nhat", "can bo sung", "can khai", "ho so con thieu", "thong tin con trong");
        var mentionsProfile = Has(q,
            "gioi tinh", "trinh do", "level", "vi tri", "vai tro", "role",
            "thong tin", "ho so", "profile");
        var asksBeforeDraft = Has(q, "truoc khi draft", "truoc luc draft", "de draft", "moi draft duoc");
        var asksWhichPeople = Has(q, "con ai", "nhung ai", "nguoi nao", "danh sach nguoi", "loc nguoi");
        var isPollQuestion = Has(q, "vote", "poll", "binh chon");
        return !isPollQuestion && asksForMissingPeople && (mentionsProfile || asksBeforeDraft || asksWhichPeople);
    }
    private static bool IsRoster(string q) => (q.Contains("danh sach", StringComparison.Ordinal) && !IsMembership(q)) || Has(q, "co nhung ai", "ai tham gia", "ai danh");
    private static bool Has(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
