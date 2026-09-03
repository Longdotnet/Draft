namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Topic-switch and continuation policy for pending conversational state.
/// A pending state may only consume turns that are actually relevant to that state.
/// </summary>
public static class ZaloPendingTurnPolicy
{
    public static ZaloPendingTurnDisposition ClassifySessionTurn(
        string pendingIntent,
        string currentQuestion,
        bool mentionedBot,
        string? freshIntent = null,
        double freshConfidence = 0)
    {
        if (IsNaturalCancel(currentQuestion))
            return ZaloPendingTurnDisposition.CancelPending;

        // Exact deterministic commands own the current turn even when their suffix is
        // also a valid session selector (for example `8 T4`). A stale clarification
        // must never reinterpret a new command as its own short answer.
        if (ZaloMenuCommandParser.TryParse(currentQuestion, out _, out _))
            return ZaloPendingTurnDisposition.SwitchToNewIntent;

        if (!string.IsNullOrWhiteSpace(freshIntent) &&
            freshConfidence >= .85 &&
            !string.Equals(pendingIntent, freshIntent, StringComparison.OrdinalIgnoreCase))
            return ZaloPendingTurnDisposition.SwitchToNewIntent;

        if (ZaloSessionResolver.LooksLikeSelector(currentQuestion))
            return ZaloPendingTurnDisposition.ContinuePending;

        // A session-choice prompt requires an actual session selector. A bare
        // confirmation cannot safely choose among T4/T6 and therefore must not let
        // old pending state consume a fresh bot-addressed action.
        if (IsStrongConfirmation(currentQuestion))
        {
            return mentionedBot
                ? ZaloPendingTurnDisposition.SwitchToNewIntent
                : ZaloPendingTurnDisposition.IgnoreCurrentTurn;
        }

        return mentionedBot
            ? ZaloPendingTurnDisposition.SwitchToNewIntent
            : ZaloPendingTurnDisposition.IgnoreCurrentTurn;
    }

    public static bool IsNaturalCancel(string value)
    {
        var normalized = ZaloTextNormalizer.Normalize(value)
            .Trim(' ', '.', '!', '?', ',', ';', ':');

        if (normalized.Length == 0)
            return false;

        return normalized is
                   "huy" or "cancel" or "thoi" or "bo qua" or "khong can nua" or
                   "thoi khoi" or "thoi khoi di" or "hoi khoi di" or "khoi" or "khoi di" or
                   "bo di" or "khong lam nua" ||
               normalized.StartsWith("huy ", StringComparison.Ordinal) ||
               normalized.StartsWith("cancel ", StringComparison.Ordinal) ||
               normalized.StartsWith("thoi khoi", StringComparison.Ordinal) ||
               normalized.StartsWith("hoi khoi", StringComparison.Ordinal) ||
               normalized.StartsWith("khoi di", StringComparison.Ordinal) ||
               normalized.StartsWith("bo qua ", StringComparison.Ordinal) ||
               normalized.Contains("khong can nua", StringComparison.Ordinal);
    }

    public static bool IsStrongConfirmation(string value)
    {
        var normalized = ZaloTextNormalizer.Normalize(value);
        return normalized is
                   "xac nhan" or "xac nhan draft" or "dong y" or "ok" or "ok chay" or
                   "chay di" or "draft di" or "chot" or "lam di" or "thuc hien di" ||
               normalized.StartsWith("xac nhan ", StringComparison.Ordinal) ||
               normalized.StartsWith("dong y ", StringComparison.Ordinal) ||
               normalized.StartsWith("chot ", StringComparison.Ordinal);
    }
}
