using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Final Poster 01 compositor. Keeps the approved Court Index + embedded volleyball artwork,
/// then redraws captain portraits and roster text for production Zalo data.
///
/// Portrait strategy:
/// - medium/high resolution avatars cover the hero frame directly;
/// - very small Zalo avatars still read as a large hero treatment by using a softened full-frame
///   background plus a large contained foreground plate, instead of shrinking into a tiny stamp;
/// - all resizing happens once with conservative sampling and a small contrast lift.
///
/// Roster strategy:
/// - single-player slots stay one line;
/// - shared slots use one full line per player instead of concatenating both names and truncating.
/// </summary>
public static class CourtIndexCrispPortraitPosterRenderer
{
    private static readonly SKColor Paper = new(246, 241, 231);
    private static readonly SKColor Ink = new(22, 22, 20);
    private static readonly SKColor Rule = new(52, 49, 43, 72);
    private static readonly SKColor[] Accents =
    [
        new SKColor(25, 82, 196),
        new SKColor(230, 68, 38),
        new SKColor(26, 108, 66)
    ];

    private const int LowResolutionShortEdge = 360;
    private const float MaxLowResolutionForegroundUpscale = 3.2f;

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
            var team = visibleTeams[index];
            var captain = PosterDrawing.FindCaptain(team);
            var top = 68f + index * 547f;
            var portrait = new SKRect(952, top + 12, 1366, top + 525);

            if (captain?.AvatarData is { Length: > 0 })
                DrawCaptainHero(canvas, portrait, captain, index);

