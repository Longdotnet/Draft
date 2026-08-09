namespace VolleyDraft.Api.Services;

internal static class ZaloOverbookMessageCatalog
{
    internal const string LightStage = "light";
    internal const string CalloutStage = "callout";
    internal const string SarcasticStage = "sarcastic";
    internal const string StubbornStage = "stubborn";

    internal const int LightStorageKey = 1001;
    internal const int CalloutStorageKey = 1002;
    internal const int SarcasticStorageKey = 1003;
    internal const int StubbornStorageKey = 1004;

    private static readonly string[] LightFrames =
    [
        "Ê {names}, đủ {capacity} slot rồi nha 😭 {stage}",
        "{names} ơi, tàu đủ ghế rồi mà bro còn leo lên 😭 {stage}",
        "Alo {names}, hiện tại {effectiveSlotCount}/{capacity} rồi nha =)) {stage}",
        "{names} check lại vote nha, full slot mất tiêu rồi 🥲 {stage}",
        "Ủa {names} 😭 thấy {capacity}/{capacity} mà vẫn bấm vô được hay vậy. {stage}",
        "{names} ơi thương anh em thì nhìn số slot trước khi click nha =)) đang dư {excessCount} người rồi. {stage}",
        "Bro {names}, slot thứ {firstExcessSlot} đang nằm ngoài capacity rồi đó 😭 {stage}",
        "{names} ơi poll này đủ người rồi nha, bot réo nhẹ một tiếng thôi =)) {stage}",
        "Alo alo {names}, kèo {sessionName} hiện {effectiveSlotCount}/{capacity} rồi nè 🥲 {stage}",
        "{names} ơi cứu BTC một pha nha 😭 slot đang full mất rồi. {stage}"
    ];

    private static readonly string[] LightStages =
    [
        "Vote dư rồi, nhường kèo cho anh em cái.",
        "Gỡ vote giúp cái nha bro.",
        "Bro đang đứng ngoài cửa đó, gỡ vote giúp nha.",
        "Đừng làm BTC khó xử, bỏ vote dư giúp cái.",
        "Lần đầu/lần hai bot nhắc nhẹ nhàng thôi nha."
    ];

    private static readonly string[] CalloutFrames =
    [
        "{names} bro ơi bot nhắc tới lần {reminderNumber} rồi đó 😭 {stage}",
        "Alo {names}, lần {reminderNumber} rồi nha =)) {effectiveSlotCount}/{capacity} vẫn chưa đổi. {stage}",
        "{names} ơi bot quay lại réo tên rồi nè 😭 {stage}",
        "Bro {names}, full slot vẫn đang full nha, bot nhắc lần {reminderNumber} rồi. {stage}",
        "{names} check poll lại giúp, hiện vẫn dư {excessCount} slot 🥲 {stage}",
        "Alo {names}, tàu đóng cửa rồi mà bro vẫn còn tên trên vé =)) {stage}",
        "{names}, BTC đang chờ đúng cái click bỏ vote của bro đó 😭 {stage}",
        "Bro {names}, slot thứ {firstExcessSlot} không phải slot VIP đâu nha =)) {stage}",
        "{names} ơi kèo {sessionName} chưa chốt được vì vẫn {effectiveSlotCount}/{capacity}. {stage}",
        "Bot xin phép réo {names} thêm lần {reminderNumber} 😭 {stage}"
    ];

    private static readonly string[] CalloutStages =
    [
        "Gỡ vote dùm để anh em còn chốt draft nha.",
        "Cứu bot một pha, bỏ vote dư giúp cái.",
        "Đừng giả bộ chưa thấy thông báo nha bro =))",
        "Vote dư không tự biến mất đâu, xử lý giúp nha.",
        "Nhẹ nhàng mấy lần rồi, giờ réo tên rõ hơn xíu nha 😭"
    ];

