namespace VolleyDraft.Api.Services;

internal static class ZaloOverbookMessageCatalog
{
    private static readonly string[] Frames =
    [
        "Ê {names}, đủ {capacity} slot rồi nha 😭 {stage} Gỡ vote giúp anh em cái.",
        "{names} ơi, tàu đủ ghế rồi mà bro còn leo lên 😭 {stage} Nhường kèo cho anh em nha.",
        "Alo {names}, hiện tại {effectiveSlotCount}/{capacity} rồi =)) {stage} Bro đang đứng ngoài cửa đó.",
        "{names} check lại vote nha, full slot mất tiêu rồi 🥲 {stage} Đừng làm BTC khó xử.",
        "Ủa {names} 😭 thấy {capacity}/{capacity} mà vẫn bấm vô được hay vậy. {stage} Gỡ vote hộ cái.",
        "{names} ơi thương anh em thì nhìn số slot trước khi click nha =)) đang dư {excessCount} người. {stage}",
        "{names} bro ơi bot nhắc tới lần {reminderNumber} rồi đó 😭 {stage} Full slot rồi, gỡ vote dùm.",
        "{names} đọc số được không trời 😭 {effectiveSlotCount}/{capacity} mà vẫn cố chen. {stage}",
        "Alo {names}, vote dư không làm sân mọc thêm đâu nha =)) {stage} Gỡ giùm cái.",
        "{names} ơi đây là poll đăng ký chứ không phải game xếp hình 😭 hết chỗ là hết chỗ. {stage}",
        "Bot đã réo rồi nha {names} =)) {stage} Gỡ vote trước khi cả group réo tên.",
        "{names}, slot thứ {firstExcessSlot} không phải slot VIP đâu 😭 {stage} Gỡ vote đi bro.",
        "Ủa {names}, bro định dùng sức mạnh niềm tin biến {capacity} slot thành {effectiveSlotCount} slot hả =)) {stage}",
        "{names} ơi lần {reminderNumber} rồi 😭 cái nút bỏ vote nó không thu phí đâu bro. {stage}",
        "{names} full slot từ đời nào rồi mà bro vẫn lì như bug production vậy =)) {stage} Gỡ vote.",
        "Thông báo khẩn: {names} vẫn đang cố chứng minh {effectiveSlotCount} ≤ {capacity} 😭 toán học đang khóc. {stage}",
        "{names} mắt thấy {capacity}/{capacity}, tay vẫn vote. Một pha xử lý đi vào lòng đất =)) {stage}",
        "{names} ơi bot nhắc tới mức này rồi mà chưa gỡ thì đúng là đam mê chen slot 😭 {stage}",
        "Bro {names} định đứng slot dư tới lúc draft luôn hả 😭 draft bằng niềm tin à? {stage} Gỡ vote giùm.",
        "{names}, cả hệ thống đang chạy ổn cho tới khi bro phát minh slot thứ {effectiveSlotCount} =)) {stage} Gỡ lẹ."
    ];

    internal static IReadOnlyList<string> GetBank(int reminderNumber)
    {
        var stage = reminderNumber switch
        {
            1 => "Lần đầu bot nhắc nhẹ nhàng thôi nha.",
            2 => "Lần 2 rồi nha bro, cứu bot một pha.",
            3 => "Lần 3 bắt đầu nghiêm túc rồi đó.",
            4 => "Lần 4 rồi, đừng giả bộ chưa thấy nha =))",
            5 => "Lần 5, độ lì đang tăng hơi nhanh đó bro.",
            6 => "Lần 6 rồi, bot bắt đầu bất lực thiệt nha 😭",
            7 => "Lần 7 rồi, bro đang unlock danh hiệu tai trâu đó =))",
            <= 10 => $"Lần {reminderNumber}, độ lì slot đang lên rank cao rồi đó.",
            <= 20 => $"Lần {reminderNumber}, hội đồng tai trâu đang gọi tên bro rồi 😭",
            <= 40 => $"Lần {reminderNumber}, bot bắt đầu nghi ngờ nút bỏ vote bị tàng hình =))",
            <= 70 => $"Lần {reminderNumber}, đây không còn là quên nữa, đây là một hành trình.",
            _ => $"Lần {reminderNumber}, huyền thoại lì slot vẫn chưa chịu kết thúc 😭"
        };
        return Frames.Select(frame => frame.Replace("{stage}", stage, StringComparison.Ordinal)).ToList();
    }

    internal static Dictionary<int, IReadOnlyList<string>> GetUiBanks(IReadOnlyDictionary<int, List<string>> overrides)
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        for (var i = 1; i <= 7; i++) result[i] = overrides.TryGetValue(i, out var custom) && custom.Count > 0 ? custom : GetBank(i);
        foreach (var pair in overrides.Where(pair => pair.Key is >= 8 and <= 100 && pair.Value.Count > 0)) result[pair.Key] = pair.Value;
        return result;
    }
}
