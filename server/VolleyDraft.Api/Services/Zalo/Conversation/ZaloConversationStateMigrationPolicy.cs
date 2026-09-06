using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public sealed record ZaloPendingMigrationDecision(
    ZaloTopicSwitchDecision Decision,
    string? FreshIntent,
    double Confidence,
    string Reason);

/// <summary>
/// Transitional policy used while legacy pending workflows are migrated to the
/// structured V2 state store. It only clears a legacy pending workflow for a
/// high-confidence deterministic operational intent from a different intent family.
/// Ambiguous/general chat remains on the existing pending path unless the pending
/// workflow is already at a confirmation boundary, where only confirmation/cancel
/// language (or an explicit same-family correction) still belongs to that workflow.
/// </summary>
public static class ZaloConversationStateMigrationPolicy
{
    public static ZaloPendingMigrationDecision Evaluate(string pendingIntent, string currentQuestion)
    {
        // This policy is called only after the webhook has established that the turn
        // explicitly addresses the bot. Addressing is transport metadata, not intent
        // text: keeping a visible leading @Npc token here makes otherwise canonical
        // confirmations such as "@Npc xác nhận" fail IsConfirmation and causes the
        // migration layer to delete the executable pending action before V1 can run it.
        var question = RemoveLeadingAddress(currentQuestion ?? string.Empty);
        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(question);
        var freshIntent = deterministic.Intent is ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat or ZaloBotIntent.Help
            ? null
            : deterministic.Intent.ToString();

        var sameIntentFamily = freshIntent is not null && SameIntentFamily(pendingIntent, freshIntent);
        if (sameIntentFamily)
            freshIntent = pendingIntent;

        var confidence = freshIntent is null ? 0 : deterministic.Confidence;
        ZaloTopicSwitchDecision decision;

        // Domain-qualified fresh commands own the turn before broad conversation-level
        // helpers such as IsCancel/IsConfirmation get a chance to consume it. This keeps
        // `hủy reminder` and `chốt slot` available to their deterministic handlers even
        // while an unrelated legacy confirmation is still pending.
        if (!sameIntentFamily && freshIntent is not null && confidence >= .85)
        {
            decision = ZaloTopicSwitchDecision.SwitchToNewIntent;
        }
        // A confirmation state has already collected every mutation argument. It may own
        // only an acknowledgement/cancel or a clearly same-family correction. Arbitrary
        // new chat such as `test` or `100+200` must not be trapped behind a stale preview.
        else if (IsConfirmationBoundary(pendingIntent) &&
                 !ZaloBotIntelligence.IsConfirmation(question) &&
                 !ZaloBotIntelligence.IsCancel(question) &&
                 freshIntent is null)
        {
            decision = ZaloTopicSwitchDecision.SwitchToNewIntent;
        }
        else
        {
            decision = ZaloConversationStateV2Store.DecideTopicSwitch(
                pendingIntent,
                question,
                freshIntent,
                confidence);
        }

        var reason = decision switch
        {
            ZaloTopicSwitchDecision.CancelPending => "explicit_cancel",
            ZaloTopicSwitchDecision.SwitchToNewIntent when freshIntent is not null => "high_confidence_new_operational_intent",
            ZaloTopicSwitchDecision.SwitchToNewIntent => "confirmation_pending_unrelated_turn",
            _ when freshIntent is null => "no_high_confidence_operational_intent",
            _ => "same_intent_family_or_confirmation"
        };
        return new ZaloPendingMigrationDecision(decision, freshIntent, confidence, reason);
    }

    public static bool SameIntentFamily(string? left, string? right)
    {
        var a = NormalizeFamily(left);
        var b = NormalizeFamily(right);
        return a.Length > 0 && a == b;
    }

    private static string RemoveLeadingAddress(string value) =>
        Regex.Replace(
            value,
            @"^\s*@\S+\s*",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

    private static bool IsConfirmationBoundary(string? intent)
    {
        var normalized = Regex.Replace(
            ZaloBotIntelligence.Normalize(intent ?? string.Empty),
            "[^a-z0-9]",
            string.Empty,
            RegexOptions.CultureInvariant);
        return normalized.EndsWith("confirm", StringComparison.Ordinal) ||
               normalized.EndsWith("confirmation", StringComparison.Ordinal);
    }

    private static string NormalizeFamily(string? value)
    {
        var normalized = Regex.Replace(
            ZaloBotIntelligence.Normalize(value ?? string.Empty),
            "[^a-z0-9]",
            string.Empty,
            RegexOptions.CultureInvariant);
        foreach (var suffix in new[] { "confirmation", "confirm" })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                normalized = normalized[..^suffix.Length];
        }

        // AutoDraft and Redraft are different execution intents but belong to one
        // conversational draft workflow. A user who is already confirming a draft
        // and says "draft lại team này" is correcting that workflow, not starting an
        // unrelated topic. Keep other intent families distinct unless explicitly mapped.
        return normalized switch
        {
            "autodraft" or "redraft" => "draft",
            _ => normalized
        };
    }
}
