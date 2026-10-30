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
    public async Task Ai_can_handle_short_tag_those_people_bring_ball_instruction()
    {
        var gateway = new StubGateway("""
            {
              "route":"Send",
              "confidence":0.98,
              "targetTokens":["AUD_0","AUD_1"],
              "message":"[[AUD_0]] [[AUD_1]] tối nay nhớ xách banh theo cứu hội nha 🏐😆 [[REQUESTER]] réo đó.",
              "reason":"remind_roster_bring_ball"
            }
            """);
        var composer = new ZaloDirectedAudienceComposer(gateway, NullLogger.Instance, 100, 500);
        var audience = Audience(
            new("u-bao", "Bảo Long"),
            new("u-chu", "Chu Thuận Thành"));

        var plan = await composer.PlanAsync(
            "conn-" + Guid.NewGuid().ToString("n"),
            "group-1",
            "admin-long",
            "Thanh Long",
            "tag mấy người đó nhớ đem banh",
            audience);
        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Thanh Long");

        Assert.Equal(ZaloDirectedAudiencePlanStatus.Ready, plan.Status);
        Assert.NotNull(rendered);
        Assert.Equal(new[] { "u-bao", "u-chu" }, rendered!.Mentions.Select(item => item.Uid));
        Assert.Contains("nhớ xách banh", rendered.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tag mấy người đó", rendered.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(gateway.Requests.Single().UseAnswerBudget);
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
    public void Explicit_all_audience_command_falls_back_to_all_grounded_targets_when_ai_rejects()
    {
        var rejected = new ZaloDirectedAudiencePlan(
            ZaloDirectedAudiencePlanStatus.Rejected,
            .51,
            [],
            null,
            "low_confidence_or_invalid_route");
        var audience = Audience(
            new("u-bao", "Bảo Long"),
            new("u-chu", "Chu Thuận Thành"),
            new("u-long", "Thanh Long"));

        var plan = ZaloDirectedAudienceComposer.PreserveExplicitAllAudienceIntent(
            rejected,
            "tag mấy người đó nhớ đem banh",
            audience.Targets.Count);
        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Thanh Long");

        Assert.Equal(ZaloDirectedAudiencePlanStatus.Ready, plan.Status);
        Assert.Equal("explicit_all_audience_fallback", plan.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, plan.TargetIndexes);
        Assert.NotNull(rendered);
        Assert.Equal(new[] { "u-bao", "u-chu", "u-long" }, rendered!.Mentions.Select(item => item.Uid));
        Assert.Contains("nhớ đem banh", rendered.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tag mấy người đó kêu họ đem banh hiểu chưa", "đem banh")]
    [InlineData("nhắn họ tối nay nhớ đem banh", "tối nay nhớ đem banh")]
    [InlineData("tui réo cả danh sách vừa rồi mai đi sớm", "mai đi sớm")]
    public void Explicit_all_audience_parser_keeps_only_relay_payload(string instruction, string expected)
    {
        Assert.True(ZaloDirectedAudienceComposer.TryExtractExplicitAllAudienceRelay(instruction, out var relay));
        Assert.Equal(expected, relay);
    }

    [Fact]
    public void Explicit_all_audience_command_keeps_valid_ai_plan_when_ai_already_selected_everyone()
    {
        var aiPlan = new ZaloDirectedAudiencePlan(
            ZaloDirectedAudiencePlanStatus.Ready,
            .98,
            [0, 1],
            "[[AUD_0]] [[AUD_1]] ơi, [[REQUESTER]] nhờ tui nhắc nhớ đem banh nha.",
            "ai_ready");

        var resolved = ZaloDirectedAudienceComposer.PreserveExplicitAllAudienceIntent(
            aiPlan,
            "tag mấy người đó nhớ đem banh",
            2);

        Assert.Same(aiPlan, resolved);
    }

    [Fact]
    public void Render_keeps_unresolved_target_as_plain_text_and_mentions_resolved_targets()
    {
        var plan = new ZaloDirectedAudiencePlan(
            ZaloDirectedAudiencePlanStatus.Ready,
            .99,
            [0, 1, 2],
            "[[AUD_0]] [[AUD_1]] [[AUD_2]] ơi, [[REQUESTER]] nhờ tui nhắc nhớ đem banh nha.",
            "ready");
        var audience = Audience(
            new(null, "Bạn của Hmy Bk #1"),
            new("u-bao", "Bảo Long"),
            new("u-chu", "Chu Thuận Thành"));

        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Thanh Long");

        Assert.NotNull(rendered);
        Assert.Contains("Bạn của Hmy Bk #1", rendered!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Bạn của Hmy Bk #1", rendered.Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "u-bao", "u-chu" }, rendered.Mentions.Select(item => item.Uid));
    }

    [Fact]
    public void Render_can_keep_all_unresolved_targets_as_plain_text_without_mentions()
    {
        var plan = new ZaloDirectedAudiencePlan(
            ZaloDirectedAudiencePlanStatus.Ready,
            .99,
            [0, 1],
            "[[AUD_0]] [[AUD_1]] ơi, [[REQUESTER]] nhờ tui nhắc nhớ đem banh nha.",
            "ready");
        var audience = Audience(
            new(null, "Bạn của Hmy Bk #1"),
            new(null, "Bạn của Hmy Bk #2"));

        var rendered = ZaloDirectedAudienceComposer.Render(plan, audience.Targets, "Thanh Long");

        Assert.NotNull(rendered);
        Assert.Empty(rendered!.Mentions);
        Assert.Contains("Bạn của Hmy Bk #1", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("Bạn của Hmy Bk #2", rendered.Text, StringComparison.Ordinal);
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
