using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationCoordinatorTests
{
    [Fact]
    public async Task Evaluate_orders_capabilities_by_priority_then_ordinal_key()
    {
        var calls = new List<string>();
        var coordinator = new ZaloMemberIntegrationCoordinator(
        [
            Capability("z-low", 10, calls),
            Capability("z-high", 20, calls),
            Capability("a-high", 20, calls)
        ]);

        var plan = await coordinator.EvaluateAsync(Context());

        Assert.Equal(new[] { "a-high", "z-high", "z-low" }, calls);
        Assert.Equal(new[] { "a-high", "z-high", "z-low" }, plan.Suggestions.Select(suggestion => suggestion.CapabilityKey));
    }

    [Fact]
    public void Constructor_rejects_duplicate_capability_keys()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ZaloMemberIntegrationCoordinator(
            [
                Capability("welcome", 100),
                Capability("welcome", 10)
            ]));

        Assert.Contains("Duplicate member-integration capability key 'welcome'", exception.Message);
    }

    [Fact]
    public async Task Evaluate_rejects_suggestion_with_mismatched_capability_key()
    {
        var capability = new StubCapability(
            key: "welcome",
            priority: 100,
            evaluate: (_, _) => ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(Suggestion("group-culture", 100)));
        var coordinator = new ZaloMemberIntegrationCoordinator([capability]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EvaluateAsync(Context()));

        Assert.Contains("capability 'welcome' returned suggestion key 'group-culture'", exception.Message);
    }

    [Fact]
    public async Task Evaluate_omits_null_suggestions_and_preserves_original_context()
    {
        var context = Context();
        ZaloMemberIntegrationContext? observedContext = null;
        var coordinator = new ZaloMemberIntegrationCoordinator(
        [
            new StubCapability(
                "silent",
                100,
                (candidate, _) =>
                {
                    observedContext = candidate;
                    return ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(null);
                }),
            Capability("guide", 50)
        ]);

        var plan = await coordinator.EvaluateAsync(context);

        Assert.Same(context, observedContext);
        Assert.Same(context, plan.Context);
        Assert.Single(plan.Suggestions);
        Assert.Equal("guide", plan.Suggestions[0].CapabilityKey);
    }

    [Fact]
    public async Task Evaluate_passes_cancellation_token_to_capability()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observedToken = default;
        var coordinator = new ZaloMemberIntegrationCoordinator(
        [
            new StubCapability(
                "welcome",
                100,
                (_, token) =>
                {
                    observedToken = token;
                    return ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(Suggestion("welcome", 100));
                })
        ]);

        await coordinator.EvaluateAsync(Context(), cancellation.Token);

        Assert.Equal(cancellation.Token, observedToken);
    }

    [Fact]
    public async Task Evaluate_stops_before_next_capability_when_token_is_cancelled_between_evaluations()
    {
        using var cancellation = new CancellationTokenSource();
        var secondWasCalled = false;
        var coordinator = new ZaloMemberIntegrationCoordinator(
        [
            new StubCapability(
                "first",
                100,
                (_, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(Suggestion("first", 100));
                }),
            new StubCapability(
                "second",
                50,
                (_, _) =>
                {
                    secondWasCalled = true;
                    return ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(Suggestion("second", 50));
                })
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.EvaluateAsync(Context(), cancellation.Token));

        Assert.False(secondWasCalled);
    }

    [Fact]
    public async Task Evaluate_throws_for_null_context()
    {
        var coordinator = new ZaloMemberIntegrationCoordinator([]);

        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.EvaluateAsync(null!));
    }

    private static IZaloMemberIntegrationCapability Capability(
        string key,
        int priority,
        List<string>? calls = null) =>
        new StubCapability(
            key,
            priority,
            (_, _) =>
            {
                calls?.Add(key);
                return ValueTask.FromResult<ZaloMemberIntegrationSuggestion?>(Suggestion(key, priority));
            });

    private static ZaloMemberIntegrationSuggestion Suggestion(string key, int priority) =>
        new(
            CapabilityKey: key,
            Priority: priority,
            Text: $"{key} text",
            ContentKey: $"{key}:content",
            Reason: $"{key} reason",
            MentionTargets: []);

    private static ZaloMemberIntegrationContext Context() =>
        new(
            ZaloConnectionId: "conn-1",
            AccountId: "account-1",
            GroupId: "group-1",
            ZaloUserId: "user-1",
            DisplayName: "New member",
            JoinedAt: DateTimeOffset.Parse("2026-10-25T01:00:00+07:00"),
            IsRejoin: false,
            SnapshotAt: DateTimeOffset.Parse("2026-10-25T02:00:00+07:00"),
            Signals: new ZaloMemberIntegrationSignals(MessageCount: 1, ActiveMessageDays: 1, HasSpoken: true),
            Facts: new ZaloMemberIntegrationGroupFacts(
                GroupName: "Volley Draft",
                BotCreatorName: "Thanh Long",
                GroupFounderName: "Nick Tran",
                GroupPurpose: "Giao lưu vui vẻ",
                CultureSummary: "Vui vẻ, hòa đồng, không toxic hay blame",
                NormalCourtFeeVnd: null,
                MaxPlayersPerSession: 12,
                ApprovedLore: []),
            Roles: new ZaloMemberIntegrationRoleDirectory(Creator: null, Admins: []),
            PeerCandidates: [],
            LoreHints: []);

    private sealed class StubCapability(
        string key,
        int priority,
        Func<ZaloMemberIntegrationContext, CancellationToken, ValueTask<ZaloMemberIntegrationSuggestion?>> evaluate)
        : IZaloMemberIntegrationCapability
    {
        public string Key { get; } = key;

        public int Priority { get; } = priority;

        public ValueTask<ZaloMemberIntegrationSuggestion?> EvaluateAsync(
            ZaloMemberIntegrationContext context,
            CancellationToken cancellationToken = default) => evaluate(context, cancellationToken);
    }
}
