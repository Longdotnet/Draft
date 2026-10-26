using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloDirectedAudienceMessageTests
{
    [Fact]
    public async Task Ai_can_turn_follow_up_goal_into_grounded_mentions_without_copying_raw_instruction()
    {
        var gateway = new StubGateway("""
            {
              "route":"Send",
              "confidence":0.97,
              "targetTokens":["AUD_0","AUD_1","AUD_2"],
              "message":"Mấy ông [[AUD_0]], [[AUD_1]], [[AUD_2]] ơi 😄 [[REQUESTER]] nhờ tui réo nhẹ: thấy kèo nào chơi được thì vào vote chung với hội nha.",
              "reason":"relay_vote_invite"
            }
            """);
        var composer = new ZaloDirectedAudienceComposer(
            gateway,
            NullLogger.Instance,
            maxUserCallsPerMinute: 100,
            maxGroupCallsPerMinute: 500);
        var audience = Audience(
            new("u-an", "An"),
            new("u-khoi", "Khôi"),
            new("u-huy", "Huy"));

        var plan = await composer.PlanAsync(
            "conn-" + Guid.NewGuid().ToString("n"),
            "group-1",
            "admin-long",
            "Thanh Long",
            "tag họ giúp tui rồi kêu họ vote chơi giúp tui nhé",
            audience);
        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Thanh Long");

        Assert.Equal(ZaloDirectedAudiencePlanStatus.Ready, plan.Status);
        Assert.NotNull(rendered);
        Assert.Contains("Thanh Long nhờ tui réo nhẹ", rendered!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tag họ giúp tui", rendered.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, rendered.Mentions.Count);
        Assert.Equal(new[] { "u-an", "u-khoi", "u-huy" }, rendered.Mentions.Select(item => item.Uid));
        foreach (var mention in rendered.Mentions)
            Assert.StartsWith("@", rendered.Text.Substring(mention.Pos, mention.Len), StringComparison.Ordinal);
        Assert.Contains("CurrentInstruction", gateway.Requests.Single().Messages.Last().Content, StringComparison.Ordinal);
        Assert.Contains("AUD_2", gateway.Requests.Single().Messages.Last().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_may_choose_a_grounded_subset_from_team_metadata()
    {
        var gateway = new StubGateway("""
            {
              "route":"Send",
              "confidence":0.95,
              "targetTokens":["AUD_1","AUD_2"],
              "message":"[[AUD_1]] với [[AUD_2]] ơi, [[REQUESTER]] nhờ tui báo mai canh tới sớm chút nha 😄",
              "reason":"team_two_subset"
            }
            """);
        var composer = new ZaloDirectedAudienceComposer(gateway, NullLogger.Instance, 100, 500);
        var audience = Audience(
            new("u-a", "A", "team:1", "Team 1"),
            new("u-b", "B", "team:2", "Team 2"),
            new("u-c", "C", "team:2", "Team 2"));

        var plan = await composer.PlanAsync(
            "conn-" + Guid.NewGuid().ToString("n"),
            "group-1",
            "admin-long",
            "Long",
            "tag team 2 rồi nói mai đi sớm giúp tui",
            audience);
        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Long");

        Assert.Equal(new[] { 1, 2 }, plan.TargetIndexes);
        Assert.NotNull(rendered);
        Assert.Equal(new[] { "u-b", "u-c" }, rendered!.Mentions.Select(item => item.Uid));
        Assert.DoesNotContain("@A", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hallucinated_recipient_token_is_rejected_before_any_mention_can_be_built()
    {
        var plan = ZaloDirectedAudienceComposer.ParsePlan(
            """
            {"route":"Send","confidence":0.99,"targetTokens":["AUD_99"],"message":"[[REQUESTER]] nhờ [[AUD_99]] nha","reason":"bad"}
            """,
            audienceCount: 2);

        Assert.Equal(ZaloDirectedAudiencePlanStatus.Rejected, plan.Status);
        Assert.Empty(plan.TargetIndexes);
    }

    [Fact]
    public void Unrelated_follow_up_is_left_for_normal_routing()
    {
        var plan = ZaloDirectedAudienceComposer.ParsePlan(
            """
            {"route":"None","confidence":0.96,"targetTokens":[],"message":"","reason":"new_topic"}
            """,
            audienceCount: 3);

        Assert.Equal(ZaloDirectedAudiencePlanStatus.None, plan.Status);
    }

    [Fact]
    public async Task Audience_store_is_sender_scoped_and_empty_result_clears_stale_reference()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloAudienceReferenceStore(db);
        var draft = new ZaloAudienceReferenceDraft(
            "recent-members",
            "thành viên mới 7 ngày",
            [new("u-an", "An"), new("u-khoi", "Khôi")]);

        await store.RememberAsync("conn-1", "g1", "admin-1", "m-list", draft);

        var loaded = await store.LoadActiveAsync("conn-1", "g1", "admin-1");
        Assert.NotNull(loaded);
        Assert.Equal(new[] { "u-an", "u-khoi" }, loaded!.Targets.Select(item => item.ZaloUserId));
        Assert.Null(await store.LoadActiveAsync("conn-1", "g1", "admin-2"));

        await store.RememberAsync(
            "conn-1",
            "g1",
            "admin-1",
            "m-empty",
            new ZaloAudienceReferenceDraft("recent-members", "thành viên mới 7 ngày", []));

        Assert.Null(await store.LoadActiveAsync("conn-1", "g1", "admin-1"));
    }

    private static ZaloAudienceReferenceSnapshot Audience(params ZaloAudienceReferenceTarget[] targets) => new(
        "ref-1",
        "conn-1",
        "group-1",
        "admin-long",
        "test",
        "danh sách vừa rồi",
        "source-1",
        targets,
        DateTimeOffset.UtcNow.AddMinutes(15),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class StubGateway(string content) : IZaloAiGateway
    {
        public bool IsConfigured => true;
        public List<ZaloAiCompletionRequest> Requests { get; } = [];

        public Task<ZaloAiCompletionResult> CompleteAsync(
            ZaloAiCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ZaloAiCompletionResult(
                true,
                content,
                ZaloAiFailureKind.None,
                "stub",
                "stub-model",
                1,
                200,
                TimeSpan.Zero,
                false));
        }
    }
}
