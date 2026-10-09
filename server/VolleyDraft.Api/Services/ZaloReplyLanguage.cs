namespace VolleyDraft.Api.Services;

internal enum ZaloReplyLanguage
{
    Vietnamese,
    English,
    Korean
}

internal static class ZaloReplyLanguageDetector
{
    public static ZaloReplyLanguage? TryDetectFromScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Any(IsHangul)) return ZaloReplyLanguage.Korean;
        if (text.Any(IsVietnameseSpecificLetter)) return ZaloReplyLanguage.Vietnamese;
        return null;
    }

    public const string SameLanguagePromptInstruction =
        "Reply in the same natural language as Question. Detect the language from the full current Question, not from a keyword list. " +
        "If Question code-switches, use the dominant language or the language the user is clearly using to address the bot. " +
        "Do not default to Vietnamese merely because the system instructions are written in Vietnamese.";

    public static string GetGeneralFallback(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English => "I couldn't complete that reply. Please send it again in a shorter sentence.",
        ZaloReplyLanguage.Korean => "답변을 끝까지 만들지 못했어요. 짧게 다시 보내 주세요.",
        _ => "Mình chưa hoàn tất được câu trả lời. Bạn gửi lại ngắn gọn giúp mình nhé."
    };

    public static string GetReasoningFallback(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English => "I'm not fully sure I understood that. Please say it again briefly or type help.",
        ZaloReplyLanguage.Korean => "요청을 정확히 이해하지 못했어요. 짧게 다시 말하거나 help를 입력해 주세요.",
        _ => "Mình chưa hiểu chắc yêu cầu này. Bạn nói lại ngắn gọn hoặc gõ help nhé."
    };

    private static bool IsHangul(char character) =>
        character is >= '\uAC00' and <= '\uD7AF' or
        >= '\u1100' and <= '\u11FF' or
        >= '\u3130' and <= '\u318F';

    private static bool IsVietnameseSpecificLetter(char character)
    {
        if (character is 'đ' or 'Đ') return true;
        var decomposed = character.ToString().Normalize(System.Text.NormalizationForm.FormD);
        if (decomposed.Length < 2) return false;
        // Only use marks that strongly distinguish Vietnamese from ordinary Latin text.
        // Tone marks alone (acute/grave/etc.) are not enough because other languages use them too.
        return decomposed.Skip(1).Any(mark => mark is '\u0302' or '\u0306' or '\u031B');
    }
}

internal static class ZaloCommonReplyText
{
    public static string NoActiveSessions(ZaloReplyLanguage language) => language switch
    {
        ZaloReplyLanguage.English =>
            "This group doesn't have any match with the bot enabled right now. Please ask an admin to check the bot configuration.",
        ZaloReplyLanguage.Korean =>
            "현재 이 그룹에는 봇이 활성화된 경기가 없어요. 관리자에게 봇 설정을 확인해 달라고 해 주세요.",
        _ =>
            "Nhóm này chưa có trận nào đang bật bot. Bạn nhờ admin kiểm tra cấu hình nhé."
    };

    public static string RateLimited(ZaloReplyLanguage? language) => language switch
    {
        ZaloReplyLanguage.English => "You're asking a bit fast 😄 Please wait a moment and try again.",
        ZaloReplyLanguage.Korean => "메시지가 조금 빨라요 😄 잠시 후 다시 보내 주세요.",
        ZaloReplyLanguage.Vietnamese => "Bạn hỏi hơi nhanh rồi 😄 Chờ một chút rồi hỏi lại giúp mình nhé.",
        _ => "⏳"
    };
}
