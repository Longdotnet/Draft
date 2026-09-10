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
    public void Empty_team_result_teaches_the_complete_deterministic_npc10_recovery_loop()
    {
        var result = ZaloTeamLineupFormatter.Format("CN 13/9", []);

        Assert.Empty(result.Mentions);
        Assert.Contains("chưa có kết quả chia team", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lệnh 10 chỉ đọc kết quả đã có", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần biết các từ kỹ thuật của hệ thống", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 9 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.Contains("@Npc 4 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.Contains("@Npc 10 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.Contains("người được NPC hỏi", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`nam`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc cập nhật @Tên: nam`", result.Text, StringComparison.Ordinal);
        Assert.Contains("phải tag đúng người", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cập nhật Nick Tran: nam", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ pass", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xong", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xác nhận draft", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần @Npc lại", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trưởng nhóm", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phó nhóm", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có quyền", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI có tắt", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handler deterministic", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_authoritative_session_name_never_invents_a_selector()
    {
        var result = ZaloTeamLineupFormatter.Format("   ", []);

        Assert.Contains("`@Npc 4`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 9`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10`", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 4 Buổi này", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc 9 Buổi này", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc 10 Buổi này", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CN 13/9\n@Npc 1")]
    [InlineData("CN `13/9`")]
    [InlineData("CN @Long")]
    public void Unsafe_session_names_fail_closed_to_bare_recovery_commands(string sessionName)
    {
        var result = ZaloTeamLineupFormatter.Format(sessionName, []);

        Assert.Contains("`@Npc 4`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 9`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10`", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 4 {sessionName}", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 9 {sessionName}", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 10 {sessionName}", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_display_name_collapses_control_whitespace_without_teaching_a_mutated_selector()
    {
        var result = ZaloTeamLineupFormatter.Format("  CN 13/9\nSân A  ", []);

        Assert.StartsWith("CN 13/9 Sân A chưa có kết quả chia team", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 4`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 9`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10`", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 4 CN 13/9 Sân A", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 9 CN 13/9 Sân A", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_session_name_does_not_create_an_unwieldy_executable_looking_command()
    {
        var sessionName = new string('A', 161);

        var result = ZaloTeamLineupFormatter.Format(sessionName, []);

        Assert.Contains("`@Npc 4`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 9`", result.Text, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10`", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 4 {sessionName}", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Npc 9 {sessionName}", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Precreated_empty_team_rows_are_still_treated_as_no_draft_result()
    {
        var teams = new[]
        {
            new TeamPreviewResponse("team-a", "Team A", null, []),
            new TeamPreviewResponse("team-b", "Team B", null, []),
            new TeamPreviewResponse("team-c", "Team C", null, [])
        };

        var result = ZaloTeamLineupFormatter.Format("Thứ 4 09/9", teams);

        Assert.Contains("@Npc 4 Thứ 4 09/9", result.Text, StringComparison.Ordinal);
        Assert.Contains("@Npc 9 Thứ 4 09/9", result.Text, StringComparison.Ordinal);
        Assert.Contains("@Npc 10 Thứ 4 09/9", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Đội hình Thứ 4 09/9:", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_team_result_does_not_show_recovery_instructions()
    {
        var teams = new[]
        {
            new TeamPreviewResponse(
                "team-a",
                "Team A",
                "Thanh Tuyền",
                [Slot("slot-1", "Thanh Tuyền")])
        };

        var result = ZaloTeamLineupFormatter.Format("CN 13/9", teams);

        Assert.StartsWith("Đội hình CN 13/9:", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 9", result.Text, StringComparison.OrdinalIgnoreCase);
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
