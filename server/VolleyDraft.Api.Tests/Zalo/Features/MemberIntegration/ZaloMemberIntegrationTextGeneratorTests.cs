using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationTextGeneratorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T14:00:00+07:00");

    [Fact]
    public void Similarity_detects_same_template_with_different_member_name()
    {
        var first = "📖 @Hmy Bk kể chút lịch sử hội nha: Nick Tran lập group này. Group được tạo để mọi người giao lưu bóng chuyền vui vẻ.";
        var second = "📖 @Anhkhoii kể chút lịch sử hội nha: Nick Tran lập group này. Group được tạo để mọi người giao lưu bóng chuyền vui vẻ.";

        Assert.True(ZaloMemberIntegrationTextGenerator.Similarity(first, second) > .75);
    }

    [Fact]
    public async Task Failed_ai_uses_least_repetitive_deterministic_variant()
    {
        var context = Context();
        var suggestion = await new ZaloMemberBotIdentityCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        Assert.NotNull(suggestion!.TextVariants);
        Assert.True(suggestion.TextVariants!.Count >= 3);

        var recent = new[] { History(suggestion.Text) };
        var generator = new ZaloMemberIntegrationTextGenerator(
            new StubGateway(isConfigured: false, content: null),
            NullLogger.Instance);

        var rewritten = await generator.RewriteAsync(context, suggestion, recent);

        Assert.NotEqual(suggestion.Text, rewritten.Text);
        Assert.Contains("@New Member", rewritten.Text);
        Assert.Contains("Thanh Long", rewritten.Text);
    }

    [Fact]
    public async Task Ai_rewrite_is_accepted_when_mentions_and_authoritative_name_are_preserved()
    {
        var context = Context();
        var suggestion = await new ZaloMemberBotIdentityCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        const string candidate = "😄 @New Member nếu thắc mắc tui là ai thì: tui là NPC do Thanh Long làm để phụ group. Kèo, vote hay share slot cần nhắc thì cứ réo tui.";
        var generator = new ZaloMemberIntegrationTextGenerator(
            new StubGateway(isConfigured: true, content: candidate),
            NullLogger.Instance);

        var rewritten = await generator.RewriteAsync(context, suggestion!, []);

        Assert.Equal(candidate, rewritten.Text);
    }

    [Fact]
    public async Task Ai_rewrite_missing_required_mention_falls_back()
    {
        var context = Context();
        var suggestion = await new ZaloMemberBotIdentityCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        var generator = new ZaloMemberIntegrationTextGenerator(
            new StubGateway(isConfigured: true, content: "Tui là NPC do Thanh Long làm để phụ group, cần gì cứ gọi nha 😄"),
            NullLogger.Instance);

        var rewritten = await generator.RewriteAsync(context, suggestion!, []);

        Assert.NotEqual("Tui là NPC do Thanh Long làm để phụ group, cần gì cứ gọi nha 😄", rewritten.Text);
        Assert.Contains("@New Member", rewritten.Text);
    }

    private static ZaloMemberIntegrationContext Context() => new(
        ZaloConnectionId: "conn-1",
        AccountId: "account-1",
        GroupId: "group-1",
        ZaloUserId: "user-1",
        DisplayName: "New Member",
        JoinedAt: Now.AddDays(-1),
        IsRejoin: false,
        SnapshotAt: Now,
        Signals: new ZaloMemberIntegrationSignals(),
        Facts: new ZaloMemberIntegrationGroupFacts(
            GroupName: "The First Spike",
            BotCreatorName: "Thanh Long",
            GroupFounderName: "Nick Tran",
            GroupPurpose: "Giao lưu bóng chuyền vui vẻ.",
            CultureSummary: "Vui vẻ, hòa đồng.",
            NormalCourtFeeVnd: 28000,
            MaxPlayersPerSession: 18,
            ApprovedLore: []),
        Roles: new ZaloMemberIntegrationRoleDirectory(null, []),
        PeerCandidates: [],
        LoreHints: []);

    private static ZaloProactiveMessageHistoryData History(string text) => new(
        Id: Guid.NewGuid().ToString("n"),
        ConnectionId: "conn-1",
        GroupId: "group-1",
        LocalDate: "2026-10-26",
        Lane: ZaloProactiveLane.MemberIntegration,
        Kind: "member_integration_bot-identity",
        ContentKey: "member-integration:bot-identity",
        SubjectUserId: "someone-else",
        SubjectName: "Someone Else",
        MessageText: text,
        SentAt: Now.AddMinutes(-30),
        ProviderMessageId: "msg-1",
        IdempotencyKey: "key-1");

    private sealed class StubGateway(bool isConfigured, string? content) : IZaloAiGateway
    {
        public bool IsConfigured => isConfigured;

        public Task<ZaloAiCompletionResult> CompleteAsync(
            ZaloAiCompletionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ZaloAiCompletionResult(
                Success: true,
                Content: content,
                FailureKind: ZaloAiFailureKind.None,
                Provider: "test",
                Model: "test",
                Attempts: 1,
                StatusCode: 200,
                Duration: TimeSpan.Zero,
                UsedFallback: false));
    }
}
