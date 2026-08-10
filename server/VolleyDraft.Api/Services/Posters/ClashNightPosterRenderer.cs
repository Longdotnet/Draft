using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

internal static class ClashNightPosterRenderer
{
    private static readonly SKColor Night = new(5, 6, 9);
    private static readonly SKColor Steel = new(215, 213, 205);
    private static readonly SKColor White = new(247, 247, 244);
    private static readonly SKColor Red = new(231, 53, 48);
    private static readonly SKColor Blue = new(43, 171, 235);
    private static readonly SKColor Gold = new(241, 174, 61);
    private static readonly SKColor Muted = new(145, 150, 158);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Night);
        var canvas = surface.Canvas;
        DrawFightNightBackdrop(canvas, sessionName);
        DrawStadiumLights(canvas);

        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT PRESENTS", 720, 62, 15, PosterDrawing.WithAlpha(White, 175), true, 600);
        DrawDistressedTitle(canvas, "CLASH", 720, 172, 120);
        DrawDistressedTitle(canvas, "NIGHT", 720, 286, 120);
        DrawSlash(canvas, 390, 176, 1050, 290);
        DrawMainEventBanner(canvas, 720, 356);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 413, 17, PosterDrawing.WithAlpha(White, 170), false, 1120);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            DrawCenterShield(canvas, "3 TEAM", "CLASH", "ONE NIGHT", 720, 820);
            PosterDrawing.DrawCenteredText(canvas, "WAITING FOR THE FIGHT CARD", 720, 1130, 43, White, true, 1100, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 1182, 18, Muted, false, 820);
            DrawFooter(canvas);
            return PosterDrawing.Encode(surface);
        }

        if (visible.Count > 0)
            DrawSideTeam(canvas, visible[0], 0, Red, new SKRect(28, 500, 575, 1318), false);
        if (visible.Count > 1)
            DrawSideTeam(canvas, visible[1], 1, Blue, new SKRect(865, 500, 1412, 1318), true);

        DrawCenterShield(canvas, "3 TEAM", "CLASH", "ONE NIGHT", 720, 620);

        if (visible.Count > 2)
            DrawCenterTeam(canvas, visible[2], Gold, new SKRect(350, 870, 1090, 1572));
        else if (visible.Count == 1)
            DrawCenterTeam(canvas, visible[0], Gold, new SKRect(350, 800, 1090, 1510));

        DrawEventBadges(canvas);
        DrawFooter(canvas);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawSideTeam(SKCanvas canvas, TeamCardTeam team, int index, SKColor accent, SKRect zone, bool mirror)
    {
        DrawWing(canvas, zone, accent, mirror);
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var titleX = mirror ? zone.Right - 18 : zone.Left + 18;
        var align = mirror ? SKTextAlign.Right : SKTextAlign.Left;

        PosterDrawing.DrawText(canvas, $"TEAM 0{index + 1}", titleX, zone.Top + 38, 17, accent, true, zone.Width - 40, null, align);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), titleX, zone.Top + 122, 62, accent, true, zone.Width - 28, PosterDrawing.BlackTypeface, align);

        var avatarSize = 232f;
        var avatarLeft = mirror ? zone.Right - avatarSize - 46 : zone.Left + 46;
        var avatarTop = zone.Top + 165;
        var avatarRect = new SKRect(avatarLeft, avatarTop, avatarLeft + avatarSize, avatarTop + avatarSize);
        DrawFightAvatar(canvas, captain, avatarRect, accent);

        var infoY = avatarRect.Bottom + 52;
        DrawCaptainTag(canvas, titleX, infoY - 24, accent, align);
        PosterDrawing.DrawText(canvas, captain.Name.ToUpperInvariant(), titleX, infoY + 27, 34, White, true, zone.Width - 60, PosterDrawing.BlackTypeface, align);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", titleX, infoY + 61, 13, accent, true, zone.Width - 60, null, align);

        var lineY = infoY + 101;
        using (var rule = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 145), StrokeWidth = 2, IsAntialias = true })
        {
            var x1 = mirror ? zone.Right - 300 : zone.Left + 20;
            var x2 = mirror ? zone.Right - 20 : zone.Left + 300;
            canvas.DrawLine(x1, lineY, x2, lineY, rule);
        }
        PosterDrawing.DrawText(canvas, "LINEUP", titleX, lineY + 30, 14, accent, true, 180, null, align);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var y = lineY + 52;
        for (var i = 0; i < slots.Count; i += 1)
        {
            var slot = slots[i];
            var label = slot.Players.Count > 1
                ? string.Join(" + ", slot.Players.Select(p => p.Name.ToUpperInvariant()))
                : (slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName).ToUpperInvariant();
            DrawLineupSlat(canvas, new SKRect(zone.Left + 22, y, zone.Right - 22, y + 44), label, accent, mirror, slot.Players.Count > 1);
            y += 50;
        }
    }

    private static void DrawCenterTeam(SKCanvas canvas, TeamCardTeam team, SKColor accent, SKRect zone)
    {
        DrawCenterPlate(canvas, zone, accent);
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        PosterDrawing.DrawCenteredText(canvas, "TEAM 03", 720, zone.Top + 40, 18, accent, true, 240);
        PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), 720, zone.Top + 125, 80, accent, true, 720, PosterDrawing.BlackTypeface);

        var avatarRect = new SKRect(585, zone.Top + 155, 855, zone.Top + 425);
        DrawFightAvatar(canvas, captain, avatarRect, accent);
        DrawCaptainTag(canvas, 720, avatarRect.Bottom + 32, accent, SKTextAlign.Center);
        PosterDrawing.DrawCenteredText(canvas, captain.Name.ToUpperInvariant(), 720, avatarRect.Bottom + 82, 41, White, true, 520, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", 720, avatarRect.Bottom + 115, 14, accent, true, 530);
        PosterDrawing.DrawCenteredText(canvas, "LINEUP", 720, avatarRect.Bottom + 156, 14, accent, true, 180);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        const float cellW = 210;
        const float gap = 14;
        var gridLeft = 720 - (cellW * 3 + gap * 2) / 2;
        var gridTop = avatarRect.Bottom + 175;
        for (var i = 0; i < slots.Count; i += 1)
        {
            var row = i / 3;
            var col = i % 3;
            var slot = slots[i];
            var label = slot.Players.Count > 1
                ? string.Join(" + ", slot.Players.Select(p => p.Name.ToUpperInvariant()))
                : (slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName).ToUpperInvariant();
            var rect = new SKRect(gridLeft + col * (cellW + gap), gridTop + row * 54, gridLeft + col * (cellW + gap) + cellW, gridTop + row * 54 + 44);
            DrawCenterRosterCell(canvas, rect, label, accent, slot.Players.Count > 1);
        }
    }

    private static void DrawFightNightBackdrop(SKCanvas canvas, string sessionName)
    {
        using (var gradient = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0), new SKPoint(0, 1800),
                       [new SKColor(8, 8, 10), new SKColor(14, 14, 18), new SKColor(3, 3, 5)],
                       [0f, .55f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, 1440, 1800), gradient);

        DrawSideGlow(canvas, new SKPoint(80, 680), Red, 700);
        DrawSideGlow(canvas, new SKPoint(1360, 680), Blue, 700);
        DrawSideGlow(canvas, new SKPoint(720, 1260), Gold, 660);

        var random = new Random(PosterDrawing.StableSeed(sessionName + "clash-night") & int.MaxValue);
        for (var i = 0; i < 420; i += 1)
        {
            var x = random.Next(0, 1440);
            var y = random.Next(80, 1740);
            var r = random.Next(1, 4);
            var hot = random.NextDouble() > .72;
            var color = hot
                ? new SKColor(255, (byte)random.Next(70, 180), 30, (byte)random.Next(18, 70))
                : new SKColor(220, 225, 230, (byte)random.Next(6, 30));
            using var particle = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawCircle(x, y, r, particle);
        }

        using var vignette = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(720, 850), 1000,
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 210)], [0f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, 1440, 1800), vignette);
    }

    private static void DrawStadiumLights(SKCanvas canvas)
    {
        for (var side = 0; side < 2; side += 1)
        {
            var x = side == 0 ? 44 : 1396;
            var color = side == 0 ? Red : Blue;
            for (var i = 0; i < 8; i += 1)
            {
                var y = 48 + i * 38;
                using var glow = new SKPaint { Color = PosterDrawing.WithAlpha(color, 45), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10), IsAntialias = true };
                canvas.DrawCircle(x, y, 12, glow);
                using var core = new SKPaint { Color = PosterDrawing.WithAlpha(White, 220), IsAntialias = true };
                canvas.DrawCircle(x, y, 4, core);
            }
        }
    }

    private static void DrawDistressedTitle(SKCanvas canvas, string text, float x, float y, float size)
    {
        PosterDrawing.DrawCenteredText(canvas, text, x + 4, y + 7, size, new SKColor(0, 0, 0, 170), true, 1120, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, text, x, y, size, Steel, true, 1120, PosterDrawing.BlackTypeface);
        using var scratch = new SKPaint { Color = new SKColor(20, 20, 20, 90), StrokeWidth = 3, IsAntialias = true };
        for (var i = -5; i <= 5; i += 1)
            canvas.DrawLine(360 + i * 48, y - size * .36f, 420 + i * 48, y + size * .18f, scratch);
    }

    private static void DrawSlash(SKCanvas canvas, float x1, float y1, float x2, float y2)
    {
        using var glow = new SKPaint { Color = new SKColor(255, 76, 31, 75), StrokeWidth = 18, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9), IsAntialias = true };
        using var core = new SKPaint { Color = new SKColor(255, 79, 29), StrokeWidth = 4, IsAntialias = true };
        canvas.DrawLine(x1, y1, x2, y2, glow);
        canvas.DrawLine(x1, y1, x2, y2, core);
    }

    private static void DrawMainEventBanner(SKCanvas canvas, float centerX, float centerY)
    {
        using var path = new SKPath();
        path.MoveTo(centerX - 300, centerY - 27);
        path.LineTo(centerX + 282, centerY - 27);
        path.LineTo(centerX + 310, centerY);
        path.LineTo(centerX + 282, centerY + 27);
        path.LineTo(centerX - 300, centerY + 27);
        path.LineTo(centerX - 326, centerY);
        path.Close();
        using var paint = new SKPaint { Color = new SKColor(135, 22, 22, 235), IsAntialias = true };
        canvas.DrawPath(path, paint);
        PosterDrawing.DrawCenteredText(canvas, "SUNDAY MAIN EVENT", centerX, centerY + 11, 31, White, true, 560, PosterDrawing.BlackTypeface);
    }

    private static void DrawWing(SKCanvas canvas, SKRect zone, SKColor accent, bool mirror)
    {
        using var path = new SKPath();
        if (!mirror)
        {
            path.MoveTo(zone.Left, zone.Top + 70);
            path.LineTo(zone.Right - 60, zone.Top);
            path.LineTo(zone.Right, zone.Bottom - 90);
            path.LineTo(zone.Left, zone.Bottom);
        }
        else
        {
            path.MoveTo(zone.Left + 60, zone.Top);
            path.LineTo(zone.Right, zone.Top + 70);
            path.LineTo(zone.Right, zone.Bottom);
            path.LineTo(zone.Left, zone.Bottom - 90);
        }
        path.Close();
        using var fill = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(zone.Left, zone.Top), new SKPoint(zone.Right, zone.Bottom),
                [PosterDrawing.WithAlpha(accent, 110), new SKColor(7, 8, 11, 235)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, border);
        using var streak = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 50), StrokeWidth = 7, IsAntialias = true };
        for (var i = 0; i < 5; i += 1)
        {
            var y = zone.Top + 55 + i * 55;
            var dx = mirror ? -155 : 155;
            canvas.DrawLine(mirror ? zone.Right : zone.Left, y, (mirror ? zone.Right : zone.Left) + dx, y - 36, streak);
        }
    }

    private static void DrawCenterPlate(SKCanvas canvas, SKRect zone, SKColor accent)
    {
        using var path = new SKPath();
        path.MoveTo(zone.Left + 85, zone.Top);
        path.LineTo(zone.Right - 85, zone.Top);
        path.LineTo(zone.Right, zone.Bottom - 80);
        path.LineTo(zone.Right - 48, zone.Bottom);
        path.LineTo(zone.Left + 48, zone.Bottom);
        path.LineTo(zone.Left, zone.Bottom - 80);
        path.Close();
        using var fill = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(zone.MidX, zone.Top), new SKPoint(zone.MidX, zone.Bottom),
                [new SKColor(58, 38, 10, 225), new SKColor(12, 10, 8, 245)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, border);
    }

    private static void DrawFightAvatar(SKCanvas canvas, TeamCardPlayer captain, SKRect rect, SKColor accent)
    {
        using var glow = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 100), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 24), IsAntialias = true };
        canvas.DrawOval(new SKRect(rect.Left - 18, rect.Top - 18, rect.Right + 18, rect.Bottom + 18), glow);
        PosterDrawing.DrawAvatar(canvas, captain, rect, accent, PosterAvatarShape.Circle, true);
        using var ring = new SKPaint { Color = PosterDrawing.WithAlpha(White, 100), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawOval(new SKRect(rect.Left - 7, rect.Top - 7, rect.Right + 7, rect.Bottom + 7), ring);
    }

    private static void DrawCaptainTag(SKCanvas canvas, float x, float y, SKColor accent, SKTextAlign align)
    {
        var width = 92f;
        var rect = align switch
        {
            SKTextAlign.Right => new SKRect(x - width, y - 18, x, y + 16),
            SKTextAlign.Center => new SKRect(x - width / 2, y - 18, x + width / 2, y + 16),
            _ => new SKRect(x, y - 18, x + width, y + 16)
        };
        PosterDrawing.DrawPill(canvas, "CAPTAIN", rect, PosterDrawing.WithAlpha(accent, 40), accent, PosterDrawing.WithAlpha(accent, 150), 10);
    }

    private static void DrawLineupSlat(SKCanvas canvas, SKRect rect, string label, SKColor accent, bool mirror, bool shared)
    {
        using var path = new SKPath();
        if (!mirror)
        {
            path.MoveTo(rect.Left + 12, rect.Top);
            path.LineTo(rect.Right, rect.Top);
            path.LineTo(rect.Right - 18, rect.Bottom);
            path.LineTo(rect.Left, rect.Bottom);
        }
        else
        {
            path.MoveTo(rect.Left, rect.Top);
            path.LineTo(rect.Right - 12, rect.Top);
            path.LineTo(rect.Right, rect.Bottom);
            path.LineTo(rect.Left + 18, rect.Bottom);
        }
        path.Close();
        using var fill = new SKPaint { Color = new SKColor(accent.Red, accent.Green, accent.Blue, 28), IsAntialias = true };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 70), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, border);
        var x = mirror ? rect.Right - 22 : rect.Left + 22;
        PosterDrawing.DrawText(canvas, label, x, rect.MidY + 6, 15, White, true, rect.Width - 44, null, mirror ? SKTextAlign.Right : SKTextAlign.Left);
        if (shared)
            PosterDrawing.DrawText(canvas, "SHARED", mirror ? rect.Left + 12 : rect.Right - 12, rect.Top + 12, 8, accent, true, 70, null, mirror ? SKTextAlign.Left : SKTextAlign.Right);
    }

    private static void DrawCenterRosterCell(SKCanvas canvas, SKRect rect, string label, SKColor accent, bool shared)
    {
        using var fill = new SKPaint { Color = new SKColor(68, 45, 15, 210), IsAntialias = true };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 85), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        PosterDrawing.DrawCutCornerPanel(canvas, rect, 8, fill);
        PosterDrawing.DrawCutCornerPanel(canvas, rect, 8, border);
        PosterDrawing.DrawCenteredText(canvas, label, rect.MidX, rect.MidY + 6, 14, White, true, rect.Width - 18);
        if (shared) PosterDrawing.DrawText(canvas, "S", rect.Right - 9, rect.Top + 10, 8, accent, true, 16, null, SKTextAlign.Right);
    }

    private static void DrawCenterShield(SKCanvas canvas, string top, string middle, string bottom, float x, float y)
    {
        var rect = new SKRect(x - 118, y - 72, x + 118, y + 96);
        using var path = new SKPath();
        path.MoveTo(rect.Left + 25, rect.Top);
        path.LineTo(rect.Right - 25, rect.Top);
        path.LineTo(rect.Right, rect.Top + 28);
        path.LineTo(rect.Right - 18, rect.Bottom - 35);
        path.LineTo(x, rect.Bottom);
        path.LineTo(rect.Left + 18, rect.Bottom - 35);
        path.LineTo(rect.Left, rect.Top + 28);
        path.Close();
        using var fill = new SKPaint { Color = new SKColor(25, 26, 28, 238), IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(170, 172, 175, 145), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, border);
        PosterDrawing.DrawCenteredText(canvas, top, x, y - 29, 18, Steel, true, 170);
        PosterDrawing.DrawCenteredText(canvas, middle, x, y + 11, 34, White, true, 190, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, bottom, x, y + 43, 12, Muted, true, 160);
    }

    private static void DrawEventBadges(SKCanvas canvas)
    {
        DrawBadge(canvas, new SKRect(35, 1555, 230, 1670), "MAIN", "EVENT", Red);
        DrawBadge(canvas, new SKRect(1210, 1555, 1405, 1670), "MATCH", "DAY", Blue);
    }

    private static void DrawBadge(SKCanvas canvas, SKRect rect, string line1, string line2, SKColor accent)
    {
        using var fill = new SKPaint { Color = new SKColor(16, 17, 19, 235), IsAntialias = true };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 135), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        PosterDrawing.DrawCutCornerPanel(canvas, rect, 15, fill);
        PosterDrawing.DrawCutCornerPanel(canvas, rect, 15, border);
        PosterDrawing.DrawCenteredText(canvas, line1, rect.MidX, rect.Top + 45, 24, Steel, true, rect.Width - 24, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, line2, rect.MidX, rect.Top + 76, 24, Steel, true, rect.Width - 24, PosterDrawing.BlackTypeface);
        for (var i = 0; i < 4; i += 1)
            PosterDrawing.DrawCenteredText(canvas, "★", rect.Left + 55 + i * 28, rect.Bottom - 17, 13, accent, true, 20);
    }

    private static void DrawSideGlow(SKCanvas canvas, SKPoint center, SKColor color, float radius)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(center, radius,
                [PosterDrawing.WithAlpha(color, 75), PosterDrawing.WithAlpha(color, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawCircle(center.X, center.Y, radius, paint);
    }

    private static void DrawFooter(SKCanvas canvas)
    {
        PosterDrawing.DrawCenteredText(canvas, "ONE COURT   •   THREE TEAMS   •   NO RETAKES", 720, 1735, 15, PosterDrawing.WithAlpha(White, 175), true, 980);
    }
}
