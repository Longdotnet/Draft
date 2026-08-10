using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

internal static class InfernoClashPosterRenderer
{
    private static readonly SKColor Flame = new(255, 77, 28);
    private static readonly SKColor Ember = new(255, 171, 54);
    private static readonly SKColor Ink = new(255, 242, 232);
    private static readonly SKColor Muted = new(185, 137, 118);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(new SKColor(14, 3, 2));
        var canvas = surface.Canvas;
        using (var bg = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1440, 1800),
                       [new SKColor(8, 4, 4), new SKColor(50, 8, 3), new SKColor(12, 2, 2)],
                       [0f, .48f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, 1440, 1800), bg);
        DrawEmbers(canvas, sessionName);
        DrawSlash(canvas, new SKRect(-180, 90, 880, 240), PosterDrawing.WithAlpha(Flame, 42));
        DrawSlash(canvas, new SKRect(760, 1600, 1560, 1735), PosterDrawing.WithAlpha(Ember, 32));

        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT", 62, 78, 18, Ember, true, 300);
        PosterDrawing.DrawText(canvas, "CLASH", 58, 190, 96, Ink, true, 620, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 64, 248, 30, Flame, true, 1040);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 66, 292, 19, Muted, false, 1040);
        PosterDrawing.DrawText(canvas, "NO SAFE ZONE", 1360, 188, 18, PosterDrawing.WithAlpha(Ember, 190), true, 260, null, SKTextAlign.Right);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "WAITING FOR THE FIGHT", 720, 900, 52, Ember, true, 1000, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var y = 350f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawClashBand(canvas, new SKRect(40, y, 1400, y + 410), visible[i], i, i % 2 == 1);
            y += 445;
        }
        PosterDrawing.DrawText(canvas, "MATCHDAY // AUTO DRAFT RESULT", 62, 1730, 12, Muted, true, 400);
        PosterDrawing.DrawText(canvas, "BURN THE BRACKET", 1370, 1730, 12, Ember, true, 300, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawClashBand(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index, bool reverse)
    {
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18), IsAntialias = true })
            DrawSlash(canvas, new SKRect(rect.Left, rect.Top + 10, rect.Right, rect.Bottom + 10), shadow.Color);
        DrawSlash(canvas, rect, index == 1 ? new SKColor(36, 6, 4, 245) : new SKColor(26, 8, 5, 247));
        using (var edge = new SKPaint { Color = index == 1 ? Ember : Flame, StrokeWidth = 4, IsAntialias = true })
        {
            if (!reverse) canvas.DrawLine(rect.Left + 80, rect.Top + 14, rect.Right - 30, rect.Top + 14, edge);
            else canvas.DrawLine(rect.Left + 30, rect.Bottom - 14, rect.Right - 80, rect.Bottom - 14, edge);
        }

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var heroX = reverse ? rect.Right - 275 : rect.Left + 68;
        var avatarRect = new SKRect(heroX, rect.Top + 82, heroX + 190, rect.Top + 272);
        PosterDrawing.DrawAvatar(canvas, captain, avatarRect, index == 1 ? Ember : Flame, PosterAvatarShape.RoundedSquare, true);
        PosterDrawing.DrawText(canvas, "CAPTAIN", heroX, rect.Top + 312, 11, index == 1 ? Ember : Flame, true, 100);
        PosterDrawing.DrawText(canvas, captain.Name, heroX, rect.Top + 344, 20, Ink, true, 250);

        var titleX = reverse ? rect.Left + 70 : rect.Left + 320;
        var titleWidth = reverse ? 720 : 720;
        PosterDrawing.DrawText(canvas, $"0{index + 1}", titleX, rect.Top + 82, 68, PosterDrawing.WithAlpha(Flame, 80), true, 110, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), titleX + 105, rect.Top + 78, 37, Ink, true, titleWidth, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}  //  {PosterDrawing.PlayerCount(team)} FIGHTERS", titleX + 108, rect.Top + 112, 13, Muted, true, 430);

        var rosterLeft = reverse ? rect.Left + 70 : rect.Left + 320;
        var rosterTop = rect.Top + 160;
        var slots = PosterDrawing.VisibleSlots(team, 6);
        for (var i = 0; i < slots.Count; i++)
        {
            var row = i / 3;
            var col = i % 3;
            var cell = new SKRect(rosterLeft + col * 270, rosterTop + row * 92, rosterLeft + col * 270 + 250, rosterTop + row * 92 + 72);
            DrawFighterCell(canvas, cell, slots[i], i, index == 1 ? Ember : Flame);
        }
    }

    private static void DrawFighterCell(SKCanvas canvas, SKRect rect, TeamCardSlot slot, int index, SKColor accent)
    {
        using (var fill = new SKPaint { Color = new SKColor(255, 255, 255, 9), IsAntialias = true }) canvas.DrawRect(rect, fill);
        using (var line = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 92), StrokeWidth = 2 }) canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, line);
        var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
        PosterDrawing.DrawAvatar(canvas, player, new SKRect(rect.Left + 4, rect.Top + 7, rect.Left + 60, rect.Top + 63), accent, PosterAvatarShape.Circle);
        var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : player.Name;
        PosterDrawing.DrawText(canvas, name, rect.Left + 72, rect.Top + 35, 16, Ink, true, rect.Width - 80);
        PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "SHARED" : $"FIGHTER {index + 1:00}", rect.Left + 72, rect.Top + 57, 9, slot.Players.Count > 1 ? accent : Muted, true, 130);
    }

    private static void DrawSlash(SKCanvas canvas, SKRect rect, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(rect.Left + 70, rect.Top);
        path.LineTo(rect.Right, rect.Top);
        path.LineTo(rect.Right - 70, rect.Bottom);
        path.LineTo(rect.Left, rect.Bottom);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    private static void DrawEmbers(SKCanvas canvas, string sessionName)
    {
        var random = new Random(PosterDrawing.StableSeed(sessionName) & int.MaxValue);
        for (var i = 0; i < 125; i++)
        {
            var x = random.Next(0, 1440);
            var y = random.Next(250, 1750);
            var size = random.Next(1, 5);
            using var ember = new SKPaint { Color = new SKColor(255, random.Next(80, 190), 35, random.Next(20, 95)), IsAntialias = true };
            canvas.DrawCircle(x, y, size, ember);
        }
    }
}

