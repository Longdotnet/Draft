using SkiaSharp;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class CourtIndexPortraitQualityTests
{
    [Fact]
    public void Poster_one_keeps_normal_zalo_avatar_inside_native_scale_editorial_plate()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(640, 640));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        // This point sits inside the hero frame but outside the smaller editorial photo plate.
        // It must remain the light Court Index matte instead of becoming stretched avatar pixels.
        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(pixel.Green > 180, $"Expected paper matte around normal Zalo avatar, got {pixel}");
    }

    [Fact]
    public void Poster_one_allows_full_bleed_only_for_genuinely_oversized_avatar()
    {
        var bytes = RenderPosterWithCaptainAvatar(CreateSolidAvatar(1600, 1600));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);

        // A truly oversized source has enough resolution headroom to cover the complete hero frame.
        var pixel = bitmap.GetPixel(970, 110);
        Assert.True(pixel.Green < 40, $"Expected full-bleed avatar for HD source, got {pixel}");
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
