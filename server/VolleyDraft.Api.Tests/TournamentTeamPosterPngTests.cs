using SkiaSharp;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class TournamentTeamPosterPngTests
{
    [Fact]
    public void Renders_three_team_vertical_tournament_poster()
    {
        var teams = new List<TeamCardTeam>
        {
            BuildTeam("TEAM ALPHA", "Nguyễn Hoàng Long", 7.8, 1),
            BuildTeam("TEAM BRAVO", "To An", 8.1, 2),
            BuildTeam("TEAM CHARLIE", "Trần Quốc Việt", 7.9, 3)
        };

        var bytes = TournamentTeamPosterPng.Render(
            "CN 16/08 - KÈO TỐI",
            new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(7)),
            "Sân bóng chuyền Bình Trưng",
            teams);

        AssertPng(bytes, TournamentTeamPosterPng.Width, TournamentTeamPosterPng.Height);
        WritePreviewIfRequested(bytes);
    }

    [Fact]
    public void Renders_empty_state_without_throwing()
    {
        var bytes = TournamentTeamPosterPng.Render(
            "T6",
            null,
            null,
            []);

        AssertPng(bytes, TournamentTeamPosterPng.Width, TournamentTeamPosterPng.Height);
    }

    [Fact]
    public void Handles_long_names_shared_slots_and_missing_avatars()
    {
        var captain = new TeamCardPlayer(
            "Nguyễn Một Cái Tên Cực Kỳ Dài Để Test Layout",
            null,
            null,
            true);
        var shared = new TeamCardSlot(
            "Shared",
            [
                new TeamCardPlayer("Người Chơi Không Có Avatar Số Một"),
                new TeamCardPlayer("Partner Ngoài Zalo")
            ]);
        var team = new TeamCardTeam(
            "ĐỘI HÌNH SIÊU DÀI KHÔNG ĐƯỢC TRÀN KHUNG",
            captain.Name,
            9.25,
            [
                new TeamCardSlot(captain.Name, [captain], true),
                shared,
                new TeamCardSlot("P3", [new TeamCardPlayer("Lê Văn Ba")]),
                new TeamCardSlot("P4", [new TeamCardPlayer("Phạm Thị Bốn")]),
                new TeamCardSlot("P5", [new TeamCardPlayer("Hoàng Năm")]),
                new TeamCardSlot("P6", [new TeamCardPlayer("Võ Sáu")])
            ]);

        var bytes = TournamentTeamPosterPng.Render(
            "GIẢI ĐẤU NỘI BỘ VOLLEY DRAFT 2026",
            DateTimeOffset.UtcNow,
            "Một địa điểm có tên rất dài để kiểm tra phần metadata không tràn khỏi poster",
            [team]);

        AssertPng(bytes, TournamentTeamPosterPng.Width, TournamentTeamPosterPng.Height);
    }

    private static TeamCardTeam BuildTeam(string name, string captainName, double score, int seed)
    {
        var slots = new List<TeamCardSlot>();
        for (var index = 0; index < 6; index += 1)
        {
            var playerName = index == 0 ? captainName : $"Player {seed}-{index + 1}";
            var player = new TeamCardPlayer(playerName, IsCaptain: index == 0);
            slots.Add(new TeamCardSlot(playerName, [player], index == 0));
        }
        return new TeamCardTeam(name, captainName, score, slots);
    }

    private static void WritePreviewIfRequested(byte[] bytes)
    {
        var path = Environment.GetEnvironmentVariable("TEAM_POSTER_PREVIEW_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private static void AssertPng(byte[] bytes, int width, int height)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1024);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
    }
}
