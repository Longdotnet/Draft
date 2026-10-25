using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloMemberIntegrationSelection(
    ZaloMemberIntegrationContext Context,
    ZaloMemberIntegrationSuggestion Suggestion);

internal sealed class ZaloMemberIntegrationService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration)
{
    private static readonly TimeSpan PerMemberSpacing = TimeSpan.FromDays(2);
    private readonly ZaloMemberIntegrationContextBuilder contextBuilder = new(db, bridge, configuration);
    private readonly ZaloMemberIntegrationCoordinator coordinator = new(CreateCapabilities());

    public async Task<IReadOnlyList<ZaloMemberIntegrationSelection>> BuildAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var contexts = await contextBuilder.BuildRecentAsync(connectionId, groupId, now, cancellationToken);
        if (contexts.Count == 0) return [];

        var result = new List<ZaloMemberIntegrationSelection>();
        foreach (var context in contexts.OrderBy(item => item.JoinedAt))
        {
            var memberHistory = proactiveHistory
                .Where(item => item.Lane == ZaloProactiveLane.Community &&
                               item.SubjectUserId == context.ZaloUserId &&
                               item.Kind.StartsWith("member_integration_", StringComparison.Ordinal))
                .OrderByDescending(item => item.SentAt)
                .ToList();
            if (memberHistory.FirstOrDefault() is { } last && now - last.SentAt < PerMemberSpacing)
                continue;

            var plan = await coordinator.EvaluateAsync(context, cancellationToken);
            var suggestion = plan.Suggestions
                .Where(item => IsHistoryEligible(item, memberHistory, now))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.CapabilityKey, StringComparer.Ordinal)
                .FirstOrDefault();
            if (suggestion is not null)
                result.Add(new ZaloMemberIntegrationSelection(context, suggestion));
        }
        return result;
    }

    private static bool IsHistoryEligible(
        ZaloMemberIntegrationSuggestion suggestion,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history,
        DateTimeOffset now)
    {
        var kind = $"member_integration_{suggestion.CapabilityKey}";
        var matching = history.Where(item => item.Kind == kind).ToList();
        if (matching.Any(item => item.ContentKey == suggestion.ContentKey)) return false;
        if (suggestion.CapabilityKey == "welcome") return true;

        var cooldown = suggestion.CapabilityKey switch
        {
            "bot-identity" or "group-origins" or "role-introduction" => TimeSpan.FromDays(14),
            "group-culture" => TimeSpan.FromDays(10),
            "practical-guide" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(5)
        };
        return matching.Count == 0 || now - matching.Max(item => item.SentAt) >= cooldown;
    }

    private static IZaloMemberIntegrationCapability[] CreateCapabilities() =>
    [
        new ZaloMemberWelcomeCapability(),
        new ZaloMemberBotIdentityCapability(),
        new ZaloMemberGroupOriginsCapability(),
        new ZaloMemberRoleIntroductionCapability(),
        new ZaloMemberGroupCultureCapability(),
        new ZaloMemberPracticalGuideCapability(),
        new ZaloMemberSocialBridgeCapability(),
        new ZaloMemberGroupLoreCapability(),
        new ZaloMemberIntegrationScoreCapability()
    ];
}
