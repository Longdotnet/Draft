using Microsoft.Extensions.Configuration;

namespace VolleyDraft.Api.Services;

public sealed record ZaloDomainEventNarratorResult(
    bool Eligible,
    bool Sent,
    string? Message,
    string Reason);

/// <summary>
/// Deterministic, read-only narrator for selected authoritative poll-derived domain events.
/// It never creates or changes registration/domain state. Source defaults keep sending disabled.
/// </summary>
public sealed class ZaloDomainEventNarrator(
    IConfiguration configuration,
    ZaloBridgeClient bridgeClient)
{
    public async Task<ZaloDomainEventNarratorResult> HandleAsync(
        string accountId,
        string groupId,
        string sessionId,
        string sessionName,
        ZaloDomainEventShadowDecision decision,
        CancellationToken cancellationToken = default)
    {
        var message = BuildMessage(sessionName, decision);
        if (message is null)
            return new(false, false, null, "event_not_narratable");

        var enabled = configuration.GetValue<bool>("ZaloBot:Ambient:DomainEventPilot:Enabled");
        if (!enabled)
            return new(true, false, message, "pilot_disabled");

        var shadowMode = configuration.GetValue<bool>("ZaloBot:Ambient:ShadowMode", true);
        var sendEnabled = configuration.GetValue<bool>("ZaloBot:Ambient:DomainEventPilot:SendEnabled");
        if (shadowMode || !sendEnabled)
            return new(true, false, message, shadowMode ? "global_shadow_mode" : "send_disabled");

        accountId = Clean(accountId, 100);
        groupId = Clean(groupId, 100);
        sessionId = Clean(sessionId, 100);
        if (accountId.Length == 0 || groupId.Length == 0 || sessionId.Length == 0)
            return new(true, false, message, "missing_provider_scope");

        var key = $"domain-event:{sessionId}:{decision.EventKind}:{decision.BeforeCount}-{decision.AfterCount}:{decision.Capacity}";
        var response = await bridgeClient.SendGroupMessageAsync(
            accountId,
            groupId,
            message,
            [],
            idempotencyKey: key);

        return response.Sent
            ? new(true, true, message, response.Mock ? "mock_sent" : "sent")
            : new(true, false, message, "bridge_not_sent");
    }

    internal static string? BuildMessage(
        string sessionName,
        ZaloDomainEventShadowDecision decision)
    {
        var name = Clean(sessionName, 80);
        if (name.Length == 0) name = "Kèo";

        return decision.EventKind switch
        {
            "RosterFilled" when decision.Capacity > 0 =>
                $"✅ {name} đã đủ {decision.AfterCount}/{decision.Capacity} người theo poll hiện tại.",
            "RosterReopened" when decision.Capacity > 0 =>
                $"📢 {name} vừa trống lại {Math.Max(1, decision.Capacity - decision.AfterCount)} suất ({decision.AfterCount}/{decision.Capacity}) theo poll hiện tại.",
            _ => null
        };
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
