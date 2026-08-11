using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Poster 01 — Court Index.
/// A neo-Swiss / risograph match-program composition inspired by the approved preview:
/// oversized editorial typography on warm paper, stacked team indexes, full-color captain
/// portraits, print-registration details, compact roster data and a prominent volleyball study.
/// The renderer remains fully data-driven and does not change poster assignment/rotation behavior.
/// </summary>
public static class CourtIndexPosterRenderer
{
    private static readonly SKColor Paper = new(246, 241, 231);
    private static readonly SKColor Ink = new(22, 22, 20);
    private static readonly SKColor Rule = new(48, 47, 42, 118);
    private static readonly SKColor Muted = new(84, 82, 74);
    private static readonly SKColor[] Accents =
    [
        new SKColor(25, 82, 196),
        new SKColor(230, 68, 38),
        new SKColor(26, 108, 66)
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

        PosterDrawing.DrawText(canvas, "OFFICIAL MATCH BALL", left + 5, 1084, 13, Muted, true, 220);
        DrawVolleyballStudy(canvas, 88, 1096, 455, 430);

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

        DrawPortraitFeature(canvas, portrait, captain, teamNumber, team.Name, accent);
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
        SKColor accent)
    {
        using (var basePaint = new SKPaint { Color = new SKColor(255, 255, 255, 78), IsAntialias = true })
            canvas.DrawRect(rect, basePaint);

        DrawCompressedText(
            canvas,
            teamNumber[^1..],
            rect.Left + 34,
            rect.Bottom - 26,
            rect.Height * .86f,
            PosterDrawing.WithAlpha(accent, 36),
            rect.Width - 40,
            PosterDrawing.BlackTypeface);

        if (captain is not null)
        {
            PosterDrawing.DrawAvatar(
                canvas,
                captain,
                rect,
                accent,
                PosterAvatarShape.Square,
                strongBorder: false,
                grayscale: false);
        }
        else
        {
            DrawMonogramFallback(canvas, rect, teamName, accent);
        }

        using var innerFrame = new SKPaint
        {
            Color = new SKColor(Paper.Red, Paper.Green, Paper.Blue, 135),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(rect.Left + 5, rect.Top + 5, rect.Right - 5, rect.Bottom - 5), innerFrame);

        using var frame = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 150),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true
        };
        canvas.DrawRect(rect, frame);
    }

    private static void DrawMonogramFallback(SKCanvas canvas, SKRect rect, string teamName, SKColor accent)
    {
        var words = (teamName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var monogram = words.Length == 0
            ? "VD"
            : words.Length == 1
                ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant()
                : string.Concat(words[0][0], words[^1][0]).ToUpperInvariant();

        PosterDrawing.DrawCenteredText(canvas, monogram, rect.MidX, rect.MidY + 64, 154, accent, true, rect.Width - 40, PosterDrawing.BlackTypeface);
    }

    private static void DrawVolleyballStudy(SKCanvas canvas, float x, float y, float width, float height)
    {
        var center = new SKPoint(x + width * .52f, y + height * .43f);
        var radius = Math.Min(width, height) * .36f;

        // Court marks sit behind the object so the ball reads as something resting on the court,
        // not as a symbol with lines painted over it.
        using (var court = new SKPaint
        {
            Color = new SKColor(32, 31, 27, 112),
            StrokeWidth = 8,
            StrokeCap = SKStrokeCap.Square,
            IsAntialias = true
        })
        {
            canvas.DrawLine(x + 2, y + height * .79f, x + width - 4, y + height * .60f, court);
            canvas.DrawLine(center.X + radius * .55f, center.Y + radius * .68f, center.X + radius * .55f, y + height - 4, court);
        }

        // Wide cast shadow + tight contact shadow give the sphere believable weight.
        using (var castShadow = new SKPaint
        {
            Color = new SKColor(25, 23, 20, 42),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 19),
            IsAntialias = true
        })
        {
            canvas.DrawOval(
                new SKRect(
                    center.X - radius * .80f,
                    center.Y + radius * .78f,
                    center.X + radius * 1.47f,
                    center.Y + radius * 1.16f),
                castShadow);
        }

        using (var contactShadow = new SKPaint
        {
            Color = new SKColor(24, 22, 19, 78),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6),
            IsAntialias = true
        })
        {
            canvas.DrawOval(
                new SKRect(
                    center.X - radius * .57f,
                    center.Y + radius * .83f,
                    center.X + radius * .70f,
                    center.Y + radius * 1.02f),
                contactShadow);
        }

        var ballSave = canvas.Save();
        canvas.Translate(center.X, center.Y);
        canvas.RotateDegrees(-14);
        canvas.Translate(-center.X, -center.Y);

        using var sphere = new SKPath();
        sphere.AddCircle(center.X, center.Y, radius);
        var ballRect = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

        using (var basePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(center.X - radius * .34f, center.Y - radius * .38f),
                radius * 1.42f,
                [new SKColor(255, 253, 246), new SKColor(232, 227, 217), new SKColor(184, 178, 166)],
                [0f, .66f, 1f],
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(center, radius, basePaint);
        }

        var clipSave = canvas.Save();
        canvas.ClipPath(sphere, SKClipOperation.Intersect, true);

        using var panelIvory = new SKPaint { Color = new SKColor(246, 242, 231, 238), IsAntialias = true };
        using var panelWarm = new SKPaint { Color = new SKColor(218, 213, 202, 232), IsAntialias = true };
        using var panelMid = new SKPaint { Color = new SKColor(142, 138, 130, 220), IsAntialias = true };
        using var panelGraphite = new SKPaint { Color = new SKColor(64, 63, 59, 222), IsAntialias = true };
        using var panelCharcoal = new SKPaint { Color = new SKColor(38, 38, 36, 224), IsAntialias = true };

        // Six filled panels, rather than thick decorative strokes. Their seams are based on the
        // recognizable volleyball topology used by Tabler's ball-volleyball icon: a center junction
        // with curved seams that sweep toward the circumference in six directions.
        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X - radius * .12f, center.Y - radius * .42f, center.X - radius * .20f, center.Y - radius * .76f, center.X - radius * .08f, center.Y - radius * 1.02f);
            panel.ArcTo(ballRect, -95, 67, false);
            panel.CubicTo(center.X + radius * .43f, center.Y - radius * .72f, center.X + radius * .31f, center.Y - radius * .24f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelIvory);
        }

        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X + radius * .33f, center.Y - radius * .28f, center.X + radius * .63f, center.Y - radius * .42f, center.X + radius * .91f, center.Y - radius * .43f);
            panel.ArcTo(ballRect, -28, 74, false);
            panel.CubicTo(center.X + radius * .76f, center.Y + radius * .28f, center.X + radius * .39f, center.Y + radius * .24f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelGraphite);
        }

        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X + radius * .41f, center.Y + radius * .24f, center.X + radius * .69f, center.Y + radius * .50f, center.X + radius * .72f, center.Y + radius * .72f);
            panel.ArcTo(ballRect, 46, 63, false);
            panel.CubicTo(center.X + radius * .27f, center.Y + radius * .83f, center.X + radius * .13f, center.Y + radius * .40f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelWarm);
        }

        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X + radius * .05f, center.Y + radius * .42f, center.X - radius * .18f, center.Y + radius * .73f, center.X - radius * .43f, center.Y + radius * .90f);
            panel.ArcTo(ballRect, 109, 61, false);
            panel.CubicTo(center.X - radius * .52f, center.Y + radius * .54f, center.X - radius * .31f, center.Y + radius * .16f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelCharcoal);
        }

        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X - radius * .32f, center.Y + radius * .17f, center.X - radius * .67f, center.Y + radius * .14f, center.X - radius * .98f, center.Y + radius * .02f);
            panel.ArcTo(ballRect, 170, 61, false);
            panel.CubicTo(center.X - radius * .74f, center.Y - radius * .31f, center.X - radius * .39f, center.Y - radius * .22f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelMid);
        }

        using (var panel = new SKPath())
        {
            panel.MoveTo(center.X, center.Y);
            panel.CubicTo(center.X - radius * .36f, center.Y - radius * .21f, center.X - radius * .56f, center.Y - radius * .50f, center.X - radius * .63f, center.Y - radius * .78f);
            panel.ArcTo(ballRect, 231, 34, false);
            panel.CubicTo(center.X - radius * .28f, center.Y - radius * .76f, center.X - radius * .12f, center.Y - radius * .38f, center.X, center.Y);
            panel.Close();
            canvas.DrawPath(panel, panelWarm);
        }

        // A shared spherical lighting pass unifies the flat panel fills into one three-dimensional ball.
        using (var highlight = new SKPaint
        {
            BlendMode = SKBlendMode.Screen,
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(center.X - radius * .38f, center.Y - radius * .43f),
                radius * .95f,
                [new SKColor(255, 255, 250, 110), new SKColor(255, 255, 250, 0)],
                [0f, 1f],
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(center, radius, highlight);
        }

        using (var shade = new SKPaint
        {
            BlendMode = SKBlendMode.Multiply,
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(center.X + radius * .42f, center.Y + radius * .52f),
                radius * 1.10f,
                [new SKColor(80, 75, 68, 0), new SKColor(67, 62, 57, 82)],
                [0f, 1f],
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(center, radius, shade);
        }

        canvas.RestoreToCount(clipSave);

        using var seam = new SKPaint
        {
            Color = new SKColor(31, 30, 27, 215),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.0f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        // Central seam -> lower-right, mirroring the first recognizable seam from Tabler.
        using (var path = new SKPath())
        {
            path.MoveTo(center.X, center.Y);
            path.CubicTo(center.X + radius * .22f, center.Y + radius * .18f, center.X + radius * .53f, center.Y + radius * .40f, center.X + radius * .86f, center.Y + radius * .46f);
            canvas.DrawPath(path, seam);
        }

        using (var path = new SKPath())
        {
            path.MoveTo(center.X - radius * .44f, center.Y + radius * .22f);
            path.CubicTo(center.X - radius * .16f, center.Y + radius * .61f, center.X + radius * .12f, center.Y + radius * .80f, center.X + radius * .48f, center.Y + radius * .88f);
            canvas.DrawPath(path, seam);
        }

        // Central seam -> lower-left.
        using (var path = new SKPath())
        {
            path.MoveTo(center.X, center.Y);
            path.CubicTo(center.X - radius * .30f, center.Y + radius * .10f, center.X - radius * .65f, center.Y + radius * .16f, center.X - radius * .91f, center.Y + radius * .48f);
            canvas.DrawPath(path, seam);
        }

        // Upper-left sweep toward center.
        using (var path = new SKPath())
        {
            path.MoveTo(center.X - radius * .94f, center.Y - radius * .10f);
            path.CubicTo(center.X - radius * .67f, center.Y - radius * .45f, center.X - radius * .28f, center.Y - radius * .46f, center.X, center.Y);
            canvas.DrawPath(path, seam);
        }

        // Top seam into the central junction.
        using (var path = new SKPath())
        {
            path.MoveTo(center.X - radius * .08f, center.Y - radius * .98f);
            path.CubicTo(center.X + radius * .08f, center.Y - radius * .70f, center.X + radius * .13f, center.Y - radius * .31f, center.X, center.Y);
            canvas.DrawPath(path, seam);
        }

        // Right-side sweep gives the ball the characteristic segmented volleyball shell.
        using (var path = new SKPath())
        {
            path.MoveTo(center.X + radius * .55f, center.Y - radius * .80f);
            path.CubicTo(center.X + radius * .83f, center.Y - radius * .42f, center.X + radius * .81f, center.Y + radius * .04f, center.X + radius * .59f, center.Y + radius * .50f);
            canvas.DrawPath(path, seam);
        }

        using var outline = new SKPaint
        {
            Color = new SKColor(25, 24, 22, 235),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.4f,
            IsAntialias = true
        };
        canvas.DrawCircle(center, radius, outline);

        // Halftone density follows the sphere's light direction: sparse at upper-left, denser in
        // lower-right shadow. This keeps the print character without flattening the object.
        var random = new Random(1801);
        using var grain = new SKPaint { IsAntialias = true };
        for (var index = 0; index < 2200; index++)
        {
            var angle = random.NextDouble() * Math.PI * 2;
            var distance = Math.Sqrt(random.NextDouble()) * radius * .985f;
            var px = center.X + Math.Cos(angle) * distance;
            var py = center.Y + Math.Sin(angle) * distance;
            var nx = (px - center.X) / radius;
            var ny = (py - center.Y) / radius;
            var shadowFactor = Math.Clamp((nx + ny + 1.15) / 3.15, 0.12, 0.78);
            if (random.NextDouble() > shadowFactor)
                continue;

            grain.Color = new SKColor(22, 21, 19, (byte)random.Next(12, 49));
            canvas.DrawCircle((float)px, (float)py, random.NextDouble() < .90 ? .68f : 1.18f, grain);
        }

        canvas.RestoreToCount(ballSave);
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
        using var paint = new SKPaint
        {
            Color = new SKColor(28, 27, 23, 190),
            StrokeWidth = 1.2f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
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