internal static class RetroArcadePosterRenderer
{
    private static readonly SKColor Navy = new(5, 8, 34);
    private static readonly SKColor Cyan = new(45, 245, 255);
    private static readonly SKColor Magenta = new(255, 67, 220);
    private static readonly SKColor Yellow = new(255, 225, 74);
    private static readonly SKColor Ink = new(244, 247, 255);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Navy);
        var canvas = surface.Canvas;
        using (var sky = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 900),
                       [new SKColor(10, 7, 42), new SKColor(72, 11, 83), new SKColor(6, 12, 47)],
                       [0f, .62f, 1f], SKShaderTileMode.Clamp)
               }) canvas.DrawRect(new SKRect(0, 0, 1440, 1800), sky);
        DrawSynthGrid(canvas);
        PosterDrawing.DrawCenteredText(canvas, "VOLLEY // DRAFT", 720, 88, 22, Cyan, true, 700);
        PosterDrawing.DrawCenteredText(canvas, "ARCADE LINEUP", 720, 176, 67, Yellow, true, 1060, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, sessionName.ToUpperInvariant(), 720, 232, 25, Magenta, true, 1100);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 276, 17, new SKColor(184, 190, 227), false, 1120);

        using (var sun = new SKPaint { Color = new SKColor(255, 89, 177, 70), IsAntialias = true }) canvas.DrawCircle(720, 392, 92, sun);
        for (var y = 340; y < 455; y += 16)
        {
            using var cut = new SKPaint { Color = Navy, StrokeWidth = 7 };
            canvas.DrawLine(620, y, 820, y, cut);
        }

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "INSERT DRAFT TO CONTINUE", 720, 970, 38, Cyan, true, 1000, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var yStart = 505f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawArcadeBoard(canvas, new SKRect(90, yStart, 1350, yStart + 340), visible[i], i);
            yStart += 372;
        }
        PosterDrawing.DrawCenteredText(canvas, "PRESS @BOT 10 TO VIEW • STYLE LOCKED FOR THIS MATCHDAY", 720, 1718, 13, new SKColor(154, 160, 205), true, 1100);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawArcadeBoard(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var accent = index switch { 0 => Cyan, 1 => Magenta, _ => Yellow };
        using (var outer = new SKPaint { Color = new SKColor(6, 10, 35, 244), IsAntialias = false }) canvas.DrawRect(rect, outer);
        using (var border = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = false }) canvas.DrawRect(rect, border);
        using (var inner = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 75), Style = SKPaintStyle.Stroke, StrokeWidth = 1 }) canvas.DrawRect(new SKRect(rect.Left + 9, rect.Top + 9, rect.Right - 9, rect.Bottom - 9), inner);

        PosterDrawing.DrawText(canvas, $"P{index + 1}", rect.Left + 24, rect.Top + 55, 31, accent, true, 90, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 104, rect.Top + 55, 31, Ink, true, 520, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"SCORE {PosterDrawing.TeamScore(team)}", rect.Right - 30, rect.Top + 54, 20, Yellow, true, 180, null, SKTextAlign.Right);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(rect.Left + 28, rect.Top + 92, rect.Left + 172, rect.Top + 236);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, accent, PosterAvatarShape.Square, true);
        PosterDrawing.DrawText(canvas, "★ CAPTAIN", rect.Left + 28, rect.Top + 265, 11, Yellow, true, 120);
        PosterDrawing.DrawText(canvas, captain.Name.ToUpperInvariant(), rect.Left + 28, rect.Top + 291, 16, Ink, true, 260);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        for (var i = 0; i < slots.Count; i++)
        {
            var col = i % 3;
            var row = i / 3;
            var cell = new SKRect(rect.Left + 320 + col * 300, rect.Top + 92 + row * 104, rect.Left + 598 + col * 300, rect.Top + 180 + row * 104);
            DrawPixelPlayer(canvas, cell, slots[i], i, accent);
        }
        PosterDrawing.DrawText(canvas, $"READY {PosterDrawing.PlayerCount(team):00}/{team.Slots.Count:00}", rect.Right - 28, rect.Bottom - 21, 10, accent, true, 180, null, SKTextAlign.Right);
    }

    private static void DrawPixelPlayer(SKCanvas canvas, SKRect rect, TeamCardSlot slot, int index, SKColor accent)
    {
        using (var fill = new SKPaint { Color = new SKColor(16, 20, 62), IsAntialias = false }) canvas.DrawRect(rect, fill);
        using (var edge = new SKPaint { Color = new SKColor(255, 255, 255, 24), Style = SKPaintStyle.Stroke, StrokeWidth = 1 }) canvas.DrawRect(rect, edge);
        var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
        PosterDrawing.DrawAvatar(canvas, player, new SKRect(rect.Left + 8, rect.Top + 10, rect.Left + 72, rect.Top + 74), accent, PosterAvatarShape.Square);
        var name = slot.Players.Count > 1 ? string.Join("+", slot.Players.Select(p => p.Name)) : player.Name;
        PosterDrawing.DrawText(canvas, $"> {name.ToUpperInvariant()}", rect.Left + 82, rect.Top + 42, 14, Ink, true, rect.Width - 92);
        PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "CO-OP SLOT" : $"SLOT {index + 1:00}", rect.Left + 82, rect.Top + 66, 9, slot.Players.Count > 1 ? Yellow : accent, true, 130);
    }

    private static void DrawSynthGrid(SKCanvas canvas)
    {
        using var grid = new SKPaint { Color = new SKColor(59, 232, 255, 32), StrokeWidth = 1 };
        const float horizon = 470;
        for (var x = -600; x <= 2000; x += 120) canvas.DrawLine(720, horizon, x, 1800, grid);
        for (var i = 0; i < 18; i++)
        {
            var t = i / 18f;
            var y = horizon + t * t * 1330;
            canvas.DrawLine(0, y, 1440, y, grid);
        }
    }
}

