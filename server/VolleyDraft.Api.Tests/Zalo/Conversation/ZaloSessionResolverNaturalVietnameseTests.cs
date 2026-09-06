using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSessionResolverNaturalVietnameseTests
{
    private static readonly DateTimeOffset NewYearsEve =
        new(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours(7));

    [Theory]
    [InlineData("trận mai mấy giờ?")]
    [InlineData("kèo mai")]
    [InlineData("tối mai")]
    [InlineData("mai 0 giờ 30")]
    public void Qualified_tomorrow_language_crosses_year_boundary_in_vietnam_time(string text)
    {
        var sessions = new List<ZaloSessionReference>
        {
            new("today", "Kèo cuối năm", new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.FromHours(7))),
            new("tomorrow", "Kèo đầu năm", new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(7)))
        };

        var result = ZaloSessionResolver.Resolve(text, sessions, NewYearsEve);

        Assert.True(result.HasExplicitSelector);
        Assert.Equal(["tomorrow"], result.CandidateIds);
    }

    [Theory]
    [InlineData("01/01 17 giờ 30")]
    [InlineData("01/01 17g30")]
    [InlineData("01/01 17h30")]
    [InlineData("01/01 17:30")]
    public void Vietnamese_clock_forms_disambiguate_sessions_on_the_same_date(string text)
    {
        var sessions = new List<ZaloSessionReference>
        {
            new("early", "Kèo chiều", new DateTimeOffset(2027, 1, 1, 17, 30, 0, TimeSpan.FromHours(7))),
            new("late", "Kèo tối", new DateTimeOffset(2027, 1, 1, 19, 0, 0, TimeSpan.FromHours(7)))
        };

        var result = ZaloSessionResolver.Resolve(text, sessions, NewYearsEve);

        Assert.True(result.IsExact);
        Assert.Equal(["early"], result.CandidateIds);
    }

    [Fact]
    public void Tomorrow_with_vietnamese_clock_disambiguates_multiple_tomorrow_sessions()
    {
        var sessions = new List<ZaloSessionReference>
        {
            new("early", "Kèo sớm", new DateTimeOffset(2027, 1, 1, 17, 30, 0, TimeSpan.FromHours(7))),
            new("late", "Kèo muộn", new DateTimeOffset(2027, 1, 1, 19, 0, 0, TimeSpan.FromHours(7)))
        };

        var result = ZaloSessionResolver.Resolve("mai 17 giờ 30", sessions, NewYearsEve);

        Assert.True(result.IsExact);
        Assert.Equal(["early"], result.CandidateIds);
    }

    [Theory]
    [InlineData("Mai chơi không?")]
    [InlineData("Mai có đi không?")]
    [InlineData("Mai vote chưa?")]
    public void Person_named_mai_is_not_silently_reinterpreted_as_tomorrow(string text)
    {
        var sessions = new List<ZaloSessionReference>
        {
            new("tomorrow", "Kèo đầu năm", new DateTimeOffset(2027, 1, 1, 17, 30, 0, TimeSpan.FromHours(7)))
        };

        var result = ZaloSessionResolver.Resolve(text, sessions, NewYearsEve);

        Assert.False(result.HasExplicitSelector);
        Assert.Empty(result.CandidateIds);
        Assert.False(ZaloSessionResolver.LooksLikeSelector(text));
    }

    [Theory]
    [InlineData("mai đánh lúc 17 giờ 30")]
    [InlineData("mai chơi vào 17g30")]
    [InlineData("mai đánh mấy giờ")]
    public void Schedule_shaped_mai_play_phrases_are_treated_as_tomorrow(string text)
    {
        var sessions = new List<ZaloSessionReference>
        {
            new("tomorrow", "Kèo đầu năm", new DateTimeOffset(2027, 1, 1, 17, 30, 0, TimeSpan.FromHours(7)))
        };

        var result = ZaloSessionResolver.Resolve(text, sessions, NewYearsEve);

        Assert.True(result.HasExplicitSelector);
        Assert.Equal(["tomorrow"], result.CandidateIds);
    }
}