using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services.Zalo.AI;

/// <summary>
/// Rejects replies that expose the assistant's internal reasoning instead of a user-facing answer.
/// </summary>
internal static class ZaloAiAnswerSafetyPolicy
{
    public static bool LooksLikeInternalReasoning(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;

        var normalized = Regex.Replace(answer.Trim().ToLowerInvariant(), @"\s+", " ");
        string[] forbiddenMarkers =
        [
            "the user is asking",
            "the user wants",
            "the user said",
            "i should ",
            "i need to ",
            "i need ",
            "i am the assistant",
            "as the assistant",
            "in this simulation",
            "the conversation shows",
            "conversation history",
            "the bot previously",
            "from the last confirmed",
            "người dùng đang hỏi",
            "người dùng muốn",
            "tôi nên ",
            "tôi cần ",
            "trong mô phỏng này",
            "phần suy luận"
        ];
        return forbiddenMarkers.Any(normalized.Contains);
    }
}
