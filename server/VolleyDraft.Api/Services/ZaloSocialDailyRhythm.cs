namespace VolleyDraft.Api.Services;

internal enum ZaloDailyGreetingKind
{
    Morning,
    Night
}

internal enum ZaloDailyGreetingMood
{
    Warm,
    PlayfulRomantic,
    MenlySupportive
}

internal sealed record ZaloDailySocialSettings(
    bool Enabled,
    bool MorningGreetingEnabled,
    bool NightGreetingEnabled,
    int GreetingDaysPerWeek,
    int GreetingRepeatDays,
    bool GreetingImagesEnabled)
{
    public static ZaloDailySocialSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:Enabled", true),
        MorningGreetingEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:MorningGreetingEnabled", true),
        NightGreetingEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:NightGreetingEnabled", true),
        GreetingDaysPerWeek: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingDaysPerWeek", 5), 1, 7),
        GreetingRepeatDays: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingRepeatDays", 14), 3, 30),
        GreetingImagesEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingImagesEnabled", true));
}

internal sealed record ZaloDailyGreetingSnapshot(
    string GroupId,
    DateTimeOffset Now,
    DateTimeOffset? LastBotMessageAt,
    int RecentTwoMinuteMessageCount,
    IReadOnlyList<ZaloSocialHistoryMessage> BotHistory);

internal sealed record ZaloDailyGreetingPlan(
    ZaloDailyGreetingKind Kind,
    ZaloDailyGreetingMood Mood,
    string Message,
    bool UseImage,
    DateOnly ServiceDate)
{
    public bool RequiresImage => Kind == ZaloDailyGreetingKind.Morning;
}

internal static class ZaloDailyGreetingEngine
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private const int MorningWindowStart = 6 * 60 + 45;  // 06:45
    private const int MorningWindowEnd = 8 * 60 + 45;    // 08:45
    private const int NightWindowStart = 22 * 60 + 30;   // 22:30
    private const int NightWindowEndUnwrapped = 24 * 60 + 20; // 00:20 next day

    public static ZaloDailyGreetingPlan? Plan(
        ZaloDailyGreetingSnapshot snapshot,
        ZaloDailySocialSettings settings,
        int minBotIntervalMinutes)
    {
        if (!settings.Enabled) return null;
        var localNow = snapshot.Now.ToOffset(VietnamOffset);
        var kind = CurrentGreetingWindow(localNow);
        if (kind is null) return null;
        if (kind == ZaloDailyGreetingKind.Morning && !settings.MorningGreetingEnabled) return null;
        if (kind == ZaloDailyGreetingKind.Night && !settings.NightGreetingEnabled) return null;
        if (snapshot.RecentTwoMinuteMessageCount >= 6) return null;
        if (snapshot.LastBotMessageAt is { } lastBot &&
            snapshot.Now - lastBot < TimeSpan.FromMinutes(Math.Clamp(minBotIntervalMinutes, 15, 720)))
            return null;

        var serviceDate = ServiceDate(localNow, kind.Value);
        var selector = Positive(StableSelector(snapshot.GroupId, serviceDate, kind.Value));
        if (selector % 7 >= settings.GreetingDaysPerWeek) return null;
        if (!HasReachedStableSendMinute(localNow, kind.Value, selector)) return null;
        if (AlreadySent(snapshot.BotHistory, kind.Value, serviceDate)) return null;

        var mood = SelectMood(selector);
        var message = ZaloDailyGreetingPhraseCatalog.Pick(
            kind.Value,
            mood,
            selector,
            snapshot.Now,
            snapshot.BotHistory,
            settings.GreetingRepeatDays);
        if (string.IsNullOrWhiteSpace(message)) return null;

        // Morning greetings are card-first: when greeting media is enabled every
        // eligible morning plan requests a card. Night cards stay occasional so
        // bedtime messages do not feel like scheduled posters.
        var useImage = settings.GreetingImagesEnabled &&
                       (kind == ZaloDailyGreetingKind.Morning || ((selector / 11) % 4 == 0));
        return new(kind.Value, mood, message, useImage, serviceDate);
    }

    public static bool IsHardQuiet(DateTimeOffset now)
    {
        var local = now.ToOffset(VietnamOffset);
        return local.Hour >= 1 && local.Hour < 6;
    }

    public static bool IsSoftGreetingZone(DateTimeOffset now)
    {
        var local = now.ToOffset(VietnamOffset);
        var minute = local.Hour * 60 + local.Minute;
        return (minute >= 6 * 60 && minute < 9 * 60 + 30) ||
               minute >= NightWindowStart ||
               minute < 60;
    }

    internal static ZaloDailyGreetingMood SelectMood(int selector)
    {
        var bucket = Positive(selector) % 100;
        return bucket < 60
            ? ZaloDailyGreetingMood.Warm
            : bucket < 85
                ? ZaloDailyGreetingMood.PlayfulRomantic
                : ZaloDailyGreetingMood.MenlySupportive;
    }

    private static ZaloDailyGreetingKind? CurrentGreetingWindow(DateTimeOffset localNow)
    {
        var minute = localNow.Hour * 60 + localNow.Minute;
        if (minute >= MorningWindowStart && minute <= MorningWindowEnd)
            return ZaloDailyGreetingKind.Morning;
        var unwrapped = localNow.Hour < 1 ? minute + 24 * 60 : minute;
        return unwrapped >= NightWindowStart && unwrapped <= NightWindowEndUnwrapped
            ? ZaloDailyGreetingKind.Night
            : null;
    }

    private static DateOnly ServiceDate(DateTimeOffset localNow, ZaloDailyGreetingKind kind)
    {
        var date = DateOnly.FromDateTime(localNow.Date);
        return kind == ZaloDailyGreetingKind.Night && localNow.Hour < 1
            ? date.AddDays(-1)
            : date;
    }

    private static bool HasReachedStableSendMinute(
        DateTimeOffset localNow,
        ZaloDailyGreetingKind kind,
        int selector)
    {
        var minute = localNow.Hour * 60 + localNow.Minute;
        if (kind == ZaloDailyGreetingKind.Morning)
        {
            // Stable per group/day but leaves enough room for worker delays.
            var target = MorningWindowStart + (selector / 7) % 106; // 06:45..08:30
            return minute >= target;
        }

        var unwrapped = localNow.Hour < 1 ? minute + 24 * 60 : minute;
        var nightTarget = NightWindowStart + (selector / 7) % 100; // 22:30..00:09
        return unwrapped >= nightTarget;
    }

    private static bool AlreadySent(
        IEnumerable<ZaloSocialHistoryMessage> history,
        ZaloDailyGreetingKind kind,
        DateOnly serviceDate)
    {
        foreach (var row in history)
        {
            if (!ZaloDailyGreetingPhraseCatalog.IsKind(row.Content, kind)) continue;
            var local = row.SentAt.ToOffset(VietnamOffset);
            var rowDate = DateOnly.FromDateTime(local.Date);
            if (kind == ZaloDailyGreetingKind.Night && local.Hour < 1)
                rowDate = rowDate.AddDays(-1);
            if (rowDate == serviceDate) return true;
        }
        return false;
    }

    private static int StableSelector(string groupId, DateOnly date, ZaloDailyGreetingKind kind)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in $"{groupId}:{date:yyyyMMdd}:{kind}")
                hash = hash * 31 + character;
            return hash;
        }
    }

    private static int Positive(int value) => value & int.MaxValue;
}

