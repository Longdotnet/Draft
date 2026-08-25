using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloContextFirstSemanticInterpreterTests
{
    [Fact]
    public void Draft_semantic_plan_accepts_grounded_natural_decision()
    {
        var sessions = new[]
        {
            new ZaloDraftSemanticSessionSnapshot(
                "session-t5",
                "T5 giao lưu",
                new DateTimeOffset(2026, 8, 27, 19, 0, 0, TimeSpan.FromHours(7)),
                3,
                6,
                null,
                null)
        };

        var plan = ZaloDraftPreparationSemanticInterpreter.ParsePlan(
            """
            {"intent":"PlayCurrentRoster","sessionId":"session-t5","requestedSlotCount":15,"needsClarification":false,"confidence":0.94,"reason":"leader accepts current roster"}
            """,
            sessions);

        Assert.True(plan.IsActionable);
        Assert.Equal(ZaloDraftSemanticIntent.PlayCurrentRoster, plan.Intent);
        Assert.Equal("session-t5", plan.SessionId);
        Assert.Equal(15, plan.RequestedSlotCount);
    }

    [Fact]
    public void Draft_semantic_plan_fails_closed_on_hallucinated_session()
    {
        var sessions = new[]
        {
            new ZaloDraftSemanticSessionSnapshot("real-session", "T5", null, 3, 6, null, null)
        };

        var plan = ZaloDraftPreparationSemanticInterpreter.ParsePlan(
            """
            {"intent":"StartDraft","sessionId":"made-up-session","requestedSlotCount":null,"needsClarification":false,"confidence":0.99,"reason":"go"}
            """,
            sessions);

        Assert.False(plan.IsActionable);
        Assert.Null(plan.SessionId);
        Assert.True(plan.NeedsClarification);
    }

    [Fact]
    public void Profile_semantic_plan_accepts_personal_slang_values()
    {
        var prompts = new[]
        {
            new ZaloProfileSemanticPromptSnapshot(
                "prompt-1",
                "session-t5",
                "T5 giao lưu",
                false,
                true,
                true)
        };

        var plan = ZaloProfileSemanticInterpreter.ParseInterpretation(
            """
            {"route":"ProfileAnswer","sessionId":"session-t5","gender":null,"role":"Attack","level":"Good","needsClarification":false,"confidence":0.92,"reason":"self describes attack role and good level"}
            """,
            prompts);

        Assert.True(plan.IsUseful);
        Assert.Equal("session-t5", plan.SessionId);
        Assert.Equal(PlayerRole.Attack, plan.Role);
        Assert.Equal(PlayerLevel.Good, plan.Level);
        Assert.Null(plan.Gender);
    }

    [Fact]
    public void Profile_semantic_plan_cannot_ground_unknown_session()
    {
        var prompts = new[]
        {
            new ZaloProfileSemanticPromptSnapshot("prompt-1", "session-t5", "T5", true, true, true)
        };

        var plan = ZaloProfileSemanticInterpreter.ParseInterpretation(
            """
            {"route":"ProfileAnswer","sessionId":"session-t7","gender":"Male","role":"Defense","level":"Average","needsClarification":false,"confidence":0.97,"reason":"answer"}
            """,
            prompts);

        Assert.False(plan.IsUseful);
        Assert.Null(plan.SessionId);
        Assert.True(plan.NeedsClarification);
    }

    [Fact]
    public void Profile_non_answer_routes_do_not_smuggle_profile_values()
    {
        var prompts = new[]
        {
            new ZaloProfileSemanticPromptSnapshot("prompt-1", "session-t5", "T5", true, true, true)
        };

        var plan = ZaloProfileSemanticInterpreter.ParseInterpretation(
            """
            {"route":"Defer","sessionId":"session-t5","gender":"Male","role":"Attack","level":"Good","needsClarification":false,"confidence":0.95,"reason":"later"}
            """,
            prompts);

        Assert.True(plan.IsUseful);
        Assert.Equal(ZaloProfileSemanticRoute.Defer, plan.Route);
        Assert.Null(plan.Gender);
        Assert.Null(plan.Role);
        Assert.Null(plan.Level);
    }
}