internal static class TitaniumLeaguePosterRenderer
{
    private static readonly SKColor Steel = new(160, 180, 196);
    private static readonly SKColor Ice = new(104, 220, 255);
    private static readonly SKColor Dark = new(12, 20, 28);
    private static readonly SKColor Ink = new(232, 241, 247);
    private static readonly SKColor Muted = new(126, 147, 161);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(new SKColor(8, 14, 20));
        var canvas = surface.Canvas;
        using (var metal = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1440, 1800),
                       [new SKColor(14, 25, 34), new SKColor(28, 42, 52), new SKColor(7, 13, 19)],
                       [0f, .48f, 1f], SKShaderTileMode.Clamp)
               }) canvas.DrawRect(new SKRect(0, 0, 1440, 1800), metal);
        DrawMetalTexture(canvas);
        PosterDrawing.DrawText(canvas, "TITANIUM LEAGUE", 62, 82, 19, Ice, true, 360);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 58, 173, 62, Ink, true, 1180, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 62, 218, 19, Muted, false, 1100);
        PosterDrawing.DrawPill(canvas, "ROSTER LOCK", new SKRect(1165, 57, 1360, 98), new SKColor(18, 33, 44, 230), Ice, PosterDrawing.WithAlpha(Ice, 120), 12);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            DrawHex(canvas, new SKPoint(720, 900), 160, new SKColor(30, 46, 57), Steel, 3);
            PosterDrawing.DrawCenteredText(canvas, "NO ROSTER", 720, 915, 36, Ice, true, 480, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var y = 300f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawTitaniumModule(canvas, new SKRect(54, y, 1386, y + 430), visible[i], i, i % 2 == 1);
            y += 468;
        }
        PosterDrawing.DrawText(canvas, "ENGINEERED FOR MATCHDAY", 62, 1740, 12, Muted, true, 320);
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT // TD-10", 1370, 1740, 12, Ice, true, 320, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawTitaniumModule(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index, bool reverse)
    {
        var accent = index switch { 0 => Ice, 1 => new SKColor(191, 205, 217), _ => new SKColor(117, 169, 255) };
        using (var panel = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                       [new SKColor(24, 37, 47, 248), new SKColor(10, 18, 25, 248)], null, SKShaderTileMode.Clamp),
                   IsAntialias = true
               }) PosterDrawing.DrawCutCornerPanel(canvas, rect, 36, panel);
        using (var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 105), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true }) PosterDrawing.DrawCutCornerPanel(canvas, rect, 36, border);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var heroCenter = new SKPoint(reverse ? rect.Right - 170 : rect.Left + 170, rect.MidY + 5);
        DrawHex(canvas, heroCenter, 126, new SKColor(15, 27, 35), accent, 3);
        var capRect = new SKRect(heroCenter.X - 84, heroCenter.Y - 84, heroCenter.X + 84, heroCenter.Y + 84);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, accent, PosterAvatarShape.RoundedSquare, true);
        PosterDrawing.DrawCenteredText(canvas, "CAPTAIN", heroCenter.X, heroCenter.Y + 126, 11, accent, true, 130);
        PosterDrawing.DrawCenteredText(canvas, captain.Name, heroCenter.X, heroCenter.Y + 155, 17, Ink, true, 280);

        var contentLeft = reverse ? rect.Left + 44 : rect.Left + 340;
        var contentRight = reverse ? rect.Right - 340 : rect.Right - 44;
        PosterDrawing.DrawText(canvas, $"MODULE 0{index + 1}", contentLeft, rect.Top + 47, 13, accent, true, 180);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), contentLeft, rect.Top + 91, 35, Ink, true, contentRight - contentLeft, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   PLAYERS {PosterDrawing.PlayerCount(team):00}   •   SLOTS {team.Slots.Count:00}", contentLeft, rect.Top + 124, 12, Muted, true, contentRight - contentLeft);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var rosterTop = rect.Top + 165;
        for (var i = 0; i < slots.Count; i++)
        {
            var col = i % 2;
            var row = i / 2;
            var x = contentLeft + col * ((contentRight - contentLeft) / 2);
            var y = rosterTop + row * 72;
            var slot = slots[i];
            var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
            PosterDrawing.DrawAvatar(canvas, player, new SKRect(x, y - 24, x + 48, y + 24), accent, PosterAvatarShape.RoundedSquare);
            var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : player.Name;
            PosterDrawing.DrawText(canvas, name, x + 61, y + 4, 16, Ink, true, (contentRight - contentLeft) / 2 - 75);
            PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "LINKED SLOT" : $"P-{i + 1:00}", x + 61, y + 24, 9, slot.Players.Count > 1 ? accent : Muted, true, 100);
        }
    }

    private static void DrawHex(SKCanvas canvas, SKPoint center, float radius, SKColor fill, SKColor border, float borderWidth)
    {
        using var path = new SKPath();
        for (var i = 0; i < 6; i++)
        {
            var angle = MathF.PI / 3f * i - MathF.PI / 6f;
            var point = new SKPoint(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
            if (i == 0) path.MoveTo(point); else path.LineTo(point);
        }
        path.Close();
        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawPath(path, fillPaint);
        using var borderPaint = new SKPaint { Color = border, Style = SKPaintStyle.Stroke, StrokeWidth = borderWidth, IsAntialias = true };
        canvas.DrawPath(path, borderPaint);
    }

    private static void DrawMetalTexture(SKCanvas canvas)
    {
        using var fine = new SKPaint { Color = new SKColor(210, 230, 240, 10), StrokeWidth = 1 };
        for (var y = 0; y < 1800; y += 6) canvas.DrawLine(0, y, 1440, y, fine);
        using var diagonal = new SKPaint { Color = new SKColor(100, 200, 240, 13), StrokeWidth = 1 };
        for (var x = -500; x < 1600; x += 210) canvas.DrawLine(x, 0, x + 620, 1800, diagonal);
    }
}