internal static class ZaloDailyGreetingPhraseCatalog
{
    private static readonly IReadOnlyDictionary<(ZaloDailyGreetingKind Kind, ZaloDailyGreetingMood Mood), string[]> Pools =
        new Dictionary<(ZaloDailyGreetingKind, ZaloDailyGreetingMood), string[]>
        {
            [(ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.Warm)] =
            [
                "Morning cả nhà ☀️ chúc hôm nay ai cũng nhẹ đầu, nhiều năng lượng và gặp toàn chuyện dễ thương nha.",
                "Sáng rồi nha mọi người ☀️ ăn sáng đàng hoàng, uống nước đầy đủ rồi mình chiến ngày mới thôi.",
                "Good morning cả nhà. Hôm nay cứ sống vui trước đã, chuyện còn lại từ từ tính 😌",
                "Chào ngày mới nha mọi người ☀️ mong hôm nay công việc trơn tru, đầu óc nhẹ tênh và có nhiều chuyện vui.",
                "Sáng tốt lành nha cả nhà. Dậy nạp năng lượng tử tế rồi mình đi kiếm một ngày đáng vui thôi 😌",
                "Morninggg ☀️ hôm nay mong cả group gặp đúng mood, đúng việc và toàn người dễ thương.",
                "Ngày mới tới rồi nha. Ăn ngon, làm việc ổn, cười nhiều một chút là quá đẹp rồi ☀️"
            ],
            [(ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.PlayfulRomantic)] =
            [
                "Morning mấy bạn trẻ ☀️ cứ đẹp trai xinh gái và vui vẻ trước, biết đâu duyên tự tìm tới 😌",
                "Sáng tốt lành nha cả nhà. Người thương chưa có thì từ từ, hôm nay cứ thương mình trước đã.",
                "Chúc cả group sáng nay đúng mood, đúng việc, đúng người. Chưa đúng người thì đúng món ăn cũng được 😌",
                "Good morning ☀️ ai có người thương thì giữ cho kỹ, ai chưa có thì cứ sống xịn trước đã, duyên tính sau.",
                "Sáng nha mọi người. Chúc hôm nay có tin vui, có tiền vào và nếu may thì có cả người làm mình cười 😌"
            ],
            [(ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.MenlySupportive)] =
            [
                "Sáng tốt lành nha cả nhà. Hôm nay có chuyện gì thì xử từng chuyện một, đừng tự làm khó mình. Chiến thôi 🤝",
                "Ngày mới rồi. Ăn uống tử tế, làm việc gọn gàng, tối về còn sức chơi nha mọi người.",
                "Morning cả nhà. Cứ bình tĩnh làm tốt phần của mình, chuyện khó tới đâu mình xử tới đó 🤝",
                "Sáng nha mọi người ☀️ giữ sức, giữ mood, làm việc cho gọn. Hôm nay mình vẫn cân được."
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.Warm)] =
            [
                "Khuya rồi nha mọi người 🌙 hôm nay vui hay mệt gì cũng để lại ở hôm nay thôi. Ngủ ngoan nha.",
                "Một ngày đủ rồi. Mai tỉnh dậy mình tính tiếp, tối nay cứ nghỉ cho tử tế trước đã. Ngủ ngoan mọi người 🌙",
                "Good night cả nhà 🌙 cất điện thoại xuống một chút, cho đầu óc nghỉ ngơi rồi ngủ thật ngon nha.",
                "Tối rồi nha mọi người. Chuyện chưa xong mai làm tiếp, giờ cho bản thân một giấc ngủ tử tế trước đã 🌙",
                "Ngủ ngoan nha cả nhà 🌙 mong mọi người khép ngày hôm nay nhẹ nhàng và mai thức dậy với mood thật đẹp.",
                "Hết một ngày rồi nha. Vui thì giữ lại, mệt thì bỏ xuống, ngủ một giấc ngon rồi mai mình tiếp tục 🌙",
                "Good night mọi người. Tối nay cứ yên tâm nghỉ, ngày mai còn nguyên một ngày mới để mình làm tốt hơn 😌"
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.PlayfulRomantic)] =
            [
                "Good night cả nhà 🌙 người có người thương thì ngủ ngon cùng người thương, người chưa có cũng phải ngủ ngon nha 😌",
                "Khuya rồi nha. Chuyện chưa vui để mai xử, người chưa thương thì từ từ kiếm 😌 tối nay ngủ ngoan trước đã.",
                "Ngủ ngon nha mọi người 🌙 ai đang single thì cứ ngủ thật ngon, biết đâu mai thức dậy có người nhắn trước 😌",
                "Tối rồi, người thương có thể tới chậm chứ giấc ngủ thì đừng cho tới trễ nha 😌 ngủ ngoan cả nhà.",
                "Good night mấy bạn trẻ 🌙 có đôi thì ấm áp, chưa có đôi thì vẫn phải thương mình cho đàng hoàng nha."
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.MenlySupportive)] =
            [
                "Nghỉ thôi mọi người. Mai còn việc mai xử, giờ ngủ cho đủ sức trước đã. Ngủ ngon nha.",
                "Một ngày chiến vậy đủ rồi. Tắt máy, nghỉ đầu, ngủ cho khỏe. Mai mình làm tiếp 🤝",
                "Khuya rồi nha. Việc khó để sáng mai đầu tỉnh xử sẽ ngon hơn, giờ nghỉ cho tử tế trước đã.",
                "Good night cả nhà. Hôm nay làm được tới đâu cũng được, ngủ đủ rồi mai mình chiến tiếp 🤝"
            ]
        };

    private static readonly IReadOnlyDictionary<ZaloDailyGreetingKind, HashSet<string>> KindLookup =
        Enum.GetValues<ZaloDailyGreetingKind>().ToDictionary(
            kind => kind,
            kind => Pools
                .Where(item => item.Key.Kind == kind)
                .SelectMany(item => item.Value)
                .ToHashSet(StringComparer.Ordinal));

    public static string? Pick(
        ZaloDailyGreetingKind kind,
        ZaloDailyGreetingMood mood,
        int selector,
        DateTimeOffset now,
        IReadOnlyList<ZaloSocialHistoryMessage> history,
        int repeatDays)
    {
        var pool = Pools[(kind, mood)];
        var cutoff = now - TimeSpan.FromDays(Math.Clamp(repeatDays, 3, 30));
        var recentExact = history
            .Where(item => item.SentAt >= cutoff)
            .Select(item => item.Content)
            .ToHashSet(StringComparer.Ordinal);
        var recentOpenings = history
            .Where(item => item.SentAt >= now - TimeSpan.FromDays(2))
            .Select(item => OpeningKey(item.Content))
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = pool
            .Where(item => !recentExact.Contains(item))
            .Where(item => !recentOpenings.Contains(OpeningKey(item)))
            .ToArray();
        if (candidates.Length == 0)
            candidates = pool.Where(item => !recentExact.Contains(item)).ToArray();
        if (candidates.Length == 0)
            candidates = pool;
        return candidates.Length == 0 ? null : candidates[Positive(selector / 101) % candidates.Length];
    }

    public static bool IsKind(string? message, ZaloDailyGreetingKind kind) =>
        !string.IsNullOrWhiteSpace(message) && KindLookup[kind].Contains(message.Trim());

    internal static IReadOnlyList<string> All(ZaloDailyGreetingKind kind) => KindLookup[kind].ToArray();

    private static string OpeningKey(string? value)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3));
    }

    private static int Positive(int value) => value & int.MaxValue;
}