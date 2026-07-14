using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamLineupFormatterTests
{
    [Theory]
    [InlineData("gửi lại danh sách team và tag từng người")]
    [InlineData("mention hết player trong 3 team giúp tui")]
    [InlineData("đưa đội hình rồi tag cả team")]
    public void Explicit_tag_phrases_enable_player_mentions(string question)
    {
        Assert.True(ZaloTeamLineupFormatter.WantsPlayerMentions(question));
    }

    [Theory]
    [InlineData("gửi lại danh sách team")]
    [InlineData("cho tui coi đội hình 3 team")]
    [InlineData("gửi ảnh card đội hình")]
    public void Normal_lineup_requests_do_not_spam_mentions(string question)
    {
        Assert.False(ZaloTeamLineupFormatter.WantsPlayerMentions(question));
    }

    [Fact]
    public void Formatter_mentions_known_zalo_players_and_keeps_guests_as_plain_text()
    {
        var teams = new[]
        {
            new TeamPreviewResponse(
                "team-a",
                "Team A",
                "Thanh Tuyền",
                [
                    Slot("slot-1", "Thanh Tuyền"),
                    Slot("slot-2", "Bạn của Nick")
                ])
        };
        var players = new Dictionary<string, IReadOnlyList<ZaloTeamMentionPlayer>>
        {
            ["slot-1"] = [new("Thanh Tuyền", "zalo-tuyen")],
            ["slot-2"] = [new("Bạn của Nick", null)]
        };

        var result = ZaloTeamLineupFormatter.Format("Thứ 4 15/7", teams, players);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("zalo-tuyen", mention.Uid);
        Assert.Equal("@Thanh Tuyền", result.Text.Substring(mention.Pos, mention.Len));
        Assert.Contains("Bạn của Nick", result.Text);
        Assert.DoesNotContain("@Bạn của Nick", result.Text);
    }

    private static TeamSlotPreviewResponse Slot(string id, string name) =>
        new(id, name, DraftSlotType.Single, PlayerGender.Unknown, false, 0);
}
