using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Poster 01 compositor that keeps the Court Index layout/data rendering but replaces the
/// procedural volleyball study with the approved high-fidelity embedded editorial asset.
/// </summary>
public static class CourtIndexAssetPosterRenderer
{
    private const string VolleyballResourceName =
        "VolleyDraft.Api.Assets.Posters.CourtIndexVolleyballStudy02.jpg";

    private static readonly SKRect VolleyballDestination =
        new(88, 1096, 543, 1526);

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        var basePoster = CourtIndexPosterRenderer.Render(sessionName, startTime, location, teams);

        using var baseBitmap = SKBitmap.Decode(basePoster);
        if (baseBitmap is null)
            return basePoster;

        using var resource = typeof(CourtIndexAssetPosterRenderer)
            .Assembly
            .GetManifestResourceStream(VolleyballResourceName);
        if (resource is null)
            return basePoster;

        using var volleyball = SKBitmap.Decode(resource);
        if (volleyball is null || volleyball.Width <= 0 || volleyball.Height <= 0)
            return basePoster;

        using var surface = SKSurface.Create(
            new SKImageInfo(
                PosterDrawing.Width,
                PosterDrawing.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create Court Index asset surface.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(baseBitmap, 0, 0);

        var source = CoverSource(volleyball, VolleyballDestination);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };

        canvas.DrawBitmap(volleyball, source, VolleyballDestination, paint);

        return PosterDrawing.Encode(surface);
    }

    private static SKRect CoverSource(SKBitmap bitmap, SKRect destination)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        var destinationAspect = destination.Width / destination.Height;

        if (sourceAspect > destinationAspect)
        {
            var cropWidth = bitmap.Height * destinationAspect;
            var left = (bitmap.Width - cropWidth) / 2f;
            return new SKRect(left, 0, left + cropWidth, bitmap.Height);
        }

        var cropHeight = bitmap.Width / destinationAspect;
        var top = (bitmap.Height - cropHeight) / 2f;
        return new SKRect(0, top, bitmap.Width, top + cropHeight);
    }
}
