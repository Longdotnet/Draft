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
        var first = "📖 @Hmy Bk kể chút về CLB Tân bình-The First Spike nha: 5 thành viên cốt cán cùng thành lập nhóm để giao lưu bóng chuyền vui vẻ và gắn kết lâu dài.";
        var second = "📖 @Anhkhoii kể chút về CLB Tân bình-The First Spike nha: 5 thành viên cốt cán cùng thành lập nhóm để giao lưu bóng chuyền vui vẻ và gắn kết lâu dài.";

        Assert.True(ZaloMemberIntegrationTextGenerator.Similarity(first, second) > .75);
    }

    [Fact]
    public async Task Failed_ai_uses_least_repetitive_deterministic_variant()
    {
        var context = Context();
        var suggestion = await new ZaloMemberGroupOriginsCapability().EvaluateAsync(context);
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
        Assert.Contains(context.Facts.GroupName, rewritten.Text);
    }

    [Fact]
    public async Task Ai_rewrite_for_group_origins_is_accepted_when_group_name_is_preserved()
    {
        var context = Context();
        var suggestion = await new ZaloMemberGroupOriginsCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        var candidate = $"😄 @New Member kể chút về {context.Facts.GroupName} nha: 5 thành viên cốt cán cùng thành lập nhóm, mỗi người đều góp một vai trò quan trọng, với mong muốn tạo sân chơi bóng chuyền vui vẻ, giao lưu và gắn kết lâu dài. Tinh thần chung là vui vẻ, tôn trọng nhau, không blame hay trách móc.";
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
        var suggestion = await new ZaloMemberGroupOriginsCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        var candidate = $"CLB Tân bình-The First Spike được 5 thành viên cốt cán thành lập để giao lưu vui vẻ.";
        var generator = new ZaloMemberIntegrationTextGenerator(new StubGateway(isConfigured: true, content: candidate), NullLogger.Instance);

        var rewritten = await generator.RewriteAsync(context, suggestion!, []);

        Assert.NotEqual(candidate, rewritten.Text);
        Assert.Contains("@New Member", rewritten.Text);
    }

    [Fact]
    public async Task Ai_rewrite_missing_live_zalo_group_name_falls_back_to_canonical_copy()
    {
        var context = Context();
        var suggestion = await new ZaloMemberGroupOriginsCapability().EvaluateAsync(context);
        Assert.NotNull(suggestion);
        const string candidate = "👋 @New Member nhóm được 5 thành viên cốt cán thành lập để chơi bóng chuyền vui vẻ.";
        var generator = new ZaloMemberIntegrationTextGenerator(
            new StubGateway(isConfigured: true, content: candidate),
            NullLogger.Instance);

        var rewritten = await generator.RewriteAsync(context, suggestion!, []);

        Assert.NotEqual(candidate, rewritten.Text);
        Assert.Contains(context.Facts.GroupName, rewritten.Text);
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
            GroupName: "CLB Tân bình-The First Spike",
            GroupFoundersSummary: "5 thành viên cốt cán",
            GroupPurpose: "tạo sân chơi bóng chuyền vui vẻ, giao lưu và gắn kết lâu dài.",
            CultureSummary: "Vui vẻ, hòa đồng, tôn trọng nhau, không toxic, không blame.",
            NormalCourtFeeVnd: 30000,
            MaxPlayersPerSession: 18,
            ApprovedLore: []),
        PeerCandidates: [],
        LoreHints: []);

    private static ZaloProactiveMessageHistoryData History(string text) => new(
        Id: Guid.NewGuid().ToString("n"),
        ConnectionId: "conn-1",
        GroupId: "group-1",
        LocalDate: "2026-10-26",
        Lane: ZaloProactiveLane.MemberIntegration,
        Kind: "member_integration_group-origins",
        ContentKey: "member-integration:group-origins",
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
