using System.Text.Json;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Services;

/// <summary>
/// AI interprets natural guest language only. It never receives mutation authority:
/// session/sponsor are already grounded by the reply graph, and reservation IDs are
/// accepted later only when they exist in the supplied snapshot.
/// </summary>
internal sealed class ZaloSemanticGuestPlanner
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IConfiguration configuration;
    private readonly ILogger logger;
    private readonly IZaloAiGateway aiGateway;

    public ZaloSemanticGuestPlanner(
        IConfiguration configuration,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        aiGateway = ZaloAiGatewayFactory.Create(httpClient ?? SharedHttpClient, configuration, logger);
    }

    public async Task<ZaloSemanticGuestPlan> PlanAsync(
        string connectionId,
        string groupId,
        string message,
        ZaloReadOnlyConversationContext context,
        ZaloSemanticGuestGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:DraftAutopilot:GuestSemanticAiEnabled", true) || !settings.Enabled)
            return ZaloSemanticGuestPlan.None("semantic_guest_disabled");
        if (!aiGateway.IsConfigured)
            return ZaloSemanticGuestPlan.None("semantic_guest_ai_not_configured");
        if (!ZaloAiBudgetLimiter.TryAcquire(
                connectionId,
                groupId,
                snapshot.SponsorZaloUserId,
                settings.MaxUserCallsPerMinute,
                settings.MaxGroupCallsPerMinute))
            return ZaloSemanticGuestPlan.None("semantic_guest_budget_exhausted");

        const string prompt = """
            Bạn là SEMANTIC GUEST DOMAIN PLANNER cho bot nhóm bóng chuyền.
            Chỉ hiểu ý nghĩa hội thoại và trả JSON. KHÔNG trả lời người dùng, KHÔNG gọi tool, KHÔNG sửa database.

            GroundingSnapshot là authority. SessionId/SponsorZaloUserId đã khóa bởi backend; không đổi sang người/kèo khác.
            ExistingGuests là guest thật của đúng sponsor và là TẬP DUY NHẤT được phép làm target mutation. Nếu dùng reservationId phải copy nguyên văn ID trong ExistingGuests. World chỉ là read-only context để hiểu roster/recruitment/guest của cả tình huống; tuyệt đối không lấy ID từ World.Guests của sponsor khác làm target.

            AnchorKind:
            - RecruitmentBroadcast: reply đúng tin tuyển người.
            - GuestConversation / ActiveGuestConversation: đang nói tiếp về guest đã biết.
            - PendingGuestAction: bot vừa hỏi field thiếu; PendingMissingFields cho biết đang chờ gì.
            - RecentGuestMutation: correction/undo chỉ trên ExistingGuests đã được backend giới hạn.

            Actions:
            - AddGuests: xác nhận chắc chắn thêm 1/2 guest và giữ slot ngay.
            - AddTentativeGuests: người gửi nói chưa chắc/còn phải hỏi/chắc khoảng/có thể dẫn guest. Tentative chỉ ghi nhớ, KHÔNG chiếm slot. Ví dụ "chắc tui dẫn thêm 1", "để tui hỏi Minh xem đi không", "có thể có 2 bạn".
            - ScheduleConditionalGuests: người gửi đặt điều kiện tương lai kiểu "nếu 19h vẫn thiếu thì +2", "7h mà còn thiếu thì cho 1 bạn tui vô". Action này CHỈ lưu một condition; KHÔNG cộng slot ngay. Phải điền conditionalHour, conditionalMinute, conditionalEvening và minimumMissingSlots. Nếu user chỉ nói "vẫn thiếu" thì minimumMissingSlots=1. Nếu nói "còn thiếu 2 slot" thì minimumMissingSlots=2.
            - ConfirmGuests: một guest Tentative trước đó giờ đã được xác nhận đi. Ví dụ "ừ nó đi", "Minh chốt đi nha", "2 bạn đó đi được". Chỉ target ExistingGuests có Status=Tentative.
            - ReplaceGuest: một guest cũ nghỉ và có một guest mới thay vào trong cùng ý định. Guests PHẢI có đúng thứ tự: phần tử 0 = guest cũ (reference/ID grounded), phần tử 1 = guest mới (reservationId=null, sponsorSequence=null, profile mới). Ví dụ "Minh nghỉ, cho Huy thay Minh".
            - UpdateGuestProfiles: bổ sung/đổi tên/giới tính/trình độ/vị trí guest đã biết, kể cả Tentative.
            - CancelGuests: guest đã giữ/chờ/tentative không đi nữa, hoặc undo recent add.
            - None: chat thường, hỏi thông tin, hoặc không liên quan.

            Phân biệt commitment:
            - "tui dẫn Huy", "+1 Huy", "Huy đi nha" trong recruitment => AddGuests nếu chắc chắn.
            - "chắc tui dẫn Huy", "có thể Huy đi", "để tui hỏi Huy" => AddTentativeGuests, không AddGuests.
            - "nếu 19h vẫn thiếu thì +2" => ScheduleConditionalGuests, tuyệt đối không AddGuests/AddTentativeGuests ngay.
            - Nếu ExistingGuests có Huy Tentative và user nói "Huy đi nha"/"ừ nó đi" => ConfirmGuests, không tạo guest mới.
            - "Minh nghỉ, Huy thế Minh" => ReplaceGuest chứ không tách thành Cancel + Add.

            Conditional time:
            - conditionalHour là giờ người dùng nói theo local time Việt Nam, 0..23.
            - conditionalMinute mặc định 0.
            - conditionalEvening=true nếu user nói rõ "tối/chiều" với giờ 1..11; backend sẽ resolve AM/PM an toàn theo giờ trận.
            - Không tự suy ra ngày/kèo khác; backend dùng đúng ngày của SessionId đã grounded.

            Correction examples RecentGuestMutation:
            - "à nhầm bạn đó nữ" => UpdateGuestProfiles.
            - "bạn thứ 2 không đi nữa" => CancelGuests guest thứ 2.
            - "thôi chỉ +1 thôi" => CancelGuests guest sau trong recent mutation.
            - "undo cái +2 hồi nãy" => CancelGuests cả hai ExistingGuests.

            Từ chung chung KHÔNG phải tên: "bạn", "bạn tui", "bạn mình", "thằng bạn", "nhỏ bạn", "đứa bạn", "người", "khách", "bạn nha". "+1 cho bạn nha" => displayName=null.
            Tên thật rõ như Minh/Huy/Ngọc Anh mới điền displayName.

            Mapping profile:
            - gender Male/Female/null.
            - level Good=giỏi/khá/tốt; Average=trung bình/tb/bình thường; New=mới/newbie/mới chơi.
            - role Attack/Defense/Setter/FullStack/New/null khi người dùng thật sự nói vị trí.

            Multi guest:
            - "+2 nam nữ" => Male, Female.
            - "+2 đều nam" => Male, Male.
            - "+2 Minh nam khá, Huy nữ trung bình" => hai item riêng.
            - Nếu hai guest mà chỉ nói "nam" không rõ target/cả hai => needsClarification=true.

            Optional profile không được làm mất slot. Quantity/target/action mới là authority quan trọng. Khi profile mơ hồ, bỏ field confidence thấp và có thể needsClarification=true.

            Chỉ trả đúng JSON object:
            {
              "action":"None|AddGuests|AddTentativeGuests|ScheduleConditionalGuests|ConfirmGuests|ReplaceGuest|UpdateGuestProfiles|CancelGuests",
              "confidence":0.0,
              "quantity":1,
              "quantityConfidence":0.0,
              "conditionalHour":null,
              "conditionalMinute":null,
              "conditionalEvening":false,
              "minimumMissingSlots":null,
              "guests":[
                {
                  "referenceText":"#1|Minh|bạn thứ hai|replacement|...",
                  "reservationId":null,
                  "sponsorSequence":null,
                  "displayName":null,
                  "nameConfidence":0.0,
                  "gender":"Male|Female|null",
                  "genderConfidence":0.0,
                  "level":"Good|Average|New|null",
                  "levelConfidence":0.0,
                  "role":"Attack|Defense|Setter|FullStack|New|null",
                  "roleConfidence":0.0,
                  "confidence":0.0
                }
              ],
              "needsClarification":false,
              "clarificationReason":"short reason",
              "reason":"short reason"
            }
            """;

        var userPayload = JsonSerializer.Serialize(new
        {
            CurrentMessage = Clean(message, 900),
            ConversationContext = context.Messages.Select(item => new
            {
                item.Role,
                item.SenderId,
                item.SenderName,
                Content = Clean(item.Content, 600),
                item.SentAt
            }),
            GroundingSnapshot = snapshot
        });

        var result = await aiGateway.CompleteAsync(
            new ZaloAiCompletionRequest(
                ZaloAiWorkload.StructuredExtraction,
                [
                    new ZaloAiChatMessage("system", prompt),
                    new ZaloAiChatMessage("user", userPayload)
                ],
                Temperature: 0,
                MaxTokens: 900,
                CorrelationId: $"guest:{groupId}:{snapshot.SponsorZaloUserId}"),
            cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning(
                "Semantic guest planner failed Kind={FailureKind} Provider={Provider} Model={Model}; failing closed.",
                result.FailureKind,
                result.Provider,
                result.Model);
            return ZaloSemanticGuestPlan.None("semantic_guest_ai_error");
        }

        return ParsePlan(result.Content);
    }

    internal static ZaloSemanticGuestPlan ParsePlan(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");
        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(content));
            var root = document.RootElement;
            if (!Enum.TryParse<ZaloSemanticGuestActionKind>(ReadString(root, "action"), true, out var action))
                return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");

            var guests = new List<ZaloSemanticGuestPlanItem>();
            if (root.TryGetProperty("guests", out var guestsNode))
            {
                if (guestsNode.ValueKind != JsonValueKind.Array)
                    return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");
                foreach (var node in guestsNode.EnumerateArray().Take(4))
                {
                    if (node.ValueKind != JsonValueKind.Object)
                        return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");
                    guests.Add(new ZaloSemanticGuestPlanItem(
                        Clean(ReadString(node, "referenceText"), 120),
                        NullIfEmpty(Clean(ReadString(node, "reservationId"), 100)),
                        ReadNullableInt(node, "sponsorSequence"),
                        NullIfEmpty(Clean(ReadString(node, "displayName"), 80)),
                        ReadConfidence(node, "nameConfidence"),
                        ReadEnum<VolleyDraft.Api.Models.PlayerGender>(node, "gender"),
                        ReadConfidence(node, "genderConfidence"),
                        ReadEnum<VolleyDraft.Api.Models.PlayerLevel>(node, "level"),
                        ReadConfidence(node, "levelConfidence"),
                        ReadEnum<VolleyDraft.Api.Models.PlayerRole>(node, "role"),
                        ReadConfidence(node, "roleConfidence"),
                        ReadConfidence(node, "confidence")));
                }
            }

            return new ZaloSemanticGuestPlan(
                action,
                ReadConfidence(root, "confidence"),
                ReadNullableInt(root, "quantity"),
                ReadConfidence(root, "quantityConfidence"),
                guests,
                ReadBool(root, "needsClarification"),
                Clean(ReadString(root, "clarificationReason"), 180),
                Clean(ReadString(root, "reason"), 180),
                ReadNullableInt(root, "conditionalHour"),
                ReadNullableInt(root, "conditionalMinute"),
                ReadBool(root, "conditionalEvening"),
                ReadNullableInt(root, "minimumMissingSlots"));
        }
        catch (JsonException)
        {
            return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");
        }
    }

    private static T? ReadEnum<T>(JsonElement node, string name) where T : struct, Enum
    {
        var text = ReadString(node, name);
        return Enum.TryParse<T>(text, true, out var value) ? value : null;
    }

    private static int? ReadNullableInt(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double ReadConfidence(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? Math.Clamp(number, 0, 1)
            : 0;

    private static bool ReadBool(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine >= 0) text = text[(firstNewLine + 1)..];
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? text[..lastFence].Trim() : text.Trim();
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