            DrawAdaptiveRoster(canvas, team, top, index);
        }

        return PosterDrawing.Encode(surface);
    }

    private static void DrawCaptainHero(
        SKCanvas canvas,
        SKRect rect,
        TeamCardPlayer captain,
        int teamIndex)
    {
        using var bitmap = SKBitmap.Decode(captain.AvatarData);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return;

        var accent = Accents[teamIndex % Accents.Length];
        var shortEdge = Math.Min(bitmap.Width, bitmap.Height);

        if (shortEdge < LowResolutionShortEdge)
            DrawLowResolutionHero(canvas, rect, bitmap, accent);
        else
            DrawFullFrameHero(canvas, rect, bitmap, accent);

        DrawFrames(canvas, rect, accent);
    }

    private static void DrawFullFrameHero(
        SKCanvas canvas,
        SKRect rect,
        SKBitmap bitmap,
        SKColor accent)
    {
        var source = CoverSource(bitmap, rect.Width / rect.Height);
        using var contrast = CreateGentleContrastFilter();
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            ColorFilter = contrast
        };

        canvas.DrawBitmap(bitmap, source, rect, paint);

        // Preserve original avatar color while adding only a very light editorial edge lift.
        using var edge = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 18),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(rect.Left + 3, rect.Top + 3, rect.Right - 3, rect.Bottom - 3), edge);
    }

    private static void DrawLowResolutionHero(
        SKCanvas canvas,
        SKRect rect,
        SKBitmap bitmap,
        SKColor accent)
    {
        // Fill the whole hero frame with the same real avatar so the composition remains as large
        // as the approved preview. The softened background hides low-resolution interpolation.
        var backgroundSource = CoverSource(bitmap, rect.Width / rect.Height);
        using var contrast = CreateGentleContrastFilter();
        using (var backgroundPaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Low,
            ColorFilter = contrast
        })
        {
            canvas.DrawBitmap(bitmap, backgroundSource, rect, backgroundPaint);
        }

        using (var soften = new SKPaint
        {
            Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, 92),
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, soften);
        }

        // The foreground keeps the full source crop and occupies most of the hero area. A typical
        // 240px Zalo avatar becomes roughly 370-390px instead of a tiny 240px stamp.
        var maxWidth = rect.Width - 30;
        var maxHeight = rect.Height - 34;
        var scale = Math.Min(
            MaxLowResolutionForegroundUpscale,
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
        using (var shadow = new SKPaint
        {
            Color = new SKColor(16, 16, 14, 46),
            IsAntialias = true
        })
        {
            canvas.DrawRect(shadowRect, shadow);
        }

        var mount = new SKRect(
            imageRect.Left - 7,
            imageRect.Top - 7,
            imageRect.Right + 7,
            imageRect.Bottom + 7);
        using (var mountPaint = new SKPaint
        {
            Color = new SKColor(253, 250, 244),
            IsAntialias = true
        })
        {
            canvas.DrawRect(mount, mountPaint);
        }

        using (var foregroundPaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            ColorFilter = contrast
        })
        {
            canvas.DrawBitmap(
                bitmap,
                new SKRect(0, 0, bitmap.Width, bitmap.Height),
                imageRect,
                foregroundPaint);
        }

        using var photoFrame = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 205),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.1f,
            IsAntialias = true
        };
        canvas.DrawRect(mount, photoFrame);
    }

    private static SKColorFilter CreateGentleContrastFilter()
    {
        // Small contrast lift only. It restores micro-contrast lost by CDN compression/resampling
        // without recoloring the captain or creating a fake AI-enhanced appearance.
        const float contrast = 1.055f;
        const float offset = -7f;
        return SKColorFilter.CreateColorMatrix(
        [
            contrast, 0, 0, 0, offset,
            0, contrast, 0, 0, offset,
            0, 0, contrast, 0, offset,
            0, 0, 0, 1, 0
        ]);
    }

    private static void DrawAdaptiveRoster(
        SKCanvas canvas,
        TeamCardTeam team,
        float teamTop,
        int teamIndex)
    {
        var accent = Accents[teamIndex % Accents.Length];

        // Cover the old one-line roster from the base renderer and redraw it with adaptive rows.
        // Keep this strictly inside the info column; the captain image begins at x=952.
        var panel = new SKRect(752, teamTop + 342, 947, teamTop + 525);
        using (var paper = new SKPaint { Color = Paper, IsAntialias = true })
            canvas.DrawRect(panel, paper);

        PosterDrawing.DrawText(canvas, "ROSTER", 764, teamTop + 362, 14, accent, true, 105);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        if (slots.Count == 0) return;

        const float x = 764;
        const float numberWidth = 28;
        const float textWidth = 151;
        const float startY = 374;
        const float availableHeight = 145;

        var desiredHeight = slots.Sum(slot => slot.Players.Count > 1 ? 32f : 21f);
        var rowScale = desiredHeight <= availableHeight ? 1f : availableHeight / desiredHeight;
        var cursor = teamTop + startY;

        for (var index = 0; index < slots.Count; index += 1)
        {
            var slot = slots[index];
            var shared = slot.Players.Count > 1;
            var rowHeight = (shared ? 32f : 21f) * rowScale;
            var number = (index + 1).ToString("00", CultureInfo.InvariantCulture);

            PosterDrawing.DrawText(
                canvas,
                number,
                x,
                cursor + Math.Max(10f, 12f * rowScale),
                Math.Max(9f, 11.5f * rowScale),
                accent,
                true,
                numberWidth);

            var names = GetSlotNames(slot);
            if (shared)
            {
                var firstBaseline = cursor + Math.Max(9.5f, 11f * rowScale);
                var secondBaseline = cursor + Math.Max(21f, 25f * rowScale);
                DrawFitTextWithoutEllipsis(
                    canvas,
                    names.ElementAtOrDefault(0) ?? slot.DisplayName,
                    x + numberWidth,
                    firstBaseline,
                    textWidth,
                    Math.Max(8.5f, 10.8f * rowScale),
                    accent,
                    bold: true);
                DrawFitTextWithoutEllipsis(
                    canvas,
                    names.ElementAtOrDefault(1) ?? "SHARED",
                    x + numberWidth,
                    secondBaseline,
                    textWidth,
                    Math.Max(8.2f, 10.2f * rowScale),
                    Ink,
                    bold: false);
            }
            else
            {
                DrawFitTextWithoutEllipsis(
                    canvas,
                    names.ElementAtOrDefault(0) ?? slot.DisplayName,
                    x + numberWidth,
                    cursor + Math.Max(10.5f, 12.5f * rowScale),
                    textWidth,
                    Math.Max(8.6f, 10.8f * rowScale),
                    Ink,
                    bold: slot.IsCaptainSlot);
            }

            using var rowRule = new SKPaint
            {
                Color = Rule,
                StrokeWidth = .8f,
                IsAntialias = true
            };
            canvas.DrawLine(
                x + numberWidth,
                cursor + rowHeight - 2,
                x + numberWidth + textWidth,
                cursor + rowHeight - 2,
                rowRule);

            cursor += rowHeight;
        }
    }

    internal static IReadOnlyList<string> BuildRosterDisplayLines(TeamCardTeam team)
    {
        var result = new List<string>();
        foreach (var slot in PosterDrawing.VisibleSlots(team, 6))
        {
            var names = GetSlotNames(slot);
            if (names.Count == 0)
            {
                result.Add(slot.DisplayName);
                continue;
            }

            foreach (var name in names.Take(2))
                result.Add(name);
        }
        return result;
    }

    private static IReadOnlyList<string> GetSlotNames(TeamCardSlot slot) =>
        slot.Players
            .Take(2)
            .Select(player => player.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

    private static void DrawFitTextWithoutEllipsis(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        float maxWidth,
        float preferredSize,
        SKColor color,
        bool bold)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var size = preferredSize;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            Typeface = bold ? PosterDrawing.BoldTypeface : PosterDrawing.RegularTypeface,
            SubpixelText = true
        };

        const float minimumSize = 7.4f;
        paint.TextSize = size;
        while (size > minimumSize && paint.MeasureText(text) > maxWidth)
        {
            size -= .35f;
            paint.TextSize = size;
        }

        // The roster intentionally never appends ellipsis. In the extremely rare case that a
        // single name still exceeds the row at minimum size, compress only the x-axis while
        // preserving the complete text.
        var measured = Math.Max(1f, paint.MeasureText(text));
        if (measured <= maxWidth)
        {
            canvas.DrawText(text, x, y, paint);
            return;
        }

        var save = canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(maxWidth / measured, 1f);
        canvas.DrawText(text, 0, 0, paint);
        canvas.RestoreToCount(save);
    }

    internal static bool ShouldUseFullBleed(
        int sourceWidth,
        int sourceHeight,
        float targetWidth,
        float targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return false;

        return Math.Min(sourceWidth, sourceHeight) >= LowResolutionShortEdge;
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
            Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, 125),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(rect.Left + 4, rect.Top + 4, rect.Right - 4, rect.Bottom - 4), inner);

        using var outer = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 185),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            IsAntialias = true
        };
        canvas.DrawRect(rect, outer);
    }
}
