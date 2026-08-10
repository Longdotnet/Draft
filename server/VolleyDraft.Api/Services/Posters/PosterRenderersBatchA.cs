using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

internal static class ChampionshipGoldPosterRenderer
{
    private static readonly SKColor Gold = new(226, 184, 72);
    private static readonly SKColor PaleGold = new(255, 231, 156);
    private static readonly SKColor Ink = new(250, 247, 238);
    private static readonly SKColor Muted = new(177, 165, 136);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(new SKColor(8, 7, 5));
        var canvas = surface.Canvas;
        using (var bg = new SKPaint
               {
                   Shader = SKShader.CreateRadialGradient(
                       new SKPoint(720, 620), 1150,
                       [new SKColor(48, 35, 10), new SKColor(10, 9, 7), new SKColor(3, 3, 3)],
                       [0f, .52f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, PosterDrawing.Width, PosterDrawing.Height), bg);

        DrawGoldRays(canvas);
        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT", 720, 94, 22, Gold, true, 520);
        PosterDrawing.DrawCenteredText(canvas, "CHAMPIONSHIP", 720, 188, 74, Ink, true, 1220, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, sessionName, 720, 252, 31, PaleGold, true, 1160);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 300, 20, Muted, false, 1180);
        DrawChampionshipRule(canvas, 110, 330, 1330);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            DrawEmpty(canvas);
            return PosterDrawing.Encode(surface);
        }

        const float margin = 64;
        const float gap = 24;
        const float top = 390;
        const float bottom = 1595;
        var cardWidth = (PosterDrawing.Width - margin * 2 - gap * 2) / 3f;
        for (var i = 0; i < visible.Count; i += 1)
        {
            var left = margin + i * (cardWidth + gap);
            DrawMedalColumn(canvas, new SKRect(left, top, left + cardWidth, bottom), visible[i], i);
        }

        PosterDrawing.DrawCenteredText(canvas, "ONE NIGHT  •  THREE TEAMS  •  ONE COURT", 720, 1685, 18, Muted, true, 900);
        PosterDrawing.DrawCenteredText(canvas, "MATCHDAY EDITION", 720, 1730, 13, PosterDrawing.WithAlpha(Gold, 170), true, 500);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawMedalColumn(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var accent = index switch
        {
            0 => Gold,
            1 => new SKColor(199, 207, 217),
            _ => new SKColor(201, 128, 76)
        };
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 130), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 24), IsAntialias = true })
            canvas.DrawRoundRect(rect, 18, 18, shadow);
        using (var panel = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                       [new SKColor(31, 27, 18, 246), new SKColor(12, 12, 11, 249)], null, SKShaderTileMode.Clamp),
                   IsAntialias = true
               })
            canvas.DrawRoundRect(rect, 18, 18, panel);
        using (var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 175), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
            canvas.DrawRoundRect(rect, 18, 18, border);

        PosterDrawing.DrawCenteredText(canvas, $"0{index + 1}", rect.MidX, rect.Top + 95, 76, PosterDrawing.WithAlpha(accent, 190), true, rect.Width - 40, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, team.Name, rect.MidX, rect.Top + 145, 26, Ink, true, rect.Width - 44, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, $"TEAM POWER  {PosterDrawing.TeamScore(team)}", rect.MidX, rect.Top + 182, 15, accent, true, rect.Width - 40);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var avatar = new SKRect(rect.MidX - 82, rect.Top + 225, rect.MidX + 82, rect.Top + 389);
        PosterDrawing.DrawAvatar(canvas, captain, avatar, accent, PosterAvatarShape.Circle, true);
        PosterDrawing.DrawCenteredText(canvas, "CAPTAIN", rect.MidX, rect.Top + 430, 12, accent, true, 130);
        PosterDrawing.DrawCenteredText(canvas, captain.Name, rect.MidX, rect.Top + 467, 23, Ink, true, rect.Width - 54);

        using var rule = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 90), StrokeWidth = 1.4f, IsAntialias = true };
        canvas.DrawLine(rect.Left + 30, rect.Top + 500, rect.Right - 30, rect.Top + 500, rule);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var y = rect.Top + 555;
        foreach (var slot in slots)
        {
            var first = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
            var avatarRect = new SKRect(rect.Left + 30, y - 28, rect.Left + 86, y + 28);
            if (slot.Players.Count > 1)
                PosterDrawing.DrawOverlappingAvatars(canvas, slot.Players, rect.Left + 30, y, 52, accent);
            else
                PosterDrawing.DrawAvatar(canvas, first, avatarRect, accent);
            var nameX = slot.Players.Count > 1 ? rect.Left + 116 : rect.Left + 104;
            var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : first.Name;
            PosterDrawing.DrawText(canvas, name, nameX, y + 7, 19, Ink, true, rect.Right - nameX - 22);
            if (slot.Players.Count > 1)
                PosterDrawing.DrawPill(canvas, "SHARED", new SKRect(rect.Right - 91, y - 17, rect.Right - 21, y + 15), PosterDrawing.WithAlpha(accent, 38), accent, PosterDrawing.WithAlpha(accent, 95), 10);
            y += 88;
        }

        PosterDrawing.DrawCenteredText(canvas, $"{PosterDrawing.PlayerCount(team)} PLAYERS  •  {team.Slots.Count} SLOTS", rect.MidX, rect.Bottom - 36, 12, Muted, true, rect.Width - 30);
    }

    private static void DrawGoldRays(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = new SKColor(226, 184, 72, 14), StrokeWidth = 2, IsAntialias = true };
        for (var i = 0; i < 22; i++)
        {
            var angle = i * MathF.PI * 2 / 22f;
            canvas.DrawLine(720, 470, 720 + MathF.Cos(angle) * 1050, 470 + MathF.Sin(angle) * 1050, paint);
        }
    }

    private static void DrawChampionshipRule(SKCanvas canvas, float left, float y, float right)
    {
        using var paint = new SKPaint { Color = PosterDrawing.WithAlpha(Gold, 165), StrokeWidth = 2, IsAntialias = true };
        canvas.DrawLine(left, y, 600, y, paint);
        canvas.DrawLine(840, y, right, y, paint);
        using var diamond = new SKPath();
        diamond.MoveTo(720, y - 12); diamond.LineTo(734, y); diamond.LineTo(720, y + 12); diamond.LineTo(706, y); diamond.Close();
        using var fill = new SKPaint { Color = Gold, IsAntialias = true };
        canvas.DrawPath(diamond, fill);
    }

    private static void DrawEmpty(SKCanvas canvas)
    {
        PosterDrawing.DrawCenteredText(canvas, "CHƯA CÓ ĐỘI HÌNH", 720, 850, 52, PaleGold, true, 900, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 910, 22, Muted, false, 800);
    }
}

