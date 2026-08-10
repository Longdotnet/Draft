using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

internal static class VelocityWavePosterRenderer
{
    private static readonly SKColor Paper = new(244, 249, 248);
    private static readonly SKColor Navy = new(9, 33, 46);
    private static readonly SKColor Teal = new(0, 172, 161);
    private static readonly SKColor Blue = new(34, 119, 255);
    private static readonly SKColor Mint = new(135, 235, 213);
    private static readonly SKColor Muted = new(91, 121, 129);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Paper);
        var canvas = surface.Canvas;
        DrawWaves(canvas);
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT", 70, 83, 18, Teal, true, 280);
        PosterDrawing.DrawText(canvas, "MATCHDAY FLOW", 66, 174, 70, Navy, true, 940, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 70, 229, 28, Blue, true, 1080);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 72, 272, 18, Muted, false, 1080);
        PosterDrawing.DrawPill(canvas, "TEAM ROTATION", new SKRect(1155, 62, 1360, 103), new SKColor(230, 245, 242), Teal, new SKColor(0, 172, 161, 70), 12);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "NO LINEUP YET", 720, 890, 50, Navy, true, 800, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, "Draft first, then the selected poster stays with this match.", 720, 945, 18, Muted, false, 1000);
            return PosterDrawing.Encode(surface);
        }

        var tops = new[] { 350f, 785f, 1220f };
        for (var i = 0; i < visible.Count; i++)
            DrawFlowTeam(canvas, new SKRect(70, tops[i], 1370, tops[i] + 380), visible[i], i);

        PosterDrawing.DrawText(canvas, "MOVE FAST. PLAY CLEAN.", 70, 1732, 13, Navy, true, 330);
        PosterDrawing.DrawText(canvas, "AUTO DRAFT RESULT", 1370, 1732, 12, Teal, true, 280, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawFlowTeam(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var accent = index switch { 0 => Teal, 1 => Blue, _ => new SKColor(28, 157, 111) };
        var offset = index == 1 ? 76f : 0f;
        rect = new SKRect(rect.Left + offset, rect.Top, rect.Right - (index == 1 ? 0 : 76), rect.Bottom);
        using (var shadow = new SKPaint { Color = new SKColor(12, 50, 60, 22), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 20), IsAntialias = true }) canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top + 8, rect.Right, rect.Bottom + 8), 34, 34, shadow);
        using (var panel = new SKPaint { Color = new SKColor(255, 255, 255, 238), IsAntialias = true }) canvas.DrawRoundRect(rect, 34, 34, panel);
        using (var rail = new SKPaint { Color = accent, IsAntialias = true }) canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Left + 13, rect.Bottom), 8, 8, rail);

        PosterDrawing.DrawText(canvas, $"0{index + 1}", rect.Left + 38, rect.Top + 72, 53, PosterDrawing.WithAlpha(accent, 125), true, 95, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 128, rect.Top + 66, 32, Navy, true, 520, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}  •  {PosterDrawing.PlayerCount(team)} PLAYERS", rect.Left + 131, rect.Top + 97, 12, Muted, true, 380);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(rect.Left + 42, rect.Top + 132, rect.Left + 198, rect.Top + 288);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, accent, PosterAvatarShape.RoundedSquare, true);
        PosterDrawing.DrawText(canvas, "CAPTAIN", rect.Left + 44, rect.Top + 319, 10, accent, true, 95);
        PosterDrawing.DrawText(canvas, captain.Name, rect.Left + 44, rect.Top + 346, 17, Navy, true, 240);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var rosterLeft = rect.Left + 300;
        var rosterTop = rect.Top + 132;
        for (var i = 0; i < slots.Count; i++)
        {
            var col = i % 3;
            var row = i / 3;
            var cell = new SKRect(rosterLeft + col * 295, rosterTop + row * 98, rosterLeft + col * 295 + 275, rosterTop + row * 98 + 82);
            DrawFlowPlayer(canvas, cell, slots[i], accent, i);
        }
    }

    private static void DrawFlowPlayer(SKCanvas canvas, SKRect rect, TeamCardSlot slot, SKColor accent, int index)
    {
        using (var fill = new SKPaint { Color = new SKColor(237, 247, 245), IsAntialias = true }) canvas.DrawRoundRect(rect, 18, 18, fill);
        var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
        PosterDrawing.DrawAvatar(canvas, player, new SKRect(rect.Left + 10, rect.Top + 10, rect.Left + 72, rect.Top + 72), accent, PosterAvatarShape.Circle);
        var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : player.Name;
        PosterDrawing.DrawText(canvas, name, rect.Left + 84, rect.Top + 39, 15, Navy, true, rect.Width - 94);
        PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "SHARED FLOW" : $"PLAYER {index + 1:00}", rect.Left + 84, rect.Top + 61, 9, slot.Players.Count > 1 ? accent : Muted, true, 120);
    }

    private static void DrawWaves(SKCanvas canvas)
    {
        using var pathA = new SKPath();
        pathA.MoveTo(-120, 610);
        pathA.CubicTo(300, 310, 520, 650, 820, 460);
        pathA.CubicTo(1060, 310, 1230, 350, 1540, 160);
        pathA.LineTo(1540, 310);
        pathA.CubicTo(1210, 500, 1010, 470, 800, 620);
        pathA.CubicTo(480, 845, 190, 520, -120, 790);
        pathA.Close();
        using var waveA = new SKPaint { Color = new SKColor(0, 172, 161, 22), IsAntialias = true };
        canvas.DrawPath(pathA, waveA);

        using var pathB = new SKPath();
        pathB.MoveTo(-180, 1450);
        pathB.CubicTo(260, 1180, 560, 1510, 880, 1300);
        pathB.CubicTo(1130, 1135, 1330, 1270, 1560, 1050);
        pathB.LineTo(1560, 1280);
        pathB.CubicTo(1320, 1460, 1090, 1360, 900, 1490);
        pathB.CubicTo(570, 1710, 270, 1420, -180, 1700);
        pathB.Close();
        using var waveB = new SKPaint { Color = new SKColor(34, 119, 255, 16), IsAntialias = true };
        canvas.DrawPath(pathB, waveB);
    }
}

