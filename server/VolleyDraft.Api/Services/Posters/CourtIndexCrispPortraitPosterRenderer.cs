using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Final Poster 01 compositor. Keeps the approved Court Index + embedded volleyball artwork,
/// then redraws captain portraits with source-resolution-aware sampling. Low-resolution Zalo
/// avatars are intentionally presented as smaller editorial photo plates instead of being
/// stretched across a 414x513 hero frame, which keeps faces materially sharper.
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
        var enoughPixelsForFullBleed =
            bitmap.Width >= rect.Width * .95f &&
            bitmap.Height >= rect.Height * .95f;

        if (enoughPixelsForFullBleed)
        {
            var source = CoverSource(bitmap, rect.Width / rect.Height);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High
            };
            canvas.DrawBitmap(bitmap, source, rect, paint);
            DrawFrames(canvas, rect, accent);
            return;
        }

        // Do not enlarge a 120/240px Zalo thumbnail to a 414x513 hero image. Instead use an
        // editorial matte and cap upscale at 1.65x. The smaller photo reads intentional in the
        // Court Index design and preserves substantially more facial sharpness.
        using (var matte = new SKPaint { Color = Paper, IsAntialias = true })
            canvas.DrawRect(rect, matte);
        using (var wash = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 18), IsAntialias = true })
            canvas.DrawRect(new SKRect(rect.Left + 10, rect.Top + 10, rect.Right - 10, rect.Bottom - 10), wash);

        PosterDrawing.DrawCenteredText(
            canvas,
            (teamIndex + 1).ToString("00", CultureInfo.InvariantCulture),
            rect.MidX,
            rect.Bottom - 30,
            250,
            PosterDrawing.WithAlpha(accent, 28),
            true,
            rect.Width - 30,
            PosterDrawing.BlackTypeface);

        const float maxUpscale = 1.65f;
        var maxWidth = rect.Width - 42;
        var maxHeight = rect.Height - 54;
        var scale = Math.Min(
            maxUpscale,
            Math.Min(maxWidth / bitmap.Width, maxHeight / bitmap.Height));
        scale = Math.Max(scale, .01f);

        var drawWidth = bitmap.Width * scale;
        var drawHeight = bitmap.Height * scale;
        var imageRect = new SKRect(
            rect.MidX - drawWidth / 2,
            rect.MidY - drawHeight / 2,
            rect.MidX + drawWidth / 2,
            rect.MidY + drawHeight / 2);

        var shadowRect = imageRect;
        shadowRect.Offset(7, 8);
        using (var shadow = new SKPaint { Color = new SKColor(18, 18, 16, 36), IsAntialias = true })
            canvas.DrawRect(shadowRect, shadow);

        var mount = new SKRect(imageRect.Left - 7, imageRect.Top - 7, imageRect.Right + 7, imageRect.Bottom + 7);
        using (var paperFrame = new SKPaint { Color = new SKColor(253, 250, 244), IsAntialias = true })
            canvas.DrawRect(mount, paperFrame);

        using (var paint = new SKPaint
        {
            IsAntialias = true,
            // Medium is deliberately crisper than the soft high-quality resampler when a
            // small source must be enlarged slightly.
            FilterQuality = SKFilterQuality.Medium
        })
        {
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height), imageRect, paint);
        }

        using (var photoFrame = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 190),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.2f,
            IsAntialias = true
        })
            canvas.DrawRect(mount, photoFrame);

        PosterDrawing.DrawText(
            canvas,
            "PROFILE / ORIGINAL CROP",
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
