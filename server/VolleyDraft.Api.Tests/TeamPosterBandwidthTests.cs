using SkiaSharp;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class TeamPosterBandwidthTests
{
    [Fact]
    public void Team_poster_delivery_is_downscaled_for_mobile_and_zalo()
    {
        var teams = new[]
        {
            new TeamCardTeam(
                "TEAM A",
                "Captain A",
                8.4,
                [
                    new TeamCardSlot("Captain A", [new TeamCardPlayer("Captain A", IsCaptain: true)], true),
                    new TeamCardSlot("Player 2", [new TeamCardPlayer("Player 2")]),
                    new TeamCardSlot("Player 3", [new TeamCardPlayer("Player 3")]),
                    new TeamCardSlot("Player 4", [new TeamCardPlayer("Player 4")])
                ]),
            new TeamCardTeam(
                "TEAM B",
                "Captain B",
                8.1,
                [
                    new TeamCardSlot("Captain B", [new TeamCardPlayer("Captain B", IsCaptain: true)], true),
                    new TeamCardSlot("Player 6", [new TeamCardPlayer("Player 6")]),
                    new TeamCardSlot("Player 7", [new TeamCardPlayer("Player 7")]),
                    new TeamCardSlot("Player 8", [new TeamCardPlayer("Player 8")])
                ])
        };

        var bytes = TeamPosterRendererRegistry.Render(
            1,
            "KÈO TỐI",
            new DateTimeOffset(2026, 8, 29, 20, 0, 0, TimeSpan.FromHours(7)),
            "Sân bóng chuyền",
            teams);

        Assert.True(bytes.Length > 10_000);
        Assert.True(bytes.Length <= 1_500_000, $"Team poster is {bytes.Length:N0} bytes, above the 1.5 MB delivery budget.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(TeamPosterRendererRegistry.DeliveryWidth, bitmap.Width);
        Assert.Equal(TeamPosterRendererRegistry.DeliveryHeight, bitmap.Height);
    }
}
