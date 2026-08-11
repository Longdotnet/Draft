using SkiaSharp;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class CourtIndexPortraitQualityTests
{
    [Fact]
    public void Poster_one_keeps_small_zalo_avatar_large_and_not_black()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(240, 240));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        // Small Zalo avatars must still fill the captain frame. This point is well inside the
        // hero image and therefore must preserve the magenta fixture instead of becoming paper,
        // a tiny centered plate, or the V3 black-frame regression.
        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(pixel.Red > 180 && pixel.Blue > 180 && pixel.Green < 80,
            $"Expected full-frame magenta avatar for 240px source, got {pixel}");
        Assert.True(pixel.Red + pixel.Green + pixel.Blue > 220,
            $"Captain portrait must not render as a black frame, got {pixel}");

        Assert.False(CourtIndexCrispPortraitPosterRenderer.ShouldUseFullBleed(240, 240, 414, 513));
    }

    [Fact]
    public void Poster_one_keeps_medium_or_hd_avatar_full_frame_and_not_black()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(640, 640));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(pixel.Red > 180 && pixel.Blue > 180 && pixel.Green < 80,
            $"Expected full-frame magenta avatar for 640px source, got {pixel}");
        Assert.True(pixel.Red + pixel.Green + pixel.Blue > 220,
            $"Captain portrait must not render as a black frame, got {pixel}");

        Assert.True(CourtIndexCrispPortraitPosterRenderer.ShouldUseFullBleed(640, 640, 414, 513));
    }

    [Fact]
    public void Shared_slot_keeps_both_full_names_without_ellipsis()
    {
        const string first = "Vivian Nguyễn Thị Minh Anh";
        const string second = "Nguyễn Minh Huy Trần Quốc Việt";
        var captain = new TeamCardPlayer("Captain", IsCaptain: true);
        var team = new TeamCardTeam(
            "TEAM A",
            captain.Name,
            8.4,
            [
                new TeamCardSlot(captain.Name, [captain], true),
                new TeamCardSlot("Shared", [new TeamCardPlayer(first), new TeamCardPlayer(second)])
            ]);

        var lines = CourtIndexCrispPortraitPosterRenderer.BuildRosterDisplayLines(team);

        Assert.Contains(first, lines);
        Assert.Contains(second, lines);
        Assert.DoesNotContain(lines, line => line.Contains('…'));

        var bytes = TeamPosterRendererRegistry.Render(
            1,
            "Thứ 4 12/8",
            new DateTimeOffset(2026, 8, 12, 17, 30, 0, TimeSpan.FromHours(7)),
            "VOLLEY DRAFT",
            [team]);
        using var rendered = SKBitmap.Decode(bytes);
        Assert.NotNull(rendered);
        Assert.Equal(1440, rendered.Width);
        Assert.Equal(1800, rendered.Height);
    }

    private static byte[] RenderPosterWithCaptainAvatar(byte[] avatar)
    {
        var captain = new TeamCardPlayer("Captain", AvatarData: avatar, IsCaptain: true);
        var team = new TeamCardTeam(
            "TEAM A",
            captain.Name,
            8.4,
            [new TeamCardSlot(captain.Name, [captain], true)]);

        return TeamPosterRendererRegistry.Render(
            1,
            "Thứ 4 12/8",
            new DateTimeOffset(2026, 8, 12, 17, 30, 0, TimeSpan.FromHours(7)),
            "VOLLEY DRAFT",
            [team]);
    }

    private static byte[] CreateSolidAvatar(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create avatar fixture.");
        surface.Canvas.Clear(new SKColor(255, 0, 255));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
