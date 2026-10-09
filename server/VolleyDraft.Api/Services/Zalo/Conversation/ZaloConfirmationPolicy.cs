namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Cross-language confirmation controls live in the conversation core rather than
/// growing the legacy bot-intelligence facade. Keep existing controls compatible.
/// </summary>
internal static class ZaloConfirmationPolicy
{
    public static bool IsConfirmation(string value)
    {
        var normalized = ZaloTextNormalizer.Normalize(value);
        return normalized is "xac nhan" or "xac nhan draft" or "dong y" or "ok chay" or "chay di" or "thuc hien di" or
               "duoc" or "ok" or "chot" or "lam di" or "tao di" or "trien khai" or
               "confirm" or "confirmed" or "yes" or "go ahead" or "do it" or
               "확인" or "확인해" or "확인 해" or "네" or "응" or "진행해" or "진행 해" ||
               normalized.StartsWith("chot ", StringComparison.Ordinal) ||
               normalized.StartsWith("dong y ", StringComparison.Ordinal) ||
               normalized.StartsWith("xac nhan draft ", StringComparison.Ordinal) ||
               normalized.StartsWith("confirm ", StringComparison.Ordinal) ||
               normalized.StartsWith("확인 ", StringComparison.Ordinal);
    }
}