internal static class NoirSpotlightPosterRenderer
{
    private static readonly SKColor Black = new(4, 4, 5);
    private static readonly SKColor White = new(244, 244, 241);
    private static readonly SKColor Gray = new(140, 140, 136);
    private static readonly SKColor Red = new(199, 39, 35);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Black);
        var canvas = surface.Canvas;
        DrawSpotlights(canvas);
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT PRESENTS", 66, 76, 14, Gray, true, 360);
        PosterDrawing.DrawText(canvas, "THE LINEUP", 62, 183, 90, White, true, 880, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 68, 240, 25, Red, true, 1060);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 70, 280, 17, Gray, false, 1080);
        using (var red = new SKPaint { Color = Red }) canvas.DrawRect(new SKRect(66, 310, 236, 318), red);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "DARK COURT. NO ROSTER.", 720, 900, 44, White, true, 1000, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var y = 370f;
        for (var i = 0; i < visible.Count; i++)
        {
            DrawNoirScene(canvas, new SKRect(66, y, 1374, y + 410), visible[i], i);
            y += 445;
        }
        PosterDrawing.DrawText(canvas, "MATCHDAY PORTRAIT SERIES", 68, 1732, 12, Gray, true, 330);
        PosterDrawing.DrawText(canvas, "01 / 10 POSTER COLLECTION", 1372, 1732, 12, White, true, 330, null, SKTextAlign.Right);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawNoirScene(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        using (var panel = new SKPaint { Color = index % 2 == 0 ? new SKColor(11, 11, 12, 230) : new SKColor(18, 18, 18, 235), IsAntialias = true }) canvas.DrawRect(rect, panel);
        using (var top = new SKPaint { Color = new SKColor(255, 255, 255, 24), StrokeWidth = 1 }) canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, top);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(rect.Left + 28, rect.Top + 45, rect.Left + 312, rect.Top + 329);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, White, PosterAvatarShape.Square, false, true);
        using (var red = new SKPaint { Color = Red }) canvas.DrawRect(new SKRect(rect.Left + 28, rect.Top + 345, rect.Left + 108, rect.Top + 352), red);
        PosterDrawing.DrawText(canvas, captain.Name.ToUpperInvariant(), rect.Left + 28, rect.Top + 383, 18, White, true, 300, PosterDrawing.BlackTypeface);

        PosterDrawing.DrawText(canvas, $"0{index + 1}", rect.Left + 350, rect.Top + 92, 83, new SKColor(255, 255, 255, 22), true, 120, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 465, rect.Top + 80, 38, White, true, 680, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"TEAM POWER {PosterDrawing.TeamScore(team)}", rect.Left + 468, rect.Top + 112, 12, Red, true, 250);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var x = rect.Left + 465;
        var y = rect.Top + 168;
        for (var i = 0; i < slots.Count; i++)
        {
            var row = i / 2;
            var col = i % 2;
            var cellX = x + col * 410;
            var cellY = y + row * 66;
            var slot = slots[i];
            var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
            PosterDrawing.DrawText(canvas, $"{i + 1:00}", cellX, cellY, 11, Gray, true, 30);
            var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : player.Name;
            PosterDrawing.DrawText(canvas, name.ToUpperInvariant(), cellX + 42, cellY, 17, White, true, 345, PosterDrawing.BlackTypeface);
            if (slot.Players.Count > 1)
                PosterDrawing.DrawText(canvas, "SHARED", cellX + 42, cellY + 20, 9, Red, true, 70);
        }
        PosterDrawing.DrawText(canvas, $"{PosterDrawing.PlayerCount(team)} PLAYERS  /  {team.Slots.Count} SLOTS", rect.Right - 28, rect.Bottom - 24, 11, Gray, true, 260, null, SKTextAlign.Right);
    }

    private static void DrawSpotlights(SKCanvas canvas)
    {
        var centers = new[] { 280f, 720f, 1160f };
        foreach (var x in centers)
        {
            using var path = new SKPath();
            path.MoveTo(x - 70, -20);
            path.LineTo(x + 70, -20);
            path.LineTo(x + 390, 1800);
            path.LineTo(x - 390, 1800);
            path.Close();
            using var light = new SKPaint
            {
                Color = new SKColor(255, 255, 245, 8),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 38)
            };
            canvas.DrawPath(path, light);
        }
    }
}

