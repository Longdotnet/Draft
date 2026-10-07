using System.Text.Json;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal static class ZaloTeamPreferenceSemanticExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<ZaloTeamPreferenceCommand?> ParseAsync(
        ZaloNaturalTeamPreferenceContext context,
        string? model,
        Func<object, string, CancellationToken, Task<string?>> sendAsync,
        Action<Exception, string?> logInvalidOutput,
        CancellationToken cancellationToken)
    {
        const string prompt = """
            Bạn là semantic parser cho quan hệ xếp team bóng chuyền. Question có thể là tiếng Việt, English, 한국어, teencode, slang hoặc code-switch nhiều ngôn ngữ. Hiểu ý nghĩa trực tiếp từ Question; không cần dịch trước và không suy luận theo vài keyword đơn lẻ.
            Chỉ trả về một JSON object hoặc JSON null, không markdown.

            Schema:
            {"operation":"SET","relation":"TOGETHER","speechAct":"REQUEST","players":["Long","To An"],"sessionReference":"T6","confidence":0.96,"needsClarification":false,"clarificationQuestion":null}

            operation chỉ được SET, CLEAR, CHANGE, QUERY.
            relation chỉ được TOGETHER, APART, UNKNOWN.
            speechAct chỉ được REQUEST, QUESTION, SUGGESTION, UNCERTAIN.
            - TOGETHER = yêu cầu các players ở cùng team.
            - APART = yêu cầu các players ở khác team/không cùng team.
            - UNKNOWN nếu câu nhắc quan hệ nhưng chưa đủ rõ hướng nào.
            - SET tạo quan hệ mới; CLEAR bỏ yêu cầu cũ; CHANGE đổi quan hệ cũ sang hướng đối lập; QUERY chỉ hỏi trạng thái/khả năng.

            Chỉ speechAct REQUEST với relation rõ ràng mới có thể dẫn tới mutation sau bước xác nhận của backend. Câu hỏi, nói vu vơ, gợi ý hoặc câu có phủ định mơ hồ phải là QUESTION/SUGGESTION/UNCERTAIN và needsClarification=true khi cần.
            Ví dụ semantic:
            - "đừng xếp tui với @Nguyễn" => SET/APART/REQUEST.
            - "bro don't put me with Nguyễn tonight" => SET/APART/REQUEST.
            - "오늘 tui don't wanna be same team với Nguyễn" => SET/APART/REQUEST.
            - "저랑 Nguyễn 같은 팀으로 하지 마세요" => SET/APART/REQUEST.
            - "put me with Nguyễn" hoặc "같은 팀으로 해줘" => SET/TOGETHER/REQUEST.
            - "đừng tách tui với Nguyễn" => SET/TOGETHER/REQUEST.
            - "I don't mind playing with Nguyễn" => không phải yêu cầu SET; dùng UNCERTAIN hoặc SUGGESTION.
            - "Nguyễn하고 같은 팀 아니어도 돼" => không tự suy thành APART request; dùng UNCERTAIN nếu không có mệnh lệnh rõ.
            - "Can I avoid Nguyễn?" => QUESTION, không phải mutation.

            Grounding bắt buộc:
            - Chỉ dùng người có thật trong SenderName, MentionedUsers hoặc tên được nêu rõ trong Question. Không bịa người, ID hay session.
            - Nếu Question dùng đại từ ngôi thứ nhất như tui/tôi/mình/me/I/저/나 thì player tương ứng là SenderName.
            - Nếu MentionedUsers có người liên quan, giữ đúng DisplayName của họ. Không đổi sang một tên gần giống.
            - SET/APART chỉ nhận đúng 2 players. TOGETHER có thể có 2 đến 12 players.
            - sessionReference chỉ lấy thứ/ngày/tên trận thực sự có trong Question; không tự đoán từ AvailableSessions.
            - Share/chung một slot, thay phiên, +1 hay +2 vào slot không phải TeamPreference; trả JSON null.
            - Đây chỉ là semantic extraction. Backend mới kiểm tra roster, quyền, conflict, state token và confirmation.
            """;
        var payload = new
        {
            model,
            temperature = 0,
            max_tokens = 260,
            messages = new object[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = JsonSerializer.Serialize(context, JsonOptions) }
            }
        };
        var content = await sendAsync(payload, "team_preference_extraction", cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var relation = ReadString(root, "relation")?.Trim().ToUpperInvariant() switch
            {
                "TOGETHER" => ZaloTeamRelationshipKind.Together,
                "APART" => ZaloTeamRelationshipKind.Apart,
                _ => ZaloTeamRelationshipKind.Unknown
            };
            var operation = ReadString(root, "operation")?.Trim().ToUpperInvariant() switch
            {
                "SET" => ZaloTeamRelationshipOperation.Set,
                "CLEAR" => ZaloTeamRelationshipOperation.Clear,
                "CHANGE" => ZaloTeamRelationshipOperation.Change,
                "QUERY" => ZaloTeamRelationshipOperation.Query,
                _ => ZaloTeamRelationshipOperation.Unknown
            };
            var speechAct = ReadString(root, "speechAct")?.Trim().ToUpperInvariant() switch
            {
                "REQUEST" => ZaloTeamRelationshipSpeechAct.Request,
                "QUESTION" => ZaloTeamRelationshipSpeechAct.Question,
                "SUGGESTION" => ZaloTeamRelationshipSpeechAct.Suggestion,
                "UNCERTAIN" => ZaloTeamRelationshipSpeechAct.Uncertain,
                _ => ZaloTeamRelationshipSpeechAct.Unknown
            };
            var extractedPlayerCandidates = root.TryGetProperty("players", out var playerElement) && playerElement.ValueKind == JsonValueKind.Array
                ? playerElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!.Trim().TrimStart('@'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            var tooManyPlayers = extractedPlayerCandidates.Count > 12;
            var extractedPlayers = extractedPlayerCandidates.Take(12).ToList();
            var players = extractedPlayers
                .Where(player => IsGroundedPlayer(context, player))
                .ToList();
            var groundingFailed = players.Count != extractedPlayers.Count;
            if (extractedPlayerCandidates.Count < 2) return null;
            var confidence = root.TryGetProperty("confidence", out var confidenceElement) &&
                             confidenceElement.ValueKind == JsonValueKind.Number &&
                             confidenceElement.TryGetDouble(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0, 1)
                : 0.5;
            var needsClarification = root.TryGetProperty("needsClarification", out var clarificationElement) &&
                                     clarificationElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? clarificationElement.GetBoolean()
                : relation == ZaloTeamRelationshipKind.Unknown ||
                  operation == ZaloTeamRelationshipOperation.Unknown ||
                  speechAct != ZaloTeamRelationshipSpeechAct.Request;
            if (relation == ZaloTeamRelationshipKind.Unknown ||
                operation == ZaloTeamRelationshipOperation.Unknown ||
                speechAct == ZaloTeamRelationshipSpeechAct.Unknown ||
                groundingFailed ||
                tooManyPlayers ||
                confidence < ZaloBotIntelligence.TeamRelationshipMutationConfidenceThreshold ||
                speechAct is ZaloTeamRelationshipSpeechAct.Suggestion or ZaloTeamRelationshipSpeechAct.Uncertain ||
                speechAct == ZaloTeamRelationshipSpeechAct.Question &&
                operation != ZaloTeamRelationshipOperation.Query)
                needsClarification = true;
            if (relation == ZaloTeamRelationshipKind.Apart && players.Count != 2)
                needsClarification = true;

            var rawSessionReference = ReadString(root, "sessionReference")?.Trim();
            var sessionReference = IsGroundedPhrase(context.Question, rawSessionReference)
                ? rawSessionReference
                : null;

            return new ZaloTeamPreferenceCommand(
                players,
                SessionReference: sessionReference,
                Relation: relation,
                Operation: operation,
                SpeechAct: speechAct,
                Confidence: confidence,
                NeedsClarification: needsClarification,
                ClarificationQuestion: ReadString(root, "clarificationQuestion")?.Trim());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            logInvalidOutput(exception, content);
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool IsGroundedPlayer(ZaloNaturalTeamPreferenceContext context, string player)
    {
        var normalized = ZaloBotIntelligence.Normalize(player);
        if (normalized.Length == 0) return false;
        if (normalized == ZaloBotIntelligence.Normalize(context.SenderName)) return true;
        if (normalized is "tui" or "toi" or "minh" or "me" or "i" or "myself" or "저" or "나" or "내" or "제")
            return true;
        if (context.MentionedUsers.Any(user =>
                normalized == ZaloBotIntelligence.Normalize(user.DisplayName.Trim().TrimStart('@'))))
            return true;
        return IsGroundedPhrase(context.Question, player);
    }

    private static bool IsGroundedPhrase(string question, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        var normalizedQuestion = ZaloBotIntelligence.Normalize(question);
        var normalizedPhrase = ZaloBotIntelligence.Normalize(phrase.Trim().TrimStart('@'));
        if (normalizedPhrase.Length == 0) return false;
        return Regex.IsMatch(
            normalizedQuestion,
            $@"(?:^|[^\p{{L}}\p{{N}}]){Regex.Escape(normalizedPhrase)}(?:$|[^\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant);
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        var fence = new string((char)96, 3);
        if (!trimmed.StartsWith(fence, StringComparison.Ordinal)) return trimmed;
        var escapedFence = Regex.Escape(fence);
        trimmed = Regex.Replace(trimmed, $"^{escapedFence}(?:json)?\\s*", string.Empty, RegexOptions.IgnoreCase);
        return Regex.Replace(trimmed, $"\\s*{escapedFence}$", string.Empty);
    }
}
