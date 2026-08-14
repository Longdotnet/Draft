using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationV2EvalTests
{
    [Fact]
    public void Eval_argument_correction_keeps_pending_intent_when_user_only_changes_session_value()
    {
        var decision = ZaloConversationStateV2Store.DecideTopicSwitch(
            "Register",
            "CN đi",
            "Register",
            .98);

        Assert.Equal(ZaloTopicSwitchDecision.ContinuePending, decision);
    }

    [Fact]
    public void Eval_new_operational_question_escapes_stale_confirmation()
    {
        var decision = ZaloConversationStateV2Store.DecideTopicSwitch(
            "AutoDraftConfirm",
            "T6 còn slot không?",
            "MissingSlots",
            .98);

        Assert.Equal(ZaloTopicSwitchDecision.SwitchToNewIntent, decision);
    }

    [Fact]
    public void Eval_ambiguous_alias_requires_clarification_not_mutation()
    {
        var people = new[]
        {
            new ZaloIdentityCandidate("zalo:1", "1", "Long Nguyễn", "p1", ["Long"]),
            new ZaloIdentityCandidate("zalo:2", "2", "Long Trần", "p2", ["Long"])
        };

        var result = ZaloIdentityResolver.ResolveCandidates("Long", people);

        Assert.Equal(ZaloIdentityResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.PersonKey);
    }

    [Fact]
    public void Eval_quote_person_beats_display_name_guessing()
    {
        var people = new[]
        {
            new ZaloIdentityCandidate("zalo:long", "long", "Long", "p1", []),
            new ZaloIdentityCandidate("zalo:tung", "tung", "Tùng", "p2", [])
        };
        var quote = new ZaloQuotedSemanticContext(
            "quoted-1", "tung", "Tùng", "Tui đánh T6", "chat", null,
            RepliesToBot: false,
            RefersToQuotedPerson: true,
            RefersToQuotedObject: false);

        var result = ZaloIdentityResolver.ResolveCandidates("ông này", people, quotedContext: quote);

        Assert.Equal("tung", result.ZaloUserId);
        Assert.Equal("quoted_sender_uid", result.Source);
    }
}
