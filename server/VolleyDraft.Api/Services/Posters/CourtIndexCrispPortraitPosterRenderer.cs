using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Final Poster 01 compositor. Keeps the approved Court Index + embedded volleyball artwork,
/// then redraws captain portraits with conservative source-resolution-aware sampling.
/// Zalo avatars are frequently compressed even when their nominal pixel dimensions look large,
/// so only genuinely oversized sources are allowed to fill the 414x513 hero frame. Normal Zalo
/// avatars are rendered as intentional editorial photo plates at or below native size instead of
/// being stretched to fill the frame.
/// </summary>
public static class CourtIndexCrispPortraitPosterRenderer
{
    private static readonly SKColor Paper = new(246, 241, 231);
    private static readonly SKColor Ink = new(22, 22, 20);
    private static readonly SKColor[] Accents =
    [
        new SKColor(25, 82, 196),
        new SKColor(230, 68, 38),
        new SKColor(26, 108, 66)
    ];

    // A source must have substantial headroom before it is allowed to cover the large captain
    // frame. A 480-640px Zalo avatar may technically fit the frame but still looks soft because
    // the CDN image is already compressed. Requiring ~2x source resolution prevents that case.
    private const float FullBleedPixelRatio = 1.9f;
    private const int FullBleedMinShortEdge = 900;

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        var basePoster = CourtIndexAssetPosterRenderer.Render(sessionName, startTime, location, teams);
        using var baseBitmap = SKBitmap.Decode(basePoster);
        if (baseBitmap is null) return basePoster;

        using var surface = SKSurface.Create(new SKImageInfo(
            PosterDrawing.Width,
            PosterDrawing.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create Court Index crisp portrait surface.");

        var canvas = surface.Canvas;
        canvas.DrawBitmap(baseBitmap, 0, 0);

        var visibleTeams = teams.Take(3).ToList();
        for (var index = 0; index < visibleTeams.Count; index += 1)
        {
            var captain = PosterDrawing.FindCaptain(visibleTeams[index]);
            if (captain?.AvatarData is not { Length: > 0 }) continue;

            var top = 68f + index * 547f;
            var portrait = new SKRect(952, top + 12, 1366, top + 525);
            DrawAdaptiveCaptain(canvas, portrait, captain, index);
        }

        return PosterDrawing.Encode(surface);
    }

    private static void DrawAdaptiveCaptain(
        SKCanvas canvas,
        SKRect rect,
        TeamCardPlayer captain,
        int teamIndex)
    {
        using var bitmap = SKBitmap.Decode(captain.AvatarData);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return;

        var accent = Accents[teamIndex % Accents.Length];
        if (ShouldUseFullBleed(bitmap.Width, bitmap.Height, rect.Width, rect.Height))
        {
            DrawFullBleedCaptain(canvas, rect, bitmap, accent);
            return;
        }

        DrawNativeScaleEditorialPlate(canvas, rect, bitmap, accent, teamIndex);
    }

    internal static bool ShouldUseFullBleed(
        int sourceWidth,
        int sourceHeight,
        float targetWidth,
        float targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return false;

        var pixelRatio = Math.Min(sourceWidth / targetWidth, sourceHeight / targetHeight);
        return pixelRatio >= FullBleedPixelRatio &&
               Math.Min(sourceWidth, sourceHeight) >= FullBleedMinShortEdge;
    }

    private static void DrawFullBleedCaptain(
        SKCanvas canvas,
        SKRect rect,
        SKBitmap bitmap,
        SKColor accent)
    {
        var source = CoverSource(bitmap, rect.Width / rect.Height);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            // Medium retains a little more micro-contrast than the very soft high-quality
            // resampler while still downsampling cleanly from a genuinely oversized source.
            FilterQuality = SKFilterQuality.Medium
        };
        canvas.DrawBitmap(bitmap, source, rect, paint);
        DrawFrames(canvas, rect, accent);
    }

    private static void DrawNativeScaleEditorialPlate(
        SKCanvas canvas,
        SKRect rect,
        SKBitmap bitmap,
        SKColor accent,
        int teamIndex)
    {
        // Cover the softer full-bleed image already present in the base poster. The photo plate
        // below deliberately trades a little image area for materially better perceived detail.
        using (var matte = new SKPaint { Color = Paper, IsAntialias = true })
            canvas.DrawRect(rect, matte);
        using (var wash = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 16), IsAntialias = true })
            canvas.DrawRect(new SKRect(rect.Left + 10, rect.Top + 10, rect.Right - 10, rect.Bottom - 10), wash);

        PosterDrawing.DrawCenteredText(
            canvas,
            (teamIndex + 1).ToString("00", CultureInfo.InvariantCulture),
            rect.MidX,
            rect.Bottom - 28,
            250,
            PosterDrawing.WithAlpha(accent, 25),
            true,
            rect.Width - 28,
            PosterDrawing.BlackTypeface);

        // Never enlarge a normal Zalo avatar. A 240px image stays 240px; a 640px image is reduced
        // into the editorial plate. This avoids manufacturing blur by interpolation.
        const float maxUpscale = 1f;
        var maxWidth = rect.Width - 62;
        var maxHeight = rect.Height - 94;
        var scale = Math.Min(
            maxUpscale,
            Math.Min(maxWidth / bitmap.Width, maxHeight / bitmap.Height));
        scale = Math.Max(scale, .01f);

        var drawWidth = bitmap.Width * scale;
        var drawHeight = bitmap.Height * scale;
        var imageRect = new SKRect(
            rect.MidX - drawWidth / 2,
            rect.MidY - drawHeight / 2 - 8,
            rect.MidX + drawWidth / 2,
            rect.MidY + drawHeight / 2 - 8);

        // Give the photo a small physical-print mount. The slightly smaller plate makes even a
        // compressed avatar read as an intentional editorial photograph instead of a blurry hero.
        var shadowRect = imageRect;
        shadowRect.Offset(8, 9);
        using (var shadow = new SKPaint { Color = new SKColor(18, 18, 16, 40), IsAntialias = true })
            canvas.DrawRect(shadowRect, shadow);

        var mount = new SKRect(imageRect.Left - 8, imageRect.Top - 8, imageRect.Right + 8, imageRect.Bottom + 8);
        using (var paperFrame = new SKPaint { Color = new SKColor(253, 250, 244), IsAntialias = true })
            canvas.DrawRect(mount, paperFrame);

        using (var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium
        })
        {
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height), imageRect, paint);
        }

        using (var photoFrame = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.1f,
            IsAntialias = true
        })
            canvas.DrawRect(mount, photoFrame);

        PosterDrawing.DrawText(
            canvas,
            "CAPTAIN PORTRAIT",
            rect.Left + 18,
            rect.Bottom - 18,
            10,
            Ink,
            true,
            rect.Width - 36);

        DrawFrames(canvas, rect, accent);
    }

    private static SKRect CoverSource(SKBitmap bitmap, float targetAspect)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        if (sourceAspect > targetAspect)
        {
            var width = bitmap.Height * targetAspect;
            var left = (bitmap.Width - width) / 2f;
            return new SKRect(left, 0, left + width, bitmap.Height);
        }

        var height = bitmap.Width / targetAspect;
        var top = (bitmap.Height - height) / 2f;
        return new SKRect(0, top, bitmap.Width, top + height);
    }

    private static void DrawFrames(SKCanvas canvas, SKRect rect, SKColor accent)
    {
        using var inner = new SKPaint
        {
            Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, 150),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(rect.Left + 5, rect.Top + 5, rect.Right - 5, rect.Bottom - 5), inner);

        using var outer = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 170),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawRect(rect, outer);
    }
}
