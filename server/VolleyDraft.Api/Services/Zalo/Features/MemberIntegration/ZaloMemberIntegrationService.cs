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
    private readonly ZaloMemberIntegrationContextBuilder contextBuilder = new(db, bridge, configuration);
    private readonly ZaloMemberIntegrationCoordinator coordinator = new(CreateCapabilities());
    private readonly ZaloMemberIntegrationDeliverySettings deliverySettings =
        ZaloMemberIntegrationDeliverySettings.FromConfiguration(configuration);

    public async Task<IReadOnlyList<ZaloMemberIntegrationSelection>> BuildAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var contexts = await BuildContextsAsync(connectionId, groupId, now, cancellationToken);
        return await BuildFromContextsAsync(contexts, proactiveHistory, now, cancellationToken);
    }

    public Task<IReadOnlyList<ZaloMemberIntegrationContext>> BuildContextsAsync(
        string connectionId,
        string groupId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        contextBuilder.BuildRecentAsync(connectionId, groupId, now, cancellationToken);

    public async Task<IReadOnlyList<ZaloMemberIntegrationSelection>> BuildFromContextsAsync(
        IReadOnlyList<ZaloMemberIntegrationContext> contexts,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (contexts.Count == 0) return [];

        var result = new List<ZaloMemberIntegrationSelection>();
        foreach (var context in contexts.OrderBy(item => item.JoinedAt))
        {
            var memberHistory = proactiveHistory
                .Where(item => (item.Lane == ZaloProactiveLane.MemberIntegration ||
                                item.Lane == ZaloProactiveLane.MemberIntegrationRecipient ||
                                item.Lane == ZaloProactiveLane.Community) &&
                               item.SubjectUserId == context.ZaloUserId &&
                               item.Kind.StartsWith("member_integration_", StringComparison.Ordinal))
                .OrderByDescending(item => item.SentAt)
                .ToList();

            if (ZaloMemberIntegrationLifecyclePolicy.HasCurrentPeriodGraduation(context, memberHistory))
                continue;

            var lifecycle = ZaloMemberIntegrationLifecyclePolicy.Evaluate(context, deliverySettings);
            if (lifecycle.ShouldAnnounceGraduation)
            {
                result.Add(new ZaloMemberIntegrationSelection(
                    context,
                    ZaloMemberIntegrationLifecyclePolicy.BuildGraduationSuggestion(context)));
                continue;
            }
            if (lifecycle.StopOnboarding)
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
        if (suggestion.CapabilityKey == "welcome")
            return matching.All(item => !string.Equals(item.ContentKey, suggestion.ContentKey, StringComparison.Ordinal));

        var cooldown = suggestion.CapabilityKey switch
        {
            "group-origins" => TimeSpan.FromDays(14),
            "group-culture" => TimeSpan.FromDays(10),
            "practical-guide" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(5)
        };
        var sameContent = matching
            .Where(item => string.Equals(item.ContentKey, suggestion.ContentKey, StringComparison.Ordinal))
            .OrderByDescending(item => item.SentAt)
            .FirstOrDefault();
        return sameContent is null || now - sameContent.SentAt >= cooldown;
    }

    private static IZaloMemberIntegrationCapability[] CreateCapabilities() =>
    [
        new ZaloMemberWelcomeCapability(),
        new ZaloMemberGroupOriginsCapability(),
        new ZaloMemberGroupCultureCapability(),
        new ZaloMemberPracticalGuideCapability(),
        new ZaloMemberSocialBridgeCapability(),
        new ZaloMemberGroupLoreCapability(),
        new ZaloMemberIntegrationScoreCapability()
    ];
}
