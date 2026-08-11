using SkiaSharp;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class CourtIndexPortraitQualityTests
{
    [Fact]
    public void Poster_one_keeps_small_zalo_avatar_visually_large_in_hero_frame()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(240, 240));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        // A 240px Zalo avatar must no longer become a tiny stamp in the middle of the 414x513
        // captain frame. This corner sits outside the foreground plate but inside the softened
        // avatar background, so it should still contain strong avatar color rather than paper.
        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(
            Math.Abs(pixel.Red - 246) > 35 || Math.Abs(pixel.Blue - 231) > 35,
            $"Expected avatar-backed hero frame for 240px source, got {pixel}");

        Assert.False(CourtIndexCrispPortraitPosterRenderer.ShouldUseFullBleed(240, 240, 414, 513));
    }

    [Fact]
    public void Poster_one_uses_direct_full_frame_for_medium_or_hd_avatar()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(640, 640));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(pixel.Red > 180 && pixel.Blue > 180 && pixel.Green < 80,
            $"Expected full-frame magenta avatar for 640px source, got {pixel}");

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
