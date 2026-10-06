using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamRelationshipLanguageTests
{
    [Theory]
    [InlineData("đừng xếp tui chung team với @To An nhen")]
    [InlineData("tui k muốn chung team với @To An")]
    [InlineData("né @To An dùm tui")]
    [InlineData("tách tui với @To An nha")]
    [InlineData("@To An team nào tui team khác")]
    [InlineData("@To An bên nào tui bên kia")]
    [InlineData("khỏi cho tui dính team @To An")]
    [InlineData("don't put me on the same team as @To An")]
    [InlineData("keep me separate from @To An")]
    [InlineData("today tui don't wanna same team with @To An")]
    [InlineData("@To An랑 같은 팀으로 하지 마")]
    [InlineData("@To An랑 다른 팀으로 해줘")]
    public void Explicit_apart_language_is_owned_by_team_separation(string text)
    {
        Assert.True(ZaloNaturalCommandParser.IsExplicitTeamSeparationRequest(text));
        Assert.Equal(
            ZaloBotIntent.TeamSeparation,
            ZaloBotIntelligence.ClassifyDeterministically(text).Intent);
    }

    [Theory]
    [InlineData("đừng tách tui với @To An")]
    [InlineData("không tách tui với @To An nha")]
    [InlineData("don't separate me from @To An")]
    [InlineData("tui muốn chung team với @To An")]
    [InlineData("put me on the same team as @To An")]
    public void Together_or_non_apart_language_is_not_promoted_to_apart(string text)
    {
        Assert.False(ZaloNaturalCommandParser.IsExplicitTeamSeparationRequest(text));
        Assert.NotEqual(
            ZaloBotIntent.TeamSeparation,
            ZaloBotIntelligence.ClassifyDeterministically(text).Intent);
    }

    [Theory]
    [InlineData("thôi khỏi né @To An nữa")]
    [InlineData("bỏ yêu cầu khác team @To An")]
    [InlineData("hủy né @To An")]
    [InlineData("remove the avoid rule with @To An")]
    [InlineData("cancel separate team with @To An")]
    public void Clear_apart_language_is_distinguished_from_set(string text)
    {
        Assert.True(ZaloNaturalCommandParser.IsExplicitTeamSeparationClearRequest(text));
        Assert.True(ZaloNaturalCommandParser.IsExplicitTeamSeparationRequest(text));
    }

    [Theory]
    [InlineData("tui với @To An chơi vui ghê")]
    [InlineData("@To An team hôm nay mấy giờ?")]
    [InlineData("avoid traffic on the way to the court")]
    public void Ordinary_chatter_is_not_an_apart_mutation(string text)
    {
        Assert.False(ZaloNaturalCommandParser.IsExplicitTeamSeparationRequest(text));
    }
}