internal static class CyberStormPosterRenderer
{
    private static readonly SKColor Cyan = new(45, 234, 255);
    private static readonly SKColor Violet = new(181, 82, 255);
    private static readonly SKColor Pink = new(255, 65, 155);
    private static readonly SKColor Ink = new(235, 249, 255);
    private static readonly SKColor Muted = new(121, 152, 176);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(new SKColor(3, 5, 20));
        var canvas = surface.Canvas;
        using (var bg = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1440, 1800),
                       [new SKColor(4, 7, 25), new SKColor(25, 5, 48), new SKColor(2, 16, 35)],
                       [0f, .54f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, 1440, 1800), bg);
        DrawScanlines(canvas);
        DrawHudCorner(canvas, 38, 34, Cyan);
        DrawHudCorner(canvas, 1402, 34, Violet, true);
        PosterDrawing.DrawText(canvas, "VD://TEAM_MATRIX", 58, 72, 18, Cyan, true, 500);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 58, 157, 61, Ink, true, 1160, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 61, 207, 21, Muted, false, 1100);
        PosterDrawing.DrawPill(canvas, "LIVE ROSTER DATA", new SKRect(1110, 171, 1348, 211), new SKColor(5, 18, 34, 230), Cyan, PosterDrawing.WithAlpha(Cyan, 110), 13);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "NO TEAM DATA // WAITING", 720, 880, 44, Cyan, true, 1000, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var accents = new[] { Cyan, Pink, Violet };
        var y = 300f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawHudLane(canvas, new SKRect(54, y, 1386, y + 430), visible[i], i, accents[i]);
            y += 468;
        }
        PosterDrawing.DrawText(canvas, "SYSTEM / MATCHDAY / GENERATED ROSTER", 58, 1742, 13, Muted, true, 600);
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT", 1380, 1742, 13, Cyan, true, 250, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawHudLane(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index, SKColor accent)
    {
        using (var panel = new SKPaint { Color = new SKColor(4, 13, 29, 224), IsAntialias = true })
            PosterDrawing.DrawCutCornerPanel(canvas, rect, 28, panel);
        using (var border = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 135), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
            PosterDrawing.DrawCutCornerPanel(canvas, rect, 28, border);

        using var glow = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 45), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 34), IsAntialias = true };
        canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + 18, rect.Bottom), glow);
        using var rail = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawRect(new SKRect(rect.Left, rect.Top + 28, rect.Left + 5, rect.Bottom - 28), rail);

        var left = new SKRect(rect.Left + 28, rect.Top + 28, rect.Left + 300, rect.Bottom - 28);
        PosterDrawing.DrawText(canvas, $"NODE_0{index + 1}", left.Left, left.Top + 24, 13, accent, true, 180);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), left.Left, left.Top + 77, 31, Ink, true, left.Width - 10, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}", left.Left, left.Top + 111, 15, Muted, true, 170);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(left.Left, left.Top + 142, left.Left + 150, left.Top + 292);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, accent, PosterAvatarShape.RoundedSquare, true);
        PosterDrawing.DrawPill(canvas, "CAPTAIN", new SKRect(left.Left, left.Top + 307, left.Left + 112, left.Top + 339), PosterDrawing.WithAlpha(accent, 34), accent, PosterDrawing.WithAlpha(accent, 105), 11);
        PosterDrawing.DrawText(canvas, captain.Name, left.Left + 122, left.Top + 332, 18, Ink, true, left.Width - 122);

        var gridLeft = rect.Left + 330;
        var gridTop = rect.Top + 38;
        const float cellWidth = 325;
        const float cellHeight = 108;
        const float gapX = 20;
        const float gapY = 18;
        var slots = PosterDrawing.VisibleSlots(team, 6);
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var row = slotIndex / 3;
            var col = slotIndex % 3;
            var cell = new SKRect(
                gridLeft + col * (cellWidth + gapX),
                gridTop + row * (cellHeight + gapY),
                gridLeft + col * (cellWidth + gapX) + cellWidth,
                gridTop + row * (cellHeight + gapY) + cellHeight);
            DrawCyberCell(canvas, cell, slots[slotIndex], accent, slotIndex);
        }
        PosterDrawing.DrawText(canvas, $"PLAYERS/{PosterDrawing.PlayerCount(team):00}   SLOTS/{team.Slots.Count:00}", gridLeft, rect.Bottom - 38, 13, Muted, true, 420);
        PosterDrawing.DrawText(canvas, $"0{index + 1}", rect.Right - 42, rect.Bottom - 22, 84, PosterDrawing.WithAlpha(accent, 31), true, 120, PosterDrawing.BlackTypeface, SKTextAlign.Right);
    }

    private static void DrawCyberCell(SKCanvas canvas, SKRect rect, TeamCardSlot slot, SKColor accent, int index)
    {
        using (var cell = new SKPaint { Color = new SKColor(12, 24, 46, 222), IsAntialias = true })
            canvas.DrawRoundRect(rect, 8, 8, cell);
        using (var line = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 80), StrokeWidth = 1, IsAntialias = true })
        {
            canvas.DrawLine(rect.Left + 8, rect.Top + 8, rect.Right - 8, rect.Top + 8, line);
            canvas.DrawLine(rect.Left + 8, rect.Bottom - 8, rect.Left + 58, rect.Bottom - 8, line);
        }
        if (slot.Players.Count > 1)
            PosterDrawing.DrawOverlappingAvatars(canvas, slot.Players, rect.Left + 14, rect.MidY, 62, accent, PosterAvatarShape.RoundedSquare);
        else
            PosterDrawing.DrawAvatar(canvas, slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName), new SKRect(rect.Left + 14, rect.MidY - 31, rect.Left + 76, rect.MidY + 31), accent, PosterAvatarShape.RoundedSquare);
        var x = rect.Left + (slot.Players.Count > 1 ? 98 : 90);
        var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName;
        PosterDrawing.DrawText(canvas, name, x, rect.Top + 49, 17, Ink, true, rect.Right - x - 12);
        PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "SHARED_LINK" : $"PLAYER_{index + 1:00}", x, rect.Top + 77, 11, slot.Players.Count > 1 ? accent : Muted, true, rect.Right - x - 12);
    }

    private static void DrawScanlines(SKCanvas canvas)
    {
        using var scan = new SKPaint { Color = new SKColor(100, 220, 255, 10), StrokeWidth = 1 };
        for (var y = 0; y < 1800; y += 9) canvas.DrawLine(0, y, 1440, y, scan);
        using var grid = new SKPaint { Color = new SKColor(104, 78, 255, 13), StrokeWidth = 1 };
        for (var x = 0; x < 1440; x += 120) canvas.DrawLine(x, 250, x, 1700, grid);
    }

    private static void DrawHudCorner(SKCanvas canvas, float x, float y, SKColor color, bool mirror = false)
    {
        using var paint = new SKPaint { Color = color, StrokeWidth = 3, IsAntialias = true };
        var dir = mirror ? -1 : 1;
        canvas.DrawLine(x, y, x + dir * 74, y, paint);
        canvas.DrawLine(x, y, x, y + 45, paint);
    }
}

