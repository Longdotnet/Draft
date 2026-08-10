using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Poster 01 — Court Index.
/// A neo-Swiss / risograph match-program composition inspired by the approved preview:
/// oversized editorial typography on warm paper, stacked team indexes, duotone captain
/// portraits, print-registration details and compact roster data. The renderer remains
/// fully data-driven and does not change poster assignment/rotation behavior.
/// </summary>
public static class CourtIndexPosterRenderer
{
    private static readonly SKColor Paper = new(246, 241, 231);
    private static readonly SKColor Ink = new(22, 22, 20);
    private static readonly SKColor Rule = new(48, 47, 42, 118);
    private static readonly SKColor Muted = new(84, 82, 74);
    private static readonly SKColor[] Accents =
    [
        new SKColor(25, 82, 196),  // cobalt blue
        new SKColor(230, 68, 38),  // vermilion
        new SKColor(26, 108, 66)   // deep green
    ];

    private static readonly string[] Traits =
    [
        "DISCIPLINE • SPEED • POWER",
        "AGILITY • TEAMWORK • HEART",
        "SMART • CONSISTENT • CLUTCH"
    ];

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Paper);
        var canvas = surface.Canvas;
        var visibleTeams = teams.Take(3).ToList();

        DrawPaperTexture(canvas, sessionName);
        DrawPrintMarks(canvas);
        DrawLeftEditorialColumn(canvas, sessionName, startTime, location);
        DrawTeamStack(canvas, visibleTeams);
        DrawFooter(canvas);

        return PosterDrawing.Encode(surface);
    }

    private static void DrawPaperTexture(SKCanvas canvas, string sessionName)
    {
        var random = new Random(PosterDrawing.StableSeed($"court-index:{sessionName}") & int.MaxValue);
        using var dot = new SKPaint { IsAntialias = true };
        for (var index = 0; index < 3200; index++)
        {
            var x = random.Next(16, PosterDrawing.Width - 16);
            var y = random.Next(16, PosterDrawing.Height - 16);
            var alpha = (byte)random.Next(4, 17);
            var radius = random.NextDouble() < .88 ? .55f : 1.15f;
            dot.Color = new SKColor(40, 37, 31, alpha);
            canvas.DrawCircle(x, y, radius, dot);
        }

        using var fiber = new SKPaint
        {
            Color = new SKColor(122, 112, 91, 12),
            StrokeWidth = 1,
            IsAntialias = true
        };
        for (var index = 0; index < 160; index++)
        {
            var x = random.Next(30, PosterDrawing.Width - 30);
            var y = random.Next(30, PosterDrawing.Height - 30);
            canvas.DrawLine(x, y, x + random.Next(3, 14), y + random.Next(-1, 2), fiber);
        }
    }

    private static void DrawPrintMarks(SKCanvas canvas)
    {
        DrawCropMark(canvas, 34, 38);
        DrawCropMark(canvas, PosterDrawing.Width - 34, 38);
        DrawCropMark(canvas, 34, PosterDrawing.Height - 38);
        DrawCropMark(canvas, PosterDrawing.Width - 34, PosterDrawing.Height - 38);
        DrawRegistrationTarget(canvas, PosterDrawing.Width / 2f, 38, 13);
        DrawRegistrationTarget(canvas, 34, 602, 12);
        DrawRegistrationTarget(canvas, PosterDrawing.Width - 34, 602, 12);
        DrawRegistrationTarget(canvas, PosterDrawing.Width / 2f, PosterDrawing.Height - 28, 12);

        PosterDrawing.DrawText(canvas, "+ COURT INDEX", 82, 44, 18, Ink, true, 200);
        PosterDrawing.DrawText(canvas, "VOL. 01", 84, 68, 13, Muted, true, 100);
        DrawVerticalText(canvas, "VOLLEY DRAFT PROGRAM", 55, 390, 16, Muted, true);

        DrawColorBars(canvas, 50, 1360);
        PosterDrawing.DrawText(canvas, "RISOGRAPH", 33, 1653, 11, Ink, true, 92);
        PosterDrawing.DrawText(canvas, "PRINT", 48, 1670, 11, Ink, true, 54);
        PosterDrawing.DrawText(canvas, "CMYK", 86, PosterDrawing.Height - 24, 12, Muted, true, 80);
    }

    private static void DrawLeftEditorialColumn(
        SKCanvas canvas,
        string sessionName,
        DateTimeOffset? startTime,
        string? location)
    {
        const float left = 88;
        const float columnRight = 570;

        DrawCompressedText(canvas, "VOLLEY", left, 300, 252, Ink, columnRight - left - 18, PosterDrawing.BlackTypeface);
        DrawCompressedText(canvas, "DRAFT", left, 574, 252, Ink, columnRight - left - 18, PosterDrawing.BlackTypeface);

        PosterDrawing.DrawText(canvas, "MATCH PROGRAM", left + 2, 652, 51, Ink, true, 460, PosterDrawing.BlackTypeface);
        DrawRule(canvas, left, 672, 455, 3, Ink);

        PosterDrawing.DrawText(canvas, "AUTO DRAFT RESULT", left + 2, 739, 25, Ink, true, 280);
        DrawRegistrationTarget(canvas, 512, 727, 22);
        DrawRule(canvas, left, 770, 455, 1.2f, Rule);

        DrawEventMetadata(canvas, left, 816, sessionName, startTime, location);
        DrawVolleyballStudy(canvas, 92, 1080, 425, 385);

        PosterDrawing.DrawText(canvas, "THREE TEAMS / ONE COURT", left, 1662, 43, Ink, true, 465, PosterDrawing.BlackTypeface);
        DrawRule(canvas, left, 1684, 455, 3, Ink);

        using var divider = new SKPaint { Color = Rule, StrokeWidth = 1.3f, IsAntialias = true };
        canvas.DrawLine(588, 82, 588, 1728, divider);

        DrawVerticalText(canvas, BuildEditionDate(startTime), 56, 1262, 14, Muted, true);
    }

    private static void DrawEventMetadata(
        SKCanvas canvas,
        float x,
        float y,
        string sessionName,
        DateTimeOffset? startTime,
        string? location)
    {
        var local = startTime?.ToOffset(TimeSpan.FromHours(7));
        var date = local?.ToString("dd/MM", CultureInfo.InvariantCulture) ?? "MATCHDAY";
        var time = local?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "--:--";
        var venue = string.IsNullOrWhiteSpace(location) ? "VOLLEY DRAFT" : location.Trim();

        DrawCalendarIcon(canvas, x + 2, y + 4);
        PosterDrawing.DrawText(canvas, "DATE", x + 46, y + 16, 12, Muted, true, 80);
        PosterDrawing.DrawText(canvas, string.IsNullOrWhiteSpace(sessionName) ? date : sessionName, x + 46, y + 58, 29, Ink, true, 395, PosterDrawing.BlackTypeface);

        DrawClockIcon(canvas, x + 2, y + 92);
        PosterDrawing.DrawText(canvas, "TIME / VENUE", x + 46, y + 104, 12, Muted, true, 120);
        PosterDrawing.DrawText(canvas, $"{time} • {venue}", x + 46, y + 147, 28, Ink, true, 395, PosterDrawing.BlackTypeface);
    }

    private static void DrawTeamStack(SKCanvas canvas, IReadOnlyList<TeamCardTeam> teams)
    {
        const float left = 610;
        const float right = 1384;
        const float top = 68;
        const float height = 535;
        const float gap = 12;

        for (var index = 0; index < 3; index++)
        {
            var y = top + index * (height + gap);
            var rect = new SKRect(left, y, right, y + height);
            if (index < teams.Count)
                DrawTeamSection(canvas, rect, teams[index], index);
            else
                DrawEmptyTeamSection(canvas, rect, index);
        }
    }

    private static void DrawTeamSection(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var accent = Accents[index];
        var teamNumber = (index + 1).ToString("00", CultureInfo.InvariantCulture);
        var captain = PosterDrawing.FindCaptain(team);

        if (index > 0)
            DrawRule(canvas, rect.Left, rect.Top, rect.Width, 1.3f, Rule);

        const float infoWidth = 330;
        var portrait = new SKRect(rect.Left + infoWidth + 12, rect.Top + 12, rect.Right - 18, rect.Bottom - 10);

        PosterDrawing.DrawPill(
            canvas,
            "TEAM",
            new SKRect(rect.Left + 8, rect.Top + 10, rect.Left + 76, rect.Top + 36),
            accent,
            Paper,
            textSize: 12);

        DrawCompressedText(canvas, teamNumber, rect.Left + 6, rect.Top + 118, 108, accent, 122, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, "TEAM", rect.Left + 8, rect.Top + 166, 37, Ink, true, 130, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name, rect.Left + 8, rect.Top + 218, 45, Ink, true, 310, PosterDrawing.BlackTypeface);
        DrawRule(canvas, rect.Left + 8, rect.Top + 238, 308, 2, accent);

        PosterDrawing.DrawText(canvas, "CAPTAIN", rect.Left + 8, rect.Top + 273, 14, accent, true, 100);
        PosterDrawing.DrawText(canvas, captain?.Name ?? team.CaptainName ?? "CHƯA CHỌN", rect.Left + 8, rect.Top + 304, 22, Ink, true, 306, PosterDrawing.BoldTypeface);

        PosterDrawing.DrawText(canvas, "POWER", rect.Left + 8, rect.Top + 362, 18, accent, true, 90);
        DrawCompressedText(canvas, PosterDrawing.TeamScore(team), rect.Left + 6, rect.Top + 456, 80, accent, 120, PosterDrawing.BlackTypeface);

        PosterDrawing.DrawText(canvas, "ROSTER", rect.Left + 154, rect.Top + 362, 14, accent, true, 90);
        DrawRosterIndex(canvas, team, rect.Left + 154, rect.Top + 382, 160, accent);

        DrawPortraitFeature(canvas, portrait, captain, teamNumber, team.Name, accent, index);
        DrawVerticalText(canvas, Traits[index], rect.Right - 3, rect.Top + 448, 15, accent, true);
    }

    private static void DrawEmptyTeamSection(SKCanvas canvas, SKRect rect, int index)
    {
        var accent = Accents[index];
        if (index > 0)
            DrawRule(canvas, rect.Left, rect.Top, rect.Width, 1.3f, Rule);

        PosterDrawing.DrawText(canvas, $"TEAM {(index + 1):00}", rect.Left + 12, rect.Top + 86, 30, accent, true, 250, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, "LINEUP PENDING", rect.Left + 12, rect.Top + 150, 52, Ink, true, 430, PosterDrawing.BlackTypeface);
        DrawRule(canvas, rect.Left + 12, rect.Top + 180, 420, 2, accent);
    }

    private static void DrawRosterIndex(SKCanvas canvas, TeamCardTeam team, float x, float y, float width, SKColor accent)
    {
        var slots = PosterDrawing.VisibleSlots(team, 6);
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var rowY = y + index * 25;
            var number = (index + 1).ToString("00", CultureInfo.InvariantCulture);
            PosterDrawing.DrawText(canvas, number, x, rowY, 12, accent, true, 26);
            PosterDrawing.DrawText(canvas, BuildSlotLabel(slot), x + 29, rowY, 12, Ink, true, width - 31);
            DrawRule(canvas, x + 29, rowY + 6, width - 31, .8f, new SKColor(52, 49, 43, 72));
        }
    }

    private static string BuildSlotLabel(TeamCardSlot slot)
    {
        if (slot.Players.Count <= 1)
            return slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName;

        var names = string.Join(" + ", slot.Players.Take(2).Select(player => player.Name));
        return string.IsNullOrWhiteSpace(names) ? $"{slot.DisplayName} / SHARED" : $"{names} / SHARED";
    }

    private static void DrawPortraitFeature(
        SKCanvas canvas,
        SKRect rect,
        TeamCardPlayer? captain,
        string teamNumber,
        string teamName,
        SKColor accent,
        int index)
    {
        using (var basePaint = new SKPaint { Color = new SKColor(255, 255, 255, 62), IsAntialias = true })
            canvas.DrawRect(rect, basePaint);

        DrawCompressedText(
            canvas,
            teamNumber[^1..],
            rect.Left + 34,
            rect.Bottom - 26,
            rect.Height * .86f,
            PosterDrawing.WithAlpha(accent, 48),
            rect.Width - 40,
            PosterDrawing.BlackTypeface);

        if (captain is not null)
            DrawDuotoneAvatar(canvas, captain, rect, accent, index);
        else
            DrawMonogramFallback(canvas, rect, teamName, accent);

        using var frame = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 122),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        canvas.DrawRect(rect, frame);
    }

    private static void DrawDuotoneAvatar(SKCanvas canvas, TeamCardPlayer player, SKRect rect, SKColor accent, int index)
    {
        if (player.AvatarData is not { Length: > 0 })
        {
            PosterDrawing.DrawAvatar(canvas, player, rect, accent, PosterAvatarShape.Square, strongBorder: false, grayscale: true);
            DrawHalftoneOverlay(canvas, rect, accent, PosterDrawing.StableSeed(player.Name));
            return;
        }

        try
        {
            using var bitmap = SKBitmap.Decode(player.AvatarData);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                PosterDrawing.DrawAvatar(canvas, player, rect, accent, PosterAvatarShape.Square, false, true);
                DrawHalftoneOverlay(canvas, rect, accent, PosterDrawing.StableSeed(player.Name));
                return;
            }

            var save = canvas.Save();
            canvas.ClipRect(rect, SKClipOperation.Intersect, true);
            var source = CropToAspect(bitmap, rect.Width / rect.Height, index);
            using var gray = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                    .299f, .587f, .114f, 0, 0,
                    .299f, .587f, .114f, 0, 0,
                    .299f, .587f, .114f, 0, 0,
                    0, 0, 0, 1, 0
                })
            };
            canvas.DrawBitmap(bitmap, source, rect, gray);

            using var tint = new SKPaint
            {
                Color = PosterDrawing.WithAlpha(accent, 205),
                BlendMode = SKBlendMode.Multiply,
                IsAntialias = true
            };
            canvas.DrawRect(rect, tint);

            using var paperWash = new SKPaint
            {
                Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, 36),
                BlendMode = SKBlendMode.Screen,
                IsAntialias = true
            };
            canvas.DrawRect(rect, paperWash);
            DrawHalftoneOverlay(canvas, rect, accent, PosterDrawing.StableSeed(player.Name));
            canvas.RestoreToCount(save);
        }
        catch
        {
            PosterDrawing.DrawAvatar(canvas, player, rect, accent, PosterAvatarShape.Square, false, true);
            DrawHalftoneOverlay(canvas, rect, accent, PosterDrawing.StableSeed(player.Name));
        }
    }

    private static SKRectI CropToAspect(SKBitmap bitmap, float targetAspect, int teamIndex)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        if (sourceAspect > targetAspect)
        {
            var width = Math.Max(1, (int)(bitmap.Height * targetAspect));
            var centerBias = teamIndex switch { 0 => -.05f, 1 => .03f, _ => -.02f };
            var left = (int)((bitmap.Width - width) * (.5f + centerBias));
            left = Math.Clamp(left, 0, Math.Max(0, bitmap.Width - width));
            return new SKRectI(left, 0, left + width, bitmap.Height);
        }

        var height = Math.Max(1, (int)(bitmap.Width / Math.Max(.01f, targetAspect)));
        var top = Math.Clamp((bitmap.Height - height) / 3, 0, Math.Max(0, bitmap.Height - height));
        return new SKRectI(0, top, bitmap.Width, top + height);
    }

    private static void DrawHalftoneOverlay(SKCanvas canvas, SKRect rect, SKColor accent, int seed)
    {
        var random = new Random(seed & int.MaxValue);
        using var inkDot = new SKPaint { IsAntialias = true };
        using var paperDot = new SKPaint { IsAntialias = true };

        for (var y = rect.Top + 4; y < rect.Bottom; y += 8)
        {
            for (var x = rect.Left + 4; x < rect.Right; x += 8)
            {
                var jitterX = (float)(random.NextDouble() * 2.4 - 1.2);
                var jitterY = (float)(random.NextDouble() * 2.4 - 1.2);
                if (random.NextDouble() < .58)
                {
                    inkDot.Color = PosterDrawing.WithAlpha(PosterDrawing.Darken(accent, .52f), (byte)random.Next(18, 45));
                    canvas.DrawCircle(x + jitterX, y + jitterY, random.NextDouble() < .85 ? 1.05f : 1.7f, inkDot);
                }
                else if (random.NextDouble() < .36)
                {
                    paperDot.Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, (byte)random.Next(24, 62));
                    canvas.DrawCircle(x + jitterX, y + jitterY, .9f, paperDot);
                }
            }
        }
    }

    private static void DrawMonogramFallback(SKCanvas canvas, SKRect rect, string teamName, SKColor accent)
    {
        var words = (teamName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var monogram = words.Length == 0 ? "VD" : words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant() : string.Concat(words[0][0], words[^1][0]).ToUpperInvariant();
        PosterDrawing.DrawCenteredText(canvas, monogram, rect.MidX, rect.MidY + 64, 154, accent, true, rect.Width - 40, PosterDrawing.BlackTypeface);
        DrawHalftoneOverlay(canvas, rect, accent, PosterDrawing.StableSeed(teamName));
    }

    private static void DrawVolleyballStudy(SKCanvas canvas, float x, float y, float width, float height)
    {
        var center = new SKPoint(x + width * .52f, y + height * .42f);
        var radius = Math.Min(width, height) * .29f;

        using var shadow = new SKPaint
        {
            Color = new SKColor(30, 28, 24, 42),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10),
            IsAntialias = true
        };
        canvas.DrawOval(new SKRect(center.X - radius * 1.25f, center.Y + radius * .75f, center.X + radius * 1.35f, center.Y + radius * 1.22f), shadow);

        using var fill = new SKPaint { Color = new SKColor(232, 227, 216), IsAntialias = true };
        using var outline = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, IsAntialias = true };
        canvas.DrawCircle(center, radius, fill);
        canvas.DrawCircle(center, radius, outline);

        var ball = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        canvas.DrawArc(ball, -65, 122, false, outline);
        canvas.DrawArc(new SKRect(ball.Left - 22, ball.Top + 18, ball.Right - 12, ball.Bottom + 24), 15, 154, false, outline);
        canvas.DrawArc(new SKRect(ball.Left + 24, ball.Top - 30, ball.Right + 28, ball.Bottom - 14), 132, 144, false, outline);

        using var court = new SKPaint { Color = new SKColor(32, 31, 27, 132), StrokeWidth = 9, IsAntialias = true };
        canvas.DrawLine(x + 12, y + height * .67f, x + width - 8, y + height * .47f, court);
        canvas.DrawLine(center.X + 15, center.Y + 18, center.X + 15, y + height - 18, court);

        var random = new Random(1801);
        using var grain = new SKPaint { IsAntialias = true };
        for (var index = 0; index < 1550; index++)
        {
            var px = x + random.NextDouble() * width;
            var py = y + random.NextDouble() * height;
            var dx = px - center.X;
            var dy = py - center.Y;
            var insideBall = dx * dx + dy * dy <= radius * radius;
            if (!insideBall && random.NextDouble() > .28) continue;
            grain.Color = new SKColor(28, 27, 23, (byte)random.Next(18, insideBall ? 56 : 34));
            canvas.DrawCircle((float)px, (float)py, random.NextDouble() < .86 ? .72f : 1.25f, grain);
        }
    }

    private static void DrawFooter(SKCanvas canvas)
    {
        PosterDrawing.DrawText(canvas, "COURT INDEX  —  VOLLEY DRAFT  —  MATCH PROGRAM", 500, PosterDrawing.Height - 24, 11, Muted, true, 430);
        PosterDrawing.DrawText(canvas, "PRINTED WITH INTENT", PosterDrawing.Width - 240, PosterDrawing.Height - 24, 11, Muted, true, 190);
    }

    private static string BuildEditionDate(DateTimeOffset? startTime)
    {
        if (startTime is null) return "MATCHDAY / VOLLEY DRAFT";
        var local = startTime.Value.ToOffset(TimeSpan.FromHours(7));
        return local.ToString("dd / MM / yyyy", CultureInfo.InvariantCulture);
    }

    private static void DrawCompressedText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        float size,
        SKColor color,
        float targetWidth,
        SKTypeface typeface)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            TextSize = size,
            Typeface = typeface,
            SubpixelText = true
        };
        var measured = Math.Max(1, paint.MeasureText(text));
        var scaleX = Math.Min(1f, targetWidth / measured);
        var save = canvas.Save();
        canvas.Translate(x, baseline);
        canvas.Scale(scaleX, 1f);
        canvas.DrawText(text, 0, 0, paint);
        canvas.RestoreToCount(save);
    }

    private static void DrawRule(SKCanvas canvas, float x, float y, float width, float stroke, SKColor color)
    {
        using var paint = new SKPaint { Color = color, StrokeWidth = stroke, IsAntialias = true };
        canvas.DrawLine(x, y, x + width, y, paint);
    }

    private static void DrawVerticalText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool bold)
    {
        var save = canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateDegrees(-90);
        PosterDrawing.DrawText(canvas, text, 0, 0, size, color, bold, 410);
        canvas.RestoreToCount(save);
    }

    private static void DrawCropMark(SKCanvas canvas, float x, float y)
    {
        using var paint = new SKPaint { Color = Ink, StrokeWidth = 1.6f, IsAntialias = true };
        canvas.DrawLine(x - 18, y, x + 18, y, paint);
        canvas.DrawLine(x, y - 18, x, y + 18, paint);
        canvas.DrawLine(x - 9, y - 13, x - 9, y + 13, paint);
        canvas.DrawLine(x + 9, y - 13, x + 9, y + 13, paint);
    }

    private static void DrawRegistrationTarget(SKCanvas canvas, float x, float y, float radius)
    {
        using var paint = new SKPaint { Color = new SKColor(28, 27, 23, 190), StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle(x, y, radius, paint);
        canvas.DrawCircle(x, y, radius * .34f, paint);
        canvas.DrawLine(x - radius - 8, y, x + radius + 8, y, paint);
        canvas.DrawLine(x, y - radius - 8, x, y + radius + 8, paint);
    }

    private static void DrawColorBars(SKCanvas canvas, float x, float y)
    {
        SKColor[] colors = [Ink, new SKColor(190, 188, 180), Accents[0], Accents[1], Accents[2], Ink];
        for (var index = 0; index < colors.Length; index++)
        {
            using var paint = new SKPaint { Color = colors[index], IsAntialias = false };
            canvas.DrawRect(new SKRect(x, y + index * 24, x + 15, y + index * 24 + 23), paint);
        }
    }

    private static void DrawCalendarIcon(SKCanvas canvas, float x, float y)
    {
        using var paint = new SKPaint { Color = Ink, StrokeWidth = 1.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        var rect = new SKRect(x, y, x + 28, y + 24);
        canvas.DrawRect(rect, paint);
        canvas.DrawLine(x, y + 7, x + 28, y + 7, paint);
        canvas.DrawLine(x + 7, y - 3, x + 7, y + 5, paint);
        canvas.DrawLine(x + 21, y - 3, x + 21, y + 5, paint);
    }

    private static void DrawClockIcon(SKCanvas canvas, float x, float y)
    {
        using var paint = new SKPaint { Color = Ink, StrokeWidth = 1.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle(x + 14, y + 14, 13, paint);
        canvas.DrawLine(x + 14, y + 14, x + 14, y + 6, paint);
        canvas.DrawLine(x + 14, y + 14, x + 21, y + 18, paint);
    }
}
