using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal enum ZaloAutoSessionConversationIntent
{
    None,
    ModifyDraft,
    Confirm,
    Cancel,
    Reset,
    Uncertain
}

internal enum ZaloAutoSessionSelectionMode
{
    None,
    Replace,
    Add,
    Remove
}

internal sealed record ZaloAutoSessionConversationInterpretation(
    ZaloAutoSessionConversationIntent Intent,
    IReadOnlyList<string> Days,
    ZaloAutoSessionSelectionMode SelectionMode,
    IReadOnlyDictionary<string, int> TimeOverrides,
    string? Location,
    int? TeamSize,
    bool ExplicitExecute,
    bool NeedsClarification,
    string? Clarification,
    string? QuestionType,
    double Confidence,
    string Interpreter);

internal sealed class ZaloAutoSessionConversationInterpreter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ZaloAutoSessionConversationInterpreter> logger)
{
    private static readonly Regex DayRegex = new(
        @"(?<![a-z0-9])(?:(?:t|thu)\s*(?<weekday>[2-7])|(?<sunday>cn|chu\s*nhat))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DayTimeRegex = new(
        @"(?:(?:t|thu)\s*(?<weekday>[2-7])|(?<sunday>cn|chu\s*nhat))[^0-9]{0,20}(?<hour>[0-2]?\d)\s*(?:h|:)(?:\s*(?<minute>[0-5]?\d))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GenericTimeRegex = new(
        @"(?<!\d)(?<hour>[0-2]?\d)\s*(?:h|:)(?:\s*(?<minute>[0-5]?\d))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocationRegex = new(
        @"(?:^|\s)(?:sân|san)\s+(?<location>[^,.;\n]{1,120})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TeamSizeRegex = new(
        @"(?<![a-z0-9])(?:moi\s*(?:doi|team))\s*(?<size>\d{1,2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CapacityRegex = new(
        @"(?<!\d)(?<capacity>\d{1,3})\s*(?:nguoi|ng)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ZaloAutoSessionConversationInterpretation> InterpretAsync(
        string text,
        ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationState state,
        string? lastQuestionType,
        CancellationToken cancellationToken = default)
    {
        var deterministic = InterpretByRules(text, draft, state, lastQuestionType);
        if (deterministic.Intent != ZaloAutoSessionConversationIntent.None ||
            deterministic.NeedsClarification ||
            deterministic.Confidence >= 0.8)
            return deterministic;

        var ai = await TryInterpretWithAiAsync(text, draft, state, lastQuestionType, cancellationToken);
        return ai ?? deterministic with
        {
            Intent = ZaloAutoSessionConversationIntent.Uncertain,
            Confidence = Math.Max(deterministic.Confidence, 0.35)
        };
    }

    internal static ZaloAutoSessionConversationInterpretation InterpretByRules(
        string text,
        ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationState state,
        string? lastQuestionType)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(text);
        normalized = Regex.Replace(
            normalized,
            @"(?<!\d)([0-2]?\d)\s*(?:h\s*)?ruoi(?![a-z0-9])",
            "$1h30",
            RegexOptions.CultureInvariant);
        var selectedItems = draft.Items.Where(item => item.Selected).OrderBy(item => item.StartTime).ToList();
        var allItems = draft.Items.OrderBy(item => item.StartTime).ToList();
        var days = ReadDays(normalized, allItems);
        var timeOverrides = ReadDayTimeOverrides(normalized);
        var location = ReadLocation(text);
        int? teamSize = null;