    private static readonly string[] SarcasticFrames =
    [
        "{names} đọc số được không trời 😭 {effectiveSlotCount}/{capacity} mà vẫn cố chen. {stage}",
        "Alo {names}, vote dư không làm sân mọc thêm đâu nha =)) {stage}",
        "{names} ơi đây là poll đăng ký chứ không phải game xếp hình 😭 hết chỗ là hết chỗ. {stage}",
        "Bot đã nhẹ nhàng mấy lần rồi nha {names} =)) {stage}",
        "{names}, slot thứ {firstExcessSlot} không phải slot VIP đâu 😭 {stage}",
        "Ủa {names}, bro định dùng sức mạnh niềm tin biến {capacity} slot thành {effectiveSlotCount} slot hả =)) {stage}",
        "{names} ơi lần {reminderNumber} rồi 😭 cái nút bỏ vote nó không thu phí đâu bro. {stage}",
        "{names} full slot từ đời nào rồi mà bro vẫn lì như bug production vậy =)) {stage}",
        "Thông báo khẩn: {names} vẫn đang cố chứng minh {effectiveSlotCount} ≤ {capacity} 😭 toán học đang khóc. {stage}",
        "{names} mắt thấy {capacity}/{capacity}, tay vẫn vote. Một pha xử lý đi vào lòng đất =)) {stage}",
        "Bro {names} định đứng slot dư tới lúc draft luôn hả 😭 draft bằng niềm tin à? {stage}",
        "{names}, cả hệ thống đang chạy ổn cho tới khi bro phát minh slot thứ {effectiveSlotCount} =)) {stage}",
        "{names} ơi bot quay lại lần {reminderNumber}, cảm giác như cron job gặp record không chịu expire 😭 {stage}",
        "Alo {names}, capacity là {capacity} chứ không phải con số để tham khảo nha =)) {stage}",
        "{names}, bro đang biến cảnh báo vượt slot thành series nhiều tập rồi đó 😭 {stage}",
        "Bot ping {names} lần {reminderNumber}: slot vẫn full, niềm tin vẫn mạnh =)) {stage}",
        "{names} ơi, server không scale thêm sân bóng chỉ vì bro vote dư đâu 😭 {stage}",
        "Bro {names}, {excessCount} slot dư vẫn đang chờ được giải phóng nè =)) {stage}",
        "{names}, nút bỏ vote vẫn online 24/7 nha bro 😭 {stage}",
        "Alo {names}, bot bắt đầu thuộc tên bro vì cái slot dư này rồi đó =)) {stage}"
    ];

    private static readonly string[] SarcasticStages =
    [
        "Gỡ lẹ giúp trước khi cả group thuộc luôn tên bro.",
        "Độ lì đang lên rank hơi nhanh đó nha =))",
        "Bot bắt đầu bất lực thiệt rồi, cứu nhau một pha 😭",
        "Đừng biến slot dư thành feature lâu dài nha bro.",
        "Xử lý cái vote trước khi nó thành legacy issue luôn nha =))"
    ];

    private static readonly string[] StubbornFrames =
    [
        "{names} ơi bot nhắc tới lần {reminderNumber} rồi mà slot dư vẫn sống khoẻ 😭 {stage}",
        "Bro {names}, tới lần {reminderNumber} rồi, cái vote này có hợp đồng thuê nhà hả =)) {stage}",
        "{names}, hội đồng tai trâu đang gọi tên bro rồi 😭 {stage}",
        "Alo {names}, bot bắt đầu nghi nút bỏ vote bị tàng hình trên máy bro =)) {stage}",
        "{names} ơi đây không còn là quên nữa, đây là một hành trình 😭 {stage}",
        "Bro {names}, huyền thoại lì slot vẫn chưa chịu kết thúc =)) {stage}",
        "{names}, reminder #{reminderNumber}: production vẫn chạy, riêng vote dư của bro vẫn bất tử 😭 {stage}",
        "Thông báo định kỳ: {names} vẫn đang camp slot thứ {firstExcessSlot} =)) {stage}",
        "{names} ơi bot đã ping tới lần {reminderNumber}, bro đang speedrun kỷ lục lì slot hả 😭 {stage}",
        "Alo {names}, {effectiveSlotCount}/{capacity} vẫn y nguyên. Đây là sự kiên định hơi sai chỗ nha =)) {stage}",
        "Bro {names}, slot dư không có lương mà sao bro làm full-time ở đó vậy 😭 {stage}",
        "{names}, cả group sắp thuộc lòng reminderNumber của bro rồi đó =)) {stage}",
        "Bot report: {names} vẫn chưa chịu release slot dư sau {reminderNumber} lần nhắc 😭 {stage}",
        "{names} ơi bug này không tự close đâu, owner của vote vào xử lý giúp =)) {stage}",
        "Bro {names}, capacity {capacity} đang xin bro tôn trọng nó một lần 😭 {stage}",
        "{names}, nếu độ lì convert thành slot thì giờ đủ mở thêm một sân rồi =)) {stage}",
        "Alo {names}, bot không muốn gặp bro mỗi {reminderNumber} lần chỉ vì một cái vote đâu 😭 {stage}",
        "{names} ơi slot thứ {firstExcessSlot} đã thành landmark vì bro đứng lâu quá rồi =)) {stage}",
        "Bro {names}, đây là lần {reminderNumber}, bot xin phép trao cúp bền bỉ rồi xin bro gỡ vote 😭 {stage}",
        "{names}, câu chuyện {effectiveSlotCount}/{capacity} kéo dài hơn cả dự kiến rồi nha =)) {stage}"
    ];

