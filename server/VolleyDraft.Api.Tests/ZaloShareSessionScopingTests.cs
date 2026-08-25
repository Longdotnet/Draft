using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareSessionScopingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tomorrow_selector_cannot_be_overridden_by_a_stronger_past_identity_match()
    {
        var candidates = new[]
        {
            Session("past", "T5 cũ", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)),
            Session("tomorrow", "Kèo 26/08", new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)),
            Session("later", "Kèo 27/08", new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero))
        };

        var scoped = ZaloBotService.ScopeShareSessionCandidateIds(candidates, "ngày mai", Now);

        Assert.Equal(["tomorrow"], scoped);
    }

    [Theory]
    [InlineData("26/08")]
    [InlineData("26/08/2026")]
    [InlineData("T4")]
    [InlineData("thứ tư")]
    [InlineData("Kèo 26/08")]
    public void Explicit_date_weekday_and_session_name_scope_before_roster_ranking(string selector)
    {
        var candidates = new[]
        {
            Session("past", "Kèo cũ", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)),
            Session("target", "Kèo 26/08", new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)),
            Session("other", "Kèo 27/08", new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero))
        };

        var scoped = ZaloBotService.ScopeShareSessionCandidateIds(candidates, selector, Now);

        Assert.Equal(["target"], scoped);
    }

    [Fact]
    public void Share_slot_without_selector_never_ranks_already_started_sessions()
    {
        var candidates = new[]
        {
            Session("past", "Đã đánh", Now.AddMinutes(-1)),
            Session("future", "Sắp đánh", Now.AddHours(2)),
            Session("unscheduled", "Chưa chốt giờ", null)
        };

        var scoped = ZaloBotService.ScopeShareSessionCandidateIds(candidates, null, Now);

        Assert.Equal(["future", "unscheduled"], scoped);
    }

    [Fact]
    public void Explicit_selector_with_no_future_match_returns_no_rankable_candidate()
    {
        var candidates = new[]
        {
            Session("past", "Kèo 20/08", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)),
            Session("future", "Kèo 27/08", new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero))
        };

        var scoped = ZaloBotService.ScopeShareSessionCandidateIds(candidates, "26/08", Now);

        Assert.Empty(scoped);
    }

    private static ZaloSessionReference Session(string id, string name, DateTimeOffset? startTime) =>
        new(id, name, startTime);
}
