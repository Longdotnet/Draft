using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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
    private readonly HttpClient httpClient;

    public ZaloSemanticGuestPlanner(
        IConfiguration configuration,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.httpClient = httpClient ?? SharedHttpClient;
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
        if (!IsConfigured())
            return ZaloSemanticGuestPlan.None("semantic_guest_ai_not_configured");
        if (!ZaloAiBudgetLimiter.TryAcquire(
                connectionId,
                groupId,
                snapshot.SponsorZaloUserId,
                settings.MaxUserCallsPerMinute,
                settings.MaxGroupCallsPerMinute))
            return ZaloSemanticGuestPlan.None("semantic_guest_budget_exhausted");

        var prompt = """
            Bạn là SEMANTIC GUEST ACTION PLANNER cho bot nhóm bóng chuyền.
            Chỉ hiểu ý nghĩa hội thoại và trả JSON. KHÔNG trả lời người dùng, KHÔNG gọi tool, KHÔNG sửa database.

            GroundingSnapshot là dữ liệu authority từ backend. SessionId và SponsorZaloUserId đã được khóa bởi reply graph/current sender; tuyệt đối không đổi sang session/người khác.
            ExistingGuests chứa guest thật của đúng sponsor. Nếu dùng reservationId thì phải copy NGUYÊN VĂN một ID có trong ExistingGuests. Không bịa ID.

            Actions:
            - AddGuests: người gửi thực sự xác nhận dẫn/thêm 1 hoặc 2 bạn chơi. Không dùng cho ý định tương lai/không chắc như "để tui hỏi bạn", "tui thử rủ", "có thể dẫn bạn".
            - UpdateGuestProfiles: bổ sung/đổi tên/giới tính/trình độ/vị trí cho guest đã giữ.
            - CancelGuests: guest đã giữ/chờ không đi nữa.
            - None: chat thường, chỉ hỏi, chưa cam kết, hoặc không liên quan guest.

            Từ chỉ người chung chung KHÔNG phải tên riêng: "bạn", "bạn tui", "bạn mình", "thằng bạn", "nhỏ bạn", "đứa bạn", "người", "khách", "bạn nha". Với các câu như "+1 cho bạn nha", "tui dẫn thêm thằng bạn", displayName phải null.
            Nếu tên thật rõ như "Minh", "Huy", "Ngọc Anh" thì mới điền displayName.

            Mapping:
            - gender: Male/Female/null.
            - level: Good cho giỏi/khá/tốt; Average cho trung bình/tb/bình thường; New cho mới/newbie/mới chơi; không rõ => null.
            - role: Attack/Defense/Setter/FullStack/New/null, chỉ điền khi người dùng thực sự nói vị trí.

            Multi guest:
            - "+2 nam nữ" => guest đầu Male, guest sau Female.
            - "+2 nữ nam" => ngược lại.
            - "+2 đều nam" => cả hai Male.
            - "+2 Minh nam khá, Huy nữ trung bình" => hai guest riêng với name/gender/level tương ứng.
            - Khi ActiveGuestConversation có đúng hai guest đang thiếu gender, câu "nam nữ" có thể map theo SponsorSequence tăng dần.
            - Nếu có hai guest mà người dùng chỉ nói "nam" và không nói cả hai/# nào, phải needsClarification=true; không tự đoán target.

            Optional profile không được cản giữ slot. Ví dụ "+1 bạn Nam" có thể mơ hồ Nam là tên hay giới tính: nếu quantity/add intent chắc chắn thì vẫn trả AddGuests quantity=1, nhưng bỏ field profile không chắc (null hoặc confidence thấp) và needsClarification=true.
            Quantity là authority quan trọng cho AddGuests. Nếu chỉ nói "mấy đứa bạn tui" mà không xác định 1 hay 2, quantity=null và needsClarification=true.

            Chỉ trả đúng JSON object:
            {
              "action":"None|AddGuests|UpdateGuestProfiles|CancelGuests",
              "confidence":0.0,
              "quantity":1,
              "quantityConfidence":0.0,
              "guests":[
                {
                  "referenceText":"#1|Minh|guest đầu|...",
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

        var payload = new
        {
            model = configuration["Ai:Model"],
            temperature = 0,
            max_tokens = 820,
            messages = new object[]
            {
                new { role = "system", content = prompt },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
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
                    })
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration["Ai:Endpoint"])
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["Ai:ApiKey"]);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Semantic guest planner AI returned {StatusCode}; failing closed.", (int)response.StatusCode);
                return ZaloSemanticGuestPlan.None("semantic_guest_ai_error");
            }

            using var document = JsonDocument.Parse(body);
            return ParsePlan(ReadModelContent(document.RootElement));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Semantic guest planner failed; failing closed.");
            return ZaloSemanticGuestPlan.None("semantic_guest_ai_error");
        }
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
                        ReadEnum<PlayerGender>(node, "gender"),
                        ReadConfidence(node, "genderConfidence"),
                        ReadEnum<PlayerLevel>(node, "level"),
                        ReadConfidence(node, "levelConfidence"),
                        ReadEnum<PlayerRole>(node, "role"),
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
                Clean(ReadString(root, "reason"), 180));
        }
        catch (JsonException)
        {
            return ZaloSemanticGuestPlan.None("semantic_guest_malformed_json");
        }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["Ai:Endpoint"]) &&
        !string.IsNullOrWhiteSpace(configuration["Ai:ApiKey"]) &&
        !string.IsNullOrWhiteSpace(configuration["Ai:Model"]);

    private static string? ReadModelContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }
        return root.TryGetProperty("output_text", out var output) && output.ValueKind == JsonValueKind.String
            ? output.GetString()
            : null;
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
