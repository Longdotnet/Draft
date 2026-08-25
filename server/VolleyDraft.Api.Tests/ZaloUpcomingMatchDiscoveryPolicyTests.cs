using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloUpcomingMatchDiscoveryPolicyTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public void Thursday_match_does_not_prompt_before_tuesday_noon()
    {
        var match = Local(2026, 8, 27, 19, 0);
        var now = Local(2026, 8, 25, 11, 59);

        Assert.False(ZaloUpcomingMatchDiscoveryPolicy.IsDue(now, match));
    }

    [Fact]
    public void Thursday_match_prompts_from_tuesday_noon()
    {
        var match = Local(2026, 8, 27, 19, 0);
        var now = Local(2026, 8, 25, 12, 0);

        Assert.True(ZaloUpcomingMatchDiscoveryPolicy.IsDue(now, match));
    }

    [Fact]
    public void Missed_noon_window_is_recovered_later_instead_of_becoming_silent()
    {
        var match = Local(2026, 8, 27, 19, 0);
        var now = Local(2026, 8, 26, 9, 0);

        Assert.True(ZaloUpcomingMatchDiscoveryPolicy.IsDue(now, match));
    }

    [Fact]
    public void Discovery_never_prompts_after_match_start()
    {
        var match = Local(2026, 8, 27, 19, 0);
        var now = Local(2026, 8, 27, 19, 1);

        Assert.False(ZaloUpcomingMatchDiscoveryPolicy.IsDue(now, match));
    }

    [Theory]
    [InlineData("anonymous_poll")]
    [InlineData("closed_poll")]
    [InlineData("no_current_schedule_option")]
    [InlineData("poll_creator_is_not_group_organizer")]
    [InlineData("all_schedule_options_already_linked")]
    [InlineData("preview_only:rule_score=0.9")]
    [InlineData("upcoming_discovery:rule_score=0.4")]
    public void Unsafe_or_already_handled_ignored_reasons_are_not_rescued(string reason)
    {
        Assert.False(ZaloUpcomingMatchDiscoveryPolicy.IsRecoverableIgnoredReason(reason));
    }

    [Theory]
    [InlineData("rule_score=0.45;ai_not_configured")]
    [InlineData("ai:false:generic_weekly_poll")]
    [InlineData("rule_score=0.60;ai_http_error")]
    public void Classifier_misses_are_eligible_for_human_confirmation(string reason)
    {
        Assert.True(ZaloUpcomingMatchDiscoveryPolicy.IsRecoverableIgnoredReason(reason));
    }

    [Fact]
    public void Prompt_is_action_first_and_explains_missing_session_without_web_status_labels()
    {
        var now = Local(2026, 8, 25, 12, 0);
        var t5 = new ZaloAutoSessionCandidate("opt-t5", "T5 19h", "T5", Local(2026, 8, 27, 19, 0), 7);
        var t7 = new ZaloAutoSessionCandidate("opt-t7", "T7 18h", "T7", Local(2026, 8, 29, 18, 0), 3);

        var prompt = ZaloUpcomingMatchDiscoveryPolicy.BuildPrompt(
            "Tuần này đánh ngày nào?",
            [t5, t7],
            t5,
            18,
            now);

        Assert.Contains("T5 còn 2 ngày", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa thấy trận tương ứng được tạo", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T7", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reply tin này", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CẦN WEBSITE", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KHÔNG CẦN WEBSITE", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rescue_reason_keeps_original_classifier_evidence()
    {
        var value = ZaloUpcomingMatchDiscoveryPolicy.MarkRescuedReason("rule_score=0.55;ai_empty");

        Assert.Equal("upcoming_discovery:rule_score=0.55;ai_empty", value);
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, VietnamOffset);
}
