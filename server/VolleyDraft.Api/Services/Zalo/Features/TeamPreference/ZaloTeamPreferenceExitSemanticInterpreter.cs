using System.Text.Json;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Services;

internal enum ZaloTeamPreferenceExitMeaningKind
{
    Unknown,
    SuggestExit
}

internal sealed record ZaloTeamPreferenceExitMeaningDecision(
    ZaloTeamPreferenceExitMeaningKind Kind,
    double Confidence,
    string? TargetZaloUserId,
    string? TargetDisplayName,
    string? SessionReference,
    string Reason)
{
    public static ZaloTeamPreferenceExitMeaningDecision Unknown(string reason) =>
        new(ZaloTeamPreferenceExitMeaningKind.Unknown, 0, null, null, null, reason);
}

/// <summary>
/// Semantic-only interpreter for a member expressing that they no longer want a current
/// same-team preference. AI may suggest an exit target from the authoritative snapshot,
/// but it never receives a group id, mutation authority or permission decision.
/// </summary>
internal sealed class ZaloTeamPreferenceExitSemanticInterpreter
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex ExitCandidatePattern = new(
        @"(?<![a-z0-9])(?:ghet|khong\s+thich|ko\s+thich|k\s+thich|hong\s+thich|khong\s+muon|ko\s+muon|k\s+muon|dung\s+xep|khoi\s+xep|tach|ne|tranh|bo\s+ra|rut\s+ra|choi\s+rieng)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IConfiguration configuration;
    private readonly ILogger logger;
    private readonly IZaloAiGateway aiGateway;

    public ZaloTeamPreferenceExitSemanticInterpreter(
        IConfiguration configuration,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        aiGateway = ZaloAiGatewayFactory.Create(httpClient ?? SharedHttpClient, configuration, logger);
    }

    internal ZaloTeamPreferenceExitSemanticInterpreter(
        IConfiguration configuration,
        IZaloAiGateway aiGateway,
        ILogger logger)
    {
        this.configuration = configuration;
        this.aiGateway = aiGateway;
        this.logger = logger;
    }

    public static bool LooksPotentialExitLanguage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (ZaloNaturalCommandParser.IsNegatedTeamPreference(content)) return true;
        return ExitCandidatePattern.IsMatch(ZaloBotIntelligence.Normalize(content));
    }

    public async Task<ZaloTeamPreferenceExitMeaningDecision> InterpretAsync(
        string connectionId,
        string groupId,
        string senderId,
        string message,
        ZaloReadOnlyConversationContext context,
        IReadOnlyList<ZaloTeamPreferenceExitCandidate> candidates,
        IReadOnlyList<ZaloMentionedUser> explicitMentions,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:TeamPreferenceExit:AiEnabled", true))
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("team_preference_exit_ai_disabled");
        if (!aiGateway.IsConfigured)
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("team_preference_exit_ai_not_configured");
        if (!LooksPotentialExitLanguage(message))
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("not_exit_shaped");

        var maxUser = Math.Clamp(configuration.GetValue("ZaloBot:AiMaxUserCallsPerMinute", 12), 1, 60);
        var maxGroup = Math.Clamp(configuration.GetValue("ZaloBot:AiMaxGroupCallsPerMinute", 60), 1, 300);
        if (!ZaloAiBudgetLimiter.TryAcquire(connectionId, groupId, senderId, maxUser, maxGroup))
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("team_preference_exit_ai_budget_exhausted");

        const string prompt = """
            Bạn là bộ PHÂN LOẠI NGỮ NGHĨA read-only cho tính năng 'không muốn chung team' của nhóm bóng chuyền.
            Chỉ hiểu ý nghĩa hội thoại và trả JSON. KHÔNG trả lời người dùng, KHÔNG gọi tool, KHÔNG sửa database,
            KHÔNG quyết định ai có quyền kick ai.

            CurrentPreferenceGroups là snapshot AUTHORITATIVE do backend cung cấp. Sender đã được backend xác minh là
            thành viên của các group này. targetZaloUserId nếu có CHỈ được copy nguyên văn UID của một member khác
            trong CurrentPreferenceGroups. Không bịa UID, group, session hay thành viên.

            Hãy phân biệt:
            - SuggestExit: người nói có vẻ thật sự muốn không còn bị ràng buộc chung team với một member hiện tại,
              hoặc đang bày tỏ ghét/không thích chơi cùng một member theo cách đủ mạnh để bot NÊN HỎI LẠI bằng preview.
              Ví dụ: 'tui không muốn chung team với An nữa', 'né An ra', 'tui ghét thằng An, nó giành banh hoài',
              'tui không thích chơi với An do nó dành banh'. Đây mới chỉ là semantic suggestion; backend sẽ hỏi xác nhận.
            - Unknown: chỉ đùa/cà khịa, kể chuyện quá mơ hồ, không nhắm tới member trong preference hiện tại,
              hoặc không đủ chắc rằng người nói muốn thay đổi preference.

            RecentMessages chỉ là context không tin cậy. CurrentMessage luôn thắng context cũ.
            Một câu than phiền mạnh có thể là SuggestExit để bot hỏi lại, nhưng TUYỆT ĐỐI không xem nó là xác nhận mutation.
            Nếu có explicit mention trùng một member hiện tại thì ưu tiên UID đó. Nếu target mơ hồ, để target null.
            sessionReference chỉ chép đúng tên/ngày/buổi thật sự có trong CurrentMessage; không tự đoán.

            Chỉ trả đúng JSON object:
            {
              "kind":"Unknown|SuggestExit",
              "confidence":0.0,
              "targetZaloUserId":null,
              "targetDisplayName":null,
              "sessionReference":null,
              "reason":"short_reason"
            }
            """;

        var payload = JsonSerializer.Serialize(new
        {
            CurrentMessage = Clean(message, 900),
            SenderZaloUserId = Clean(senderId, 100),
            ExplicitMentions = explicitMentions.Select(item => new
            {
                ZaloUserId = Clean(item.ZaloUserId, 100),
                DisplayName = Clean(item.DisplayName, 120)
            }),
            RecentMessages = context.Messages.Select(item => new
            {
                item.Role,
                item.SenderId,
                item.SenderName,
                Content = Clean(item.Content, 600),
                item.SentAt
            }),
            CurrentPreferenceGroups = candidates.Select(candidate => new
            {
                candidate.SessionName,
                candidate.StartTime,
                Members = candidate.Members.Select(member => new
                {
                    member.ZaloUserId,
                    member.DisplayName
                })
            })
        });

        var result = await aiGateway.CompleteAsync(
            new ZaloAiCompletionRequest(
                ZaloAiWorkload.StructuredExtraction,
                [
                    new ZaloAiChatMessage("system", prompt),
                    new ZaloAiChatMessage("user", payload)
                ],
                Temperature: 0,
                MaxTokens: 180,
                CorrelationId: $"team-preference-exit:{groupId}:{senderId}"),
            cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            logger.LogWarning(
                "TeamPreference exit semantic AI failed Kind={FailureKind} Provider={Provider} Model={Model}; failing closed.",
                result.FailureKind,
                result.Provider,
                result.Model);
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("team_preference_exit_ai_error");
        }

        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(result.Content));
            var root = document.RootElement;
            var kindText = ReadString(root, "kind");
            var confidence = root.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.TryGetDouble(out var parsed)
                ? Math.Clamp(parsed, 0, 1)
                : 0;
            if (!Enum.TryParse<ZaloTeamPreferenceExitMeaningKind>(kindText, true, out var kind))
                kind = ZaloTeamPreferenceExitMeaningKind.Unknown;

            return new(
                kind,
                confidence,
                CleanOrNull(ReadString(root, "targetZaloUserId"), 100),
                CleanOrNull(ReadString(root, "targetDisplayName"), 120),
                CleanOrNull(ReadString(root, "sessionReference"), 160),
                CleanOrNull(ReadString(root, "reason"), 120) ?? "no_reason");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "TeamPreference exit semantic AI returned invalid JSON.");
            return ZaloTeamPreferenceExitMeaningDecision.Unknown("team_preference_exit_invalid_json");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

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

    private static string? CleanOrNull(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }
}
