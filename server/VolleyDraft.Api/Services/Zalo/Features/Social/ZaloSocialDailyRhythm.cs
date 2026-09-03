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
    MenlySupportive,
    TenderRomantic,
    LonelyComfort,
    CozyGroupLove,
    LightPlayfulSweet
}

internal sealed record ZaloDailySocialSettings(
    bool Enabled,
    bool MorningGreetingEnabled,
    bool NightGreetingEnabled,
    int GreetingDaysPerWeek,
    int GreetingRepeatDays,
    bool GreetingImagesEnabled)
{
    // Zero keeps old manually-created settings backwards compatible: callers that do
    // not know about the per-kind knobs inherit GreetingDaysPerWeek.
    public int MorningGreetingDaysPerWeek { get; init; }
    public int NightGreetingDaysPerWeek { get; init; }
    public bool NightGreetingCardFirst { get; init; } = true;

    public int DaysPerWeek(ZaloDailyGreetingKind kind)
    {
        var configured = kind == ZaloDailyGreetingKind.Morning
            ? MorningGreetingDaysPerWeek
            : NightGreetingDaysPerWeek;
        return configured is >= 1 and <= 7
            ? configured
            : Math.Clamp(GreetingDaysPerWeek, 1, 7);
    }

    public static ZaloDailySocialSettings FromConfiguration(IConfiguration configuration)
    {
        var sharedDays = Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingDaysPerWeek", 5),
            1,
            7);
        return new(
            Enabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:Enabled", true),
            MorningGreetingEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:MorningGreetingEnabled", true),
            NightGreetingEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:NightGreetingEnabled", true),
            GreetingDaysPerWeek: sharedDays,
            GreetingRepeatDays: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingRepeatDays", 14), 3, 30),
            GreetingImagesEnabled: configuration.GetValue("ZaloBot:Ambient:DailyRhythm:GreetingImagesEnabled", true))
        {
            MorningGreetingDaysPerWeek = Math.Clamp(
                configuration.GetValue("ZaloBot:Ambient:DailyRhythm:MorningGreetingDaysPerWeek", sharedDays),
                1,
                7),
            NightGreetingDaysPerWeek = Math.Clamp(
                configuration.GetValue("ZaloBot:Ambient:DailyRhythm:NightGreetingDaysPerWeek", sharedDays),
                1,
                7),
            NightGreetingCardFirst = configuration.GetValue(
                "ZaloBot:Ambient:DailyRhythm:NightGreetingCardFirst",
                true)
        };
    }
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
    DateOnly ServiceDate,
    bool CardRequired = false)
{
    public bool RequiresImage => Kind == ZaloDailyGreetingKind.Morning || CardRequired;
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
        if (selector % 7 >= settings.DaysPerWeek(kind.Value)) return null;
        if (!HasReachedStableSendMinute(localNow, kind.Value, selector)) return null;
        if (AlreadySent(snapshot.BotHistory, kind.Value, serviceDate)) return null;

        var mood = kind == ZaloDailyGreetingKind.Night
            ? SelectNightMood(selector)
            : SelectMood(selector);
        var message = ZaloDailyGreetingPhraseCatalog.Pick(
            kind.Value,
            mood,
            selector,
            snapshot.Now,
            snapshot.BotHistory,
            settings.GreetingRepeatDays);
        if (string.IsNullOrWhiteSpace(message)) return null;

        // Morning remains card-first. Night is card-first by default, with an explicit
        // compatibility switch that can restore the old occasional-card behavior.
        var useImage = settings.GreetingImagesEnabled &&
                       (kind == ZaloDailyGreetingKind.Morning ||
                        (kind == ZaloDailyGreetingKind.Night && settings.NightGreetingCardFirst) ||
                        ((selector / 11) % 4 == 0));
        var cardRequired = kind == ZaloDailyGreetingKind.Night && settings.NightGreetingCardFirst;
        return new(kind.Value, mood, message, useImage, serviceDate, cardRequired);
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

    // Morning distribution stays exactly as before to avoid changing an already-live feature.
    internal static ZaloDailyGreetingMood SelectMood(int selector)
    {
        var bucket = Positive(selector) % 100;
        return bucket < 60
            ? ZaloDailyGreetingMood.Warm
            : bucket < 85
                ? ZaloDailyGreetingMood.PlayfulRomantic
                : ZaloDailyGreetingMood.MenlySupportive;
    }

    internal static ZaloDailyGreetingMood SelectNightMood(int selector)
    {
        var bucket = Positive(selector) % 100;
        return bucket < 40
            ? ZaloDailyGreetingMood.TenderRomantic
            : bucket < 70
                ? ZaloDailyGreetingMood.LonelyComfort
                : bucket < 90
                    ? ZaloDailyGreetingMood.CozyGroupLove
                    : ZaloDailyGreetingMood.LightPlayfulSweet;
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

            // Legacy night moods are kept callable for compatibility, but live Night planning
            // now uses the four inclusive romantic moods below.
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.Warm)] =
            [
                "Khuya rồi nha mọi người 🌙 hôm nay vui hay mệt gì cũng để lại ở hôm nay thôi. Ngủ ngoan nha.",
                "Một ngày đủ rồi. Mai tỉnh dậy mình tính tiếp, tối nay cứ nghỉ cho tử tế trước đã. Ngủ ngoan mọi người 🌙",
                "Good night cả nhà 🌙 cất điện thoại xuống một chút, cho đầu óc nghỉ ngơi rồi ngủ thật ngon nha.",
                "Tối rồi nha mọi người. Chuyện chưa xong mai làm tiếp, giờ cho bản thân một giấc ngủ tử tế trước đã 🌙"
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.PlayfulRomantic)] =
            [
                "Khuya rồi nha, trái tim nào còn lang thang thì cũng nên đi ngủ thôi nè 🌙",
                "Tối rồi, điều dễ thương có thể tới chậm chứ giấc ngủ thì đừng cho tới trễ nha 😌",
                "Ngủ ngon nha mọi người 🌙 biết đâu mai thức dậy lại có một chuyện nhỏ làm mình cười."
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.MenlySupportive)] =
            [
                "Nghỉ thôi mọi người. Mai còn việc mai xử, giờ ngủ cho đủ sức trước đã. Ngủ ngon nha.",
                "Một ngày chiến vậy đủ rồi. Tắt máy, nghỉ đầu, ngủ cho khỏe. Mai mình làm tiếp 🤝",
                "Khuya rồi nha. Việc khó để sáng mai đầu tỉnh xử sẽ ngon hơn, giờ nghỉ cho tử tế trước đã."
            ],

            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.TenderRomantic)] =
            [
                "Đêm xuống rồi 🌙 mong ai trong group mình cũng có một giấc ngủ thật yên, nhẹ như một cái ôm vừa đủ.",
                "Ngủ ngoan nha mọi người. Mong đêm nay dịu dàng với mỗi người hơn một chút 🤍",
                "Khép ngày hôm nay lại thôi 🌙 chúc bạn ngủ thật ngon và thức dậy với một trái tim nhẹ hơn.",
                "Khuya rồi đó. Những điều chưa kịp vui cứ để mai, tối nay mình xứng đáng được nghỉ trong bình yên nha.",
                "Chúc cả nhà một đêm thật êm ✨ mong giấc ngủ mang đi bớt mệt và để lại một chút ấm áp.",
                "Nếu hôm nay dài quá thì thôi, mình dừng ở đây nha. Ngủ ngoan và để đêm nay dỗ dành mình một chút 🌙",
                "Đêm nay mong mọi người ngủ trong cảm giác mình vẫn được thương, dù ngày hôm nay có ra sao 🤍",
                "Cất ngày hôm nay xuống nha. Chúc mỗi người một khoảng yên thật riêng, một giấc ngủ thật mềm 🌙",
                "Ngủ ngon nha. Mong trăng đêm nay dịu như cách mỗi người đều xứng đáng được đối xử dịu dàng.",
                "Một lời chúc nhỏ trước khi ngày khép lại: ngủ thật yên nha, mai mình lại có thêm một ngày để thương đời hơn chút."
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.LonelyComfort)] =
            [
                "Ai tối nay thấy lòng hơi trống thì nhận lời chúc này nha 🌙 mong bạn ngủ trong cảm giác mình không hề bị bỏ quên.",
                "Nếu hôm nay chưa ai hỏi bạn có ổn không, thì tối nay cứ nghỉ trước đã nha. Bạn đã cố gắng nhiều rồi 🤍",
                "Có những đêm chỉ cần một lời chúc nhỏ cũng đủ ấm lòng. Vậy nên ngủ ngoan nhé, người đang đọc dòng này.",
                "Nếu tối nay bạn đang một mình, mong lời chúc nhỏ này đủ làm tim ấm thêm một chút. Ngủ ngon nha 🌙",
                "Không sao nếu hôm nay chưa vui lắm. Chỉ cần đêm nay mình được ngủ yên cũng đã là một điều tử tế rồi.",
                "Ai còn ôm một chút buồn thì để nó ngồi ngoài cửa phòng nha. Tối nay mình ngủ trước, mai tính tiếp 🤍",
                "Mong người đang mệt được nghỉ, người đang buồn được nhẹ, người đang cô đơn thấy lòng ấm hơn một chút tối nay 🌙",
                "Nếu hôm nay có khoảnh khắc làm bạn thấy mình bé xíu, thì đêm nay cứ cho bản thân được nghỉ thật an toàn nha.",
                "Chuyện chưa ổn không cần phải ổn hết trong một đêm. Ngủ ngoan trước nha, sáng mai mình sẽ có thêm sức.",
                "Tối nay không cần phải mạnh mẽ nữa đâu. Cứ ngủ một giấc thật ngon, phần còn lại để ngày mai lo 🤍"
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.CozyGroupLove)] =
            [
                "Cả nhà ngủ ngon nha 🌙 mong ai đang mệt sẽ nhẹ hơn, ai đang cô đơn sẽ ấm hơn một chút.",
                "Khép ngày hôm nay lại thôi mọi người. Chúc mỗi người trong group mình có một đêm thật yên 🤍",
                "Good night cả nhà 🌙 hôm nay mình đã gặp nhau ở đây rồi, giờ ai về giấc ngủ nấy cho thật ngon nha.",
                "Khuya rồi, chúc cả group nghỉ thật tử tế. Mai có chuyện vui thì nhớ mang lên đây kể nhau nghe 😌",
                "Tới giờ sạc pin rồi nha mọi người ✨ chúc cả nhà ngủ sâu, đầu nhẹ và tim cũng nhẹ.",
                "Đêm nay chúc cả nhà ngủ thật ngon. Dù hôm nay ra sao, mong ai cũng được nghỉ trong cảm giác được yêu thương.",
                "Một ngày nữa của group mình khép lại rồi 🌙 ngủ ngoan nha, mai gặp nhau với mood dễ thương hơn.",
                "Ai còn thức thì coi đây là tín hiệu đi ngủ nha 😌 cả nhà nghỉ ngon, mai mình lại chuyện trò tiếp.",
                "Chúc mỗi người trong group một chiếc chăn ấm, một cái gối êm và một cái đầu chịu im đúng giờ 🌙",
                "Thôi mình trả ngày hôm nay về cho đêm nha. Ngủ ngon cả nhà, cảm ơn vì vẫn ở đây cùng nhau 🤍"
            ],
            [(ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.LightPlayfulSweet)] =
            [
                "Khuya rồi đó 🌙 trái tim nào còn lang thang thì về giường trước nha, chuyện dễ thương để mai tính 😌",
                "Ngủ sớm đi nha, thức thêm cũng chưa giàu ngay đâu 😌🌙 để mai tỉnh táo rồi mình kiếm tiếp.",
                "Ai còn chờ một tin nhắn thì cho nó thêm một đêm nha 😌 mình ngủ ngon trước đã.",
                "Bot gửi chút dịu dàng cuối ngày nè 🤍 nhận xong thì cất điện thoại và ngủ ngoan nha.",
                "Đừng scroll thêm nữa nha mấy người dễ thương 😌🌙 giấc ngủ đang đứng ngoài cửa chờ lâu rồi.",
                "Tối nay ai chưa được ai chúc ngủ ngon thì coi như group mình chúc rồi nha 🌙 ngủ ngoan.",
                "Khuya rồi, drama để mai, crush để mai, công việc cũng để mai 😌 giờ ưu tiên ngủ ngon trước nha.",
                "Chúc cả nhà mơ đẹp ✨ nếu có gặp điều dễ thương trong mơ thì sáng mai nhớ kể.",
                "Trái tim đi ngủ, cái đầu cũng đi ngủ, riêng báo thức mai tự lo nha 😌🌙",
                "Ngủ ngoan nha. Biết đâu sáng mai mở mắt ra, đời tự nhiên dễ thương hơn hôm nay một xíu ✨"
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
        if (!Pools.TryGetValue((kind, mood), out var pool))
            return null;

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