        if (days.Count > 0 &&
            timeOverrides.Count == 0 &&
            string.IsNullOrWhiteSpace(location) &&
            Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(dong qua|it qua|vang qua|ai di|ai danh|ai choi)(?![a-z0-9])",
                RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(tao|lay|chon|bo|khoi|them|doi|chi|thoi)(?![a-z0-9])",
                RegexOptions.CultureInvariant))
        {
            return Result(
                ZaloAutoSessionConversationIntent.Uncertain,
                needsClarification: true,
                clarification: "Tui hiểu đây là nhận xét về lịch/người chơi nên chưa đổi bản nháp. Nếu muốn đổi, bạn nói rõ kiểu “T6 thôi”, “bỏ T6” hoặc “T6 6h”.",
                questionType: "intent",
                confidence: 0.99);
        }

        var selectionMode = ZaloAutoSessionSelectionMode.None;
        var intent = ZaloAutoSessionConversationIntent.None;
        var explicitExecute = false;
        var confidence = 0.45;

        var ordinalDays = ReadOrdinalDays(normalized, allItems);
        if (days.Count == 0 && ordinalDays.Count > 0)
            days = ordinalDays;

        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(nhu ban dau|lam lai tu dau|reset)(?![a-z0-9])"))
            return Result(ZaloAutoSessionConversationIntent.Reset, confidence: 0.99);

        var hasSelectionSignal = days.Count > 0 ||
                                 Regex.IsMatch(normalized, @"\b(cai dau|cai cuoi|cai giua|2 cai sau)\b");
        var hasCancelSignal = normalized is "thoi" or "khoi" or "bo di" ||
                              Regex.IsMatch(
                                  normalized,
                                  @"(?<![a-z0-9])(huy|bo qua|dung tao|khong tao|thoi khoi|khoi tao)(?![a-z0-9])");

        if (hasCancelSignal && !hasSelectionSignal)
            return Result(ZaloAutoSessionConversationIntent.Cancel, confidence: 0.99);

        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(lam 2 cai|tao 2 cai|lay 2 cai)(?![a-z0-9])") &&
            !normalized.Contains("2 cai sau", StringComparison.Ordinal) &&
            days.Count == 0)
        {
            return Result(
                ZaloAutoSessionConversationIntent.ModifyDraft,
                needsClarification: true,
                clarification: BuildTwoItemClarification(allItems),
                questionType: "selection",
                confidence: 0.98);
        }

        if (days.Count > 0 &&
            !string.IsNullOrWhiteSpace(lastQuestionType) &&
            lastQuestionType.StartsWith("time-pending:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(lastQuestionType["time-pending:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pendingMinutes))
        {
            foreach (var day in days) timeOverrides[day] = Math.Clamp(pendingMinutes, 0, 1439);
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            selectionMode = ZaloAutoSessionSelectionMode.None;
            confidence = 0.99;
        }
        else if (days.Count > 0)
        {
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            var removeSignal = Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(bo|khoi|khong lay|dung lay|khong tao)(?![a-z0-9])");
            var addSignal = Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(them|cung|them ca)(?![a-z0-9])");
            var replaceSignal = Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(chi|thoi|lay|chon|tao|lam 2 cai|2 cai dau|2 cai sau)(?![a-z0-9])");

            if (removeSignal)
                selectionMode = ZaloAutoSessionSelectionMode.Remove;
            else if (addSignal)
                selectionMode = ZaloAutoSessionSelectionMode.Add;
            else if (timeOverrides.Count > 0 && !replaceSignal)
                selectionMode = ZaloAutoSessionSelectionMode.None;
            else
                selectionMode = ZaloAutoSessionSelectionMode.Replace;
            confidence = 0.95;
        }

        if (timeOverrides.Count > 0)
        {
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            confidence = Math.Max(confidence, 0.98);
        }
        else
        {
            var genericTime = GenericTimeRegex.Match(normalized);
            if (genericTime.Success &&
                TryReadMinutes(genericTime, out var minutes))
            {
                string? targetDay = null;
                if (!string.IsNullOrWhiteSpace(lastQuestionType) &&
                    lastQuestionType.StartsWith("time:", StringComparison.OrdinalIgnoreCase))
                {
                    targetDay = lastQuestionType[5..].Trim().ToUpperInvariant();
                }
                else if (days.Count == 1)
                {
                    targetDay = days[0];
                }
                else if (selectedItems.Count == 1)
                {
                    targetDay = selectedItems[0].DayKey;
                }

                if (targetDay is null)
                {
                    return Result(
                        ZaloAutoSessionConversationIntent.ModifyDraft,
                        needsClarification: true,
                        clarification: $"Bạn muốn {FormatMinutes(minutes)} cho ngày nào: {string.Join(", ", selectedItems.Select(item => item.DayKey))}?",
                        questionType: $"time-pending:{minutes}",
                        confidence: 0.99);
                }

                timeOverrides[targetDay] = minutes;
                intent = ZaloAutoSessionConversationIntent.ModifyDraft;
                confidence = 0.97;
            }
        }

        if (normalized.Contains("san cu", StringComparison.Ordinal))
            location = "__INITIAL__";

        if (!string.IsNullOrWhiteSpace(location))
        {
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            confidence = Math.Max(confidence, 0.96);
        }

        if (string.Equals(lastQuestionType, "capacity", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clarifiedCapacity) &&
            clarifiedCapacity is >= 6 and <= 90 &&
            clarifiedCapacity % 3 == 0)
        {
            teamSize = Math.Clamp(clarifiedCapacity / 3, 2, 30);
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            confidence = Math.Max(confidence, 0.99);
        }

        var teamSizeMatch = TeamSizeRegex.Match(normalized);
        if (teamSize is null &&
            teamSizeMatch.Success &&
            int.TryParse(teamSizeMatch.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTeamSize))
        {
            teamSize = Math.Clamp(parsedTeamSize, 2, 30);
            intent = ZaloAutoSessionConversationIntent.ModifyDraft;
            confidence = Math.Max(confidence, 0.98);
        }
        else if (teamSize is null)
        {
            var capacityMatch = CapacityRegex.Match(normalized);
            if (capacityMatch.Success &&
                int.TryParse(capacityMatch.Groups["capacity"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
            {
                if (capacity >= 6 && capacity <= 90 && capacity % 3 == 0)
                {
                    teamSize = Math.Clamp(capacity / 3, 2, 30);
                    intent = ZaloAutoSessionConversationIntent.ModifyDraft;
                    confidence = Math.Max(confidence, 0.98);
                }
                else if (capacity >= 6 && capacity <= 90)
                {
                    var lower = Math.Max(6, capacity / 3 * 3);
                    var upper = Math.Min(90, lower + 3);
                    return Result(
                        ZaloAutoSessionConversationIntent.ModifyDraft,
                        needsClarification: true,
                        clarification: $"3 đội thì {capacity} người không chia đều. Bạn muốn {lower} người ({lower / 3}/đội) hay {upper} người ({upper / 3}/đội)?",
                        questionType: "capacity",
                        confidence: 0.99);
                }
            }
        }

        var negatedCreate = Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])(khong tao|dung tao|khoi tao)(?![a-z0-9])");
        var directCreate = !negatedCreate && Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])(tao|tao di|lam di|chot|trien|xac nhan tao|tao website|ok tao)(?![a-z0-9])");
        var softConfirm = Regex.IsMatch(
            normalized,
            @"^(ok|oke|okay|u|uh|uhm|ừ|uh|dong y|dung roi|chuan|chot)$",
            RegexOptions.CultureInvariant);

        if (directCreate)
        {
            explicitExecute = true;
            intent = ZaloAutoSessionConversationIntent.Confirm;
            confidence = 0.99;
        }
        else if (softConfirm && state == ZaloAutoSessionConversationState.ReadyToConfirm)
        {
            intent = ZaloAutoSessionConversationIntent.Confirm;
            confidence = 0.98;
        }
        else if (softConfirm)
        {
            return Result(
                ZaloAutoSessionConversationIntent.Uncertain,
                needsClarification: true,
                clarification: "Tui chưa xem câu đó là lệnh tạo vì chưa ở bước xác nhận cuối. Bạn muốn sửa gì cứ nói; muốn tạo thì nói “tạo đi”.",
                questionType: "confirm",
                confidence: 0.95);
        }

        if (intent == ZaloAutoSessionConversationIntent.None &&
            Regex.IsMatch(normalized, @"(?<![a-z0-9])(de coi|chac vay|tuy|cung duoc|um|uhm)(?![a-z0-9])"))
        {
            return Result(
                ZaloAutoSessionConversationIntent.Uncertain,
                needsClarification: true,
                clarification: "Tui chưa chắc đó có phải lệnh tạo hay không. Website vẫn chưa được tạo. Bạn có thể nói “tạo đi”, hoặc nói phần muốn sửa.",
                questionType: "confirm",
                confidence: 0.92);
        }

        return new(
            intent,
            days,
            selectionMode,
            timeOverrides,
            location,
            teamSize,
            explicitExecute,
            false,
            null,
            null,
            confidence,
            "rules");
    }

    private async Task<ZaloAutoSessionConversationInterpretation?> TryInterpretWithAiAsync(
        string text,
        ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationState state,
        string? lastQuestionType,
        CancellationToken cancellationToken)
    {
        var endpoint = configuration["Ai:Endpoint"];
        var apiKey = configuration["Ai:ApiKey"];
        var model = configuration["Ai:Model"];
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
            return null;

        var payload = new
        {
            model,
            temperature = 0,
            max_tokens = 220,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                        Bạn chỉ diễn giải câu trả lời của trưởng/phó nhóm cho bản nháp lịch bóng chuyền.
                        TUYỆT ĐỐI không quyết định quyền tạo và không tự thực thi.
                        Trả đúng JSON:
                        {
                          "intent":"modify|confirm|cancel|reset|uncertain|none",
                          "selectionMode":"replace|add|remove|none",
                          "days":["T6","CN"],
                          "timeOverrides":{"T6":"18:00"},
                          "location":null,
                          "teamSize":null,
                          "confidence":0.90,
                          "needsClarification":false,
                          "clarification":null
                        }
                        Nếu câu nói mơ hồ, needsClarification=true. Không suy diễn ngày/giờ không có căn cứ.
                        """
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        message = text,
                        state = state.ToString(),
                        lastQuestionType,
                        draft = draft.Items.Select(item => new
                        {
                            item.DayKey,
                            item.OptionContent,
                            item.StartTime,
                            item.Selected
                        }),
                        draft.Location,
                        draft.TeamSize
                    }, JsonOptions)
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var content = ReadContent(document.RootElement);
            if (string.IsNullOrWhiteSpace(content)) return null;
            using var parsed = JsonDocument.Parse(StripCodeFence(content));
            var root = parsed.RootElement;

            var intent = ParseIntent(ReadString(root, "intent"));
            var selectionMode = ParseSelectionMode(ReadString(root, "selectionMode"));
            var days = root.TryGetProperty("days", out var daysNode) && daysNode.ValueKind == JsonValueKind.Array
                ? daysNode.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => NormalizeDay(item.GetString()))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("timeOverrides", out var timeNode) && timeNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in timeNode.EnumerateObject())
                {
                    var day = NormalizeDay(property.Name);
                    if (day is null || property.Value.ValueKind != JsonValueKind.String) continue;
                    if (TryParseClock(property.Value.GetString(), out var minutes)) overrides[day] = minutes;
                }
            }

            var location = ReadString(root, "location");
            int? teamSize = null;
            if (root.TryGetProperty("teamSize", out var teamSizeNode) &&
                teamSizeNode.TryGetInt32(out var parsedTeamSize) &&
                parsedTeamSize is >= 2 and <= 30)
                teamSize = parsedTeamSize;
            var confidence = root.TryGetProperty("confidence", out var confidenceNode) &&
                             confidenceNode.TryGetDouble(out var value)
                ? Math.Clamp(value, 0, 1)
                : 0;
            var needsClarification = root.TryGetProperty("needsClarification", out var clarificationFlag) &&
                                     clarificationFlag.ValueKind == JsonValueKind.True;
            var clarification = ReadString(root, "clarification");

            // AI may interpret/clarify only. Explicit execution is intentionally always false.
            return new(
                intent,
                days,
                selectionMode,
                overrides,
                location,
                teamSize,
                false,
                needsClarification || confidence < 0.7,
                clarification,
                needsClarification ? "ai" : null,
                confidence,
                "ai");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(exception, "Auto Session V3 AI interpretation failed; rules remain authoritative");
            return null;
        }
    }

    private static List<string> ReadDays(
        string normalized,
        IReadOnlyList<ZaloAutoSessionConversationDraftItem> items)
    {
        var available = items.Select(item => item.DayKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in DayRegex.Matches(normalized))
        {
            var day = match.Groups["weekday"].Success
                ? $"T{match.Groups["weekday"].Value}"
                : "CN";
            if (available.Contains(day) && !result.Contains(day, StringComparer.OrdinalIgnoreCase))
                result.Add(day);
        }
        return result;
    }

    private static List<string> ReadOrdinalDays(
        string normalized,
        IReadOnlyList<ZaloAutoSessionConversationDraftItem> items)
    {
        if (items.Count == 0) return [];
        if (normalized.Contains("2 cai sau", StringComparison.Ordinal) && items.Count >= 2)
            return items.Skip(Math.Max(0, items.Count - 2)).Select(item => item.DayKey).ToList();
        if (normalized.Contains("2 cai dau", StringComparison.Ordinal) && items.Count >= 2)
            return items.Take(2).Select(item => item.DayKey).ToList();
        if (normalized.Contains("cai dau", StringComparison.Ordinal) ||
            normalized.Contains("dau bo", StringComparison.Ordinal) ||
            normalized.Contains("dau khoi", StringComparison.Ordinal))
            return [items[0].DayKey];
        if (normalized.Contains("cai cuoi", StringComparison.Ordinal) ||
            normalized.Contains("cuoi bo", StringComparison.Ordinal) ||
            normalized.Contains("cuoi khoi", StringComparison.Ordinal))
            return [items[^1].DayKey];
        if ((normalized.Contains("cai giua", StringComparison.Ordinal) ||
             normalized.Contains("lich giua", StringComparison.Ordinal)) && items.Count >= 3)
            return [items[items.Count / 2].DayKey];
        return [];
    }

    private static Dictionary<string, int> ReadDayTimeOverrides(string normalized)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DayTimeRegex.Matches(normalized))
        {
            if (!TryReadMinutes(match, out var minutes)) continue;
            var day = match.Groups["weekday"].Success
                ? $"T{match.Groups["weekday"].Value}"
                : "CN";
            result[day] = minutes;
        }
        return result;
    }

    private static bool TryReadMinutes(Match match, out int minutes)
    {
        minutes = 0;
        if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
            return false;
        var minute = 0;
        if (match.Groups["minute"].Success)
            int.TryParse(match.Groups["minute"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        if (hour is >= 1 and <= 11) hour += 12;
        minutes = hour * 60 + minute;
        return true;
    }

    private static string? ReadLocation(string original)
    {
        var match = LocationRegex.Match(original ?? string.Empty);
        if (!match.Success) return null;
        var value = match.Groups["location"].Value.Trim();
        if (value.Length == 0) return null;
        return value.Length <= 120 ? value : value[..120];
    }

    private static string BuildTwoItemClarification(IReadOnlyList<ZaloAutoSessionConversationDraftItem> items)
    {
        if (items.Count <= 2) return "Bạn muốn lấy cả hai lịch đúng không?";
        var combinations = new List<string>();
        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
            combinations.Add($"{items[i].DayKey} + {items[j].DayKey}");
        return $"Bạn muốn 2 lịch nào: {string.Join(", ", combinations.Take(4))}?";
    }

    private static string FormatMinutes(int minutes) =>
        $"{Math.Clamp(minutes, 0, 1439) / 60:00}:{Math.Clamp(minutes, 0, 1439) % 60:00}";

    private static string? NormalizeDay(string? value)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(value).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return Regex.IsMatch(normalized, @"^(T[2-7]|CN)$", RegexOptions.CultureInvariant) ? normalized : null;
    }

    private static bool TryParseClock(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!TimeOnly.TryParseExact(value.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        minutes = time.Hour * 60 + time.Minute;
        return true;
    }

    private static ZaloAutoSessionConversationIntent ParseIntent(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "modify" => ZaloAutoSessionConversationIntent.ModifyDraft,
            "confirm" => ZaloAutoSessionConversationIntent.Confirm,
            "cancel" => ZaloAutoSessionConversationIntent.Cancel,
            "reset" => ZaloAutoSessionConversationIntent.Reset,
            "uncertain" => ZaloAutoSessionConversationIntent.Uncertain,
            _ => ZaloAutoSessionConversationIntent.None
        };

    private static ZaloAutoSessionSelectionMode ParseSelectionMode(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "replace" => ZaloAutoSessionSelectionMode.Replace,
            "add" => ZaloAutoSessionSelectionMode.Add,
            "remove" => ZaloAutoSessionSelectionMode.Remove,
            _ => ZaloAutoSessionSelectionMode.None
        };

    private static string? ReadContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString();
        return null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewLine = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? text[(firstNewLine + 1)..lastFence].Trim()
            : text;
    }

    private static ZaloAutoSessionConversationInterpretation Result(
        ZaloAutoSessionConversationIntent intent,
        IReadOnlyList<string>? days = null,
        ZaloAutoSessionSelectionMode selectionMode = ZaloAutoSessionSelectionMode.None,
        IReadOnlyDictionary<string, int>? timeOverrides = null,
        string? location = null,
        int? teamSize = null,
        bool explicitExecute = false,
        bool needsClarification = false,
        string? clarification = null,
        string? questionType = null,
        double confidence = 1)
        => new(
            intent,
            days ?? [],
            selectionMode,
            timeOverrides ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            location,
            teamSize,
            explicitExecute,
            needsClarification,
            clarification,
            questionType,
            confidence,
            "rules");
}