    private static readonly string[] StubbornStages =
    [
        "Gỡ vote giúp để series này có tập cuối nha bro.",
        "Tai trâu vừa thôi, nhường slot cho đúng người đăng ký nha 😭",
        "Bot xin thua độ lì, nhưng slot vẫn phải về đúng capacity =))",
        "Đừng để reminder này thành truyền thống của group nha bro.",
        "Chốt hạ giùm: bỏ vote dư để anh em còn draft."
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultStageBanks =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [LightStage] = BuildBank(LightFrames, LightStages, 50),
            [CalloutStage] = BuildBank(CalloutFrames, CalloutStages, 50),
            [SarcasticStage] = BuildBank(SarcasticFrames, SarcasticStages, 100),
            [StubbornStage] = BuildBank(StubbornFrames, StubbornStages, 100)
        };

    internal static string GetStageName(int reminderNumber) => reminderNumber switch
    {
        <= 2 => LightStage,
        <= 5 => CalloutStage,
        <= 15 => SarcasticStage,
        _ => StubbornStage
    };

    internal static IReadOnlyList<string> GetBank(int reminderNumber) =>
        GetDefaultStageBank(GetStageName(reminderNumber));

    internal static IReadOnlyList<string> GetDefaultStageBank(string stage) =>
        DefaultStageBanks.TryGetValue(stage, out var bank) ? bank : DefaultStageBanks[LightStage];

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> GetDefaultStageBanks() =>
        DefaultStageBanks.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> GetUiStageBanks(
        IReadOnlyDictionary<int, List<string>> overrides)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in new[] { LightStage, CalloutStage, SarcasticStage, StubbornStage })
        {
            var storageKey = GetStageStorageKey(stage);
            result[stage] = overrides.TryGetValue(storageKey, out var custom) && custom.Count > 0
                ? custom
                : GetDefaultStageBank(stage);
        }
        return result;
    }

    internal static Dictionary<int, IReadOnlyList<string>> GetUiBanks(IReadOnlyDictionary<int, List<string>> overrides) =>
        overrides
            .Where(pair => pair.Key is >= 1 and <= 100 && pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);

    internal static bool TryGetCustomStageBank(
        IReadOnlyDictionary<int, List<string>> overrides,
        string stage,
        out IReadOnlyList<string> bank)
    {
        var storageKey = GetStageStorageKey(stage);
        if (overrides.TryGetValue(storageKey, out var custom) && custom.Count > 0)
        {
            bank = custom;
            return true;
        }
        bank = [];
        return false;
    }

    internal static int GetStageStorageKey(string stage) => stage.ToLowerInvariant() switch
    {
        LightStage => LightStorageKey,
        CalloutStage => CalloutStorageKey,
        SarcasticStage => SarcasticStorageKey,
        StubbornStage => StubbornStorageKey,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown overbook message stage.")
    };

    internal static bool TryGetStageStorageKey(string stage, out int key)
    {
        try
        {
            key = GetStageStorageKey(stage);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            key = 0;
            return false;
        }
    }

    private static IReadOnlyList<string> BuildBank(
        IReadOnlyList<string> frames,
        IReadOnlyList<string> stages,
        int count)
    {
        var result = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var frame = frames[index % frames.Count];
            var stage = stages[(index / frames.Count) % stages.Count];
            result.Add(frame.Replace("{stage}", stage, StringComparison.Ordinal));
        }
        return result;
    }
}