internal static class StreetClashPosterRenderer
{
    private static readonly SKColor Concrete = new(37, 38, 36);
    private static readonly SKColor Paper = new(230, 226, 212);
    private static readonly SKColor Lime = new(199, 255, 67);
    private static readonly SKColor Orange = new(255, 100, 39);
    private static readonly SKColor Pink = new(255, 74, 147);
    private static readonly SKColor Ink = new(24, 24, 22);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Concrete);
        var canvas = surface.Canvas;
        DrawConcrete(canvas, sessionName);
        DrawSprayWord(canvas, "VOLLEY", 55, 175, 95, Lime, -4);
        DrawSprayWord(canvas, "CLASH", 470, 190, 100, Orange, 3);
        PosterDrawing.DrawText(canvas, sessionName.ToUpperInvariant(), 64, 260, 28, Paper, true, 1070, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, PosterDrawing.BuildMetadata(startTime, location), 66, 301, 17, new SKColor(181, 181, 170), false, 1070);
        PosterDrawing.DrawPill(canvas, "STREET EDITION", new SKRect(1150, 63, 1360, 105), new SKColor(20, 20, 20, 190), Lime, PosterDrawing.WithAlpha(Lime, 120), 12);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawJaggedPaper(canvas, new SKRect(220, 720, 1220, 1030), Paper, 41);
            PosterDrawing.DrawCenteredText(canvas, "NO TEAM. NO NOISE.", 720, 875, 52, Ink, true, 850, PosterDrawing.BlackTypeface);
            return PosterDrawing.Encode(surface);
        }

        var accents = new[] { Lime, Orange, Pink };
        var rects = new[]
        {
            new SKRect(70, 365, 1345, 750),
            new SKRect(105, 795, 1380, 1180),
            new SKRect(55, 1225, 1330, 1610)
        };
        for (var i = 0; i < visible.Count; i++)
            DrawStreetSheet(canvas, rects[i], visible[i], i, accents[i], i == 1 ? -2.0f : i == 2 ? 1.6f : .8f);

        DrawSprayWord(canvas, "PLAY LOUD", 850, 1720, 43, Lime, -2);
        PosterDrawing.DrawText(canvas, "AUTO DRAFT / LOCKED POSTER", 62, 1735, 11, new SKColor(170, 170, 160), true, 320);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawStreetSheet(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index, SKColor accent, float rotation)
    {
        var save = canvas.Save();
        canvas.RotateDegrees(rotation, rect.MidX, rect.MidY);
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 100), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14), IsAntialias = true }) canvas.DrawRect(new SKRect(rect.Left + 12, rect.Top + 14, rect.Right + 12, rect.Bottom + 14), shadow);
        PosterDrawing.DrawJaggedPaper(canvas, rect, Paper, PosterDrawing.StableSeed(team.Name + index));

        using (var stripe = new SKPaint { Color = accent }) canvas.DrawRect(new SKRect(rect.Left + 20, rect.Top + 18, rect.Left + 38, rect.Bottom - 18), stripe);
        PosterDrawing.DrawText(canvas, $"0{index + 1}", rect.Left + 62, rect.Top + 92, 67, accent, true, 110, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 168, rect.Top + 82, 39, Ink, true, 610, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)} // {PosterDrawing.PlayerCount(team)} HEADS", rect.Left + 172, rect.Top + 113, 12, new SKColor(85, 82, 74), true, 360);

        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        var capRect = new SKRect(rect.Right - 244, rect.Top + 38, rect.Right - 64, rect.Top + 218);
        PosterDrawing.DrawAvatar(canvas, captain, capRect, accent, PosterAvatarShape.Square, true);
        using (var capTag = new SKPaint { Color = accent }) canvas.DrawRect(new SKRect(rect.Right - 244, rect.Top + 229, rect.Right - 135, rect.Top + 256), capTag);
        PosterDrawing.DrawText(canvas, "CAPTAIN", rect.Right - 233, rect.Top + 249, 10, Ink, true, 92);
        PosterDrawing.DrawText(canvas, captain.Name.ToUpperInvariant(), rect.Right - 244, rect.Top + 285, 15, Ink, true, 190, PosterDrawing.BlackTypeface);

        var slots = PosterDrawing.VisibleSlots(team, 6);
        var startX = rect.Left + 70;
        var startY = rect.Top + 170;
        for (var i = 0; i < slots.Count; i++)
        {
            var col = i % 3;
            var row = i / 3;
            var x = startX + col * 300;
            var y = startY + row * 82;
            var slot = slots[i];
            var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
            PosterDrawing.DrawAvatar(canvas, player, new SKRect(x, y, x + 54, y + 54), accent, PosterAvatarShape.Square);
            var name = slot.Players.Count > 1 ? string.Join(" / ", slot.Players.Select(p => p.Name)) : player.Name;
            PosterDrawing.DrawText(canvas, name.ToUpperInvariant(), x + 66, y + 27, 14, Ink, true, 215, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawText(canvas, slot.Players.Count > 1 ? "SHARED" : $"NO.{i + 1:00}", x + 66, y + 48, 9, slot.Players.Count > 1 ? accent : new SKColor(102, 98, 89), true, 80);
        }
        canvas.RestoreToCount(save);
    }

    private static void DrawConcrete(SKCanvas canvas, string sessionName)
    {
        var random = new Random(PosterDrawing.StableSeed(sessionName) & int.MaxValue);
        for (var i = 0; i < 420; i++)
        {
            var shade = random.Next(28, 58);
            var alpha = random.Next(5, 24);
            using var speck = new SKPaint { Color = new SKColor((byte)shade, (byte)shade, (byte)shade, (byte)alpha), IsAntialias = true };
            canvas.DrawCircle(random.Next(0, 1440), random.Next(0, 1800), random.Next(1, 6), speck);
        }
        using var seam = new SKPaint { Color = new SKColor(255, 255, 255, 12), StrokeWidth = 1 };
        canvas.DrawLine(0, 590, 1440, 550, seam);
        canvas.DrawLine(0, 1190, 1440, 1230, seam);
    }

    private static void DrawSprayWord(SKCanvas canvas, string text, float x, float y, float size, SKColor color, float rotation)
    {
        var save = canvas.Save();
        canvas.RotateDegrees(rotation, x, y);
        PosterDrawing.DrawText(canvas, text, x + 5, y + 6, size, new SKColor(0, 0, 0, 145), true, 700, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, text, x, y, size, color, true, 700, PosterDrawing.BlackTypeface);
        canvas.RestoreToCount(save);
    }
}
