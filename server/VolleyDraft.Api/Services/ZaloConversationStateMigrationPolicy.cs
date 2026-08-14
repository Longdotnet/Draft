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
/// Ambiguous/general chat remains on the existing pending path.
/// </summary>
public static class ZaloConversationStateMigrationPolicy
{
    public static ZaloPendingMigrationDecision Evaluate(string pendingIntent, string currentQuestion)
    {
        var question = currentQuestion ?? string.Empty;
        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(question);
        var freshIntent = deterministic.Intent is ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat or ZaloBotIntent.Help
            ? null
            : deterministic.Intent.ToString();

        if (freshIntent is not null && SameIntentFamily(pendingIntent, freshIntent))
            freshIntent = pendingIntent;

        var confidence = freshIntent is null ? 0 : deterministic.Confidence;
        var decision = ZaloConversationStateV2Store.DecideTopicSwitch(
            pendingIntent,
            question,
            freshIntent,
            confidence);
        var reason = decision switch
        {
            ZaloTopicSwitchDecision.CancelPending => "explicit_cancel",
            ZaloTopicSwitchDecision.SwitchToNewIntent => "high_confidence_new_operational_intent",
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