internal static class MonolithBroadcastPosterRenderer
{
    private static readonly SKColor Paper = new(240, 236, 224);
    private static readonly SKColor Black = new(14, 14, 13);
    private static readonly SKColor Red = new(220, 52, 42);
    private static readonly SKColor Gray = new(100, 99, 92);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Paper);
        var canvas = surface.Canvas;
        using (var side = new SKPaint { Color = Black }) canvas.DrawRect(new SKRect(0, 0, 108, 1800), side);
        PosterDrawing.DrawText(canvas, "VOLLEY", 46, 1700, 24, Paper, true, 1500, PosterDrawing.BlackTypeface);
        canvas.Save();
        canvas.RotateDegrees(-90, 54, 900);
        PosterDrawing.DrawCenteredText(canvas, "MATCHDAY / TEAM SELECTION / 2026", 54, 912, 14, new SKColor(220, 216, 203), true, 1500);
        canvas.Restore();

        PosterDrawing.DrawText(canvas, "VD", 158, 90, 20, Red, true, 100);
        PosterDrawing.DrawText(canvas, "LINEUP", 158, 192, 91, Black, true, 760, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 160, 252, 29, Gray, true, 1100);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 160, 300, 18, Gray, false, 1110);
        using (var bar = new SKPaint { Color = Red }) canvas.DrawRect(new SKRect(160, 336, 470, 346), bar);
        using (var rule = new SKPaint { Color = new SKColor(25, 25, 23, 50), StrokeWidth = 2 }) canvas.DrawLine(500, 341, 1360, 341, rule);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawText(canvas, "NO TEAMS YET.", 160, 820, 72, Black, true, 1100, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawText(canvas, "Draft first. The poster will lock when the session gets its first image.", 164, 875, 20, Gray, false, 1050);
            return PosterDrawing.Encode(surface);
        }

        var y = 405f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawEditorialBand(canvas, new SKRect(142, y, 1370, y + 405), visible[i], i);
            y += 432;
        }
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT", 160, 1734, 14, Black, true, 250);
        PosterDrawing.DrawText(canvas, "AUTOMATED MATCHDAY GRAPHIC", 1360, 1734, 12, Gray, true, 400, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawEditorialBand(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var dark = index % 2 == 0;
        var fill = dark ? Black : new SKColor(221, 216, 201);
        var foreground = dark ? Paper : Black;
        var muted = dark ? new SKColor(170, 165, 153) : Gray;
        using (var paint = new SKPaint { Color = fill, IsAntialias = true }) canvas.DrawRect(rect, paint);
        using (var accent = new SKPaint { Color = Red }) canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + 11, rect.Bottom), accent);

        PosterDrawing.DrawText(canvas, $"0{index + 1}", rect.Left + 34, rect.Top + 114, 103, dark ? new SKColor(255, 255, 255, 33) : new SKColor(0, 0, 0, 25), true, 180, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 190, rect.Top + 76, 42, foreground, true, 560, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}  /  {PosterDrawing.PlayerCount(team)} PLAYERS", rect.Left + 194, rect.Top + 112, 14, Red, true, 420);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(rect.Left + 52, rect.Top + 155, rect.Left + 192, rect.Top + 295);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, Red, PosterAvatarShape.Square, true, dark);
        PosterDrawing.DrawText(canvas, "CAPTAIN", rect.Left + 52, rect.Top + 330, 11, Red, true, 110);
        PosterDrawing.DrawText(canvas, captain.Name, rect.Left + 52, rect.Top + 361, 17, foreground, true, 280);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var startX = rect.Left + 360;
        var startY = rect.Top + 165;
        for (var i = 0; i < slots.Count; i++)
        {
            var col = i % 2;
            var row = i / 2;
            var x = startX + col * 420;
            var y = startY + row * 70;
            var slot = slots[i];
            var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName;
            PosterDrawing.DrawText(canvas, $"{i + 1:00}", x, y, 13, Red, true, 30);
            PosterDrawing.DrawText(canvas, name.ToUpperInvariant(), x + 42, y, 19, foreground, true, 345, PosterDrawing.BlackTypeface);
            if (slot.Players.Count > 1)
                PosterDrawing.DrawText(canvas, "SHARED", x + 42, y + 22, 9, muted, true, 80);
        }
        using var bottom = new SKPaint { Color = dark ? new SKColor(255, 255, 255, 28) : new SKColor(0, 0, 0, 35), StrokeWidth = 1 };
        canvas.DrawLine(rect.Left + 360, rect.Bottom - 45, rect.Right - 28, rect.Bottom - 45, bottom);
        PosterDrawing.DrawText(canvas, $"TEAM / {index + 1:00}", rect.Right - 28, rect.Bottom - 19, 10, muted, true, 130, null, SKTextAlign.Right);
    }
}
