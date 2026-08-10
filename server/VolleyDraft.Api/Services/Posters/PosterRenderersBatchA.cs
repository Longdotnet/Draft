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
    private static readonly SKColor Night = new(6, 8, 14);
    private static readonly SKColor Bone = new(245, 238, 221);
    private static readonly SKColor Amber = new(244, 163, 72);
    private static readonly SKColor Crimson = new(210, 48, 58);
    private static readonly SKColor Ice = new(77, 192, 211);
    private static readonly SKColor Smoke = new(147, 145, 142);

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, string? location, IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Night);
        var canvas = surface.Canvas;
        DrawCinematicBackdrop(canvas, sessionName);
        DrawFilmFrame(canvas);

        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT PRESENTS", 720, 72, 16, PosterDrawing.WithAlpha(Bone, 175), true, 560);
        PosterDrawing.DrawCenteredText(canvas, "TRIPLE", 720, 185, 94, Bone, true, 1180, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "THREAT", 720, 284, 111, Amber, true, 1220, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, sessionName.ToUpperInvariant(), 720, 337, 25, Bone, true, 1140);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 377, 16, PosterDrawing.WithAlpha(Bone, 145), false, 1120);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "THE CAST HAS NOT BEEN REVEALED", 720, 885, 38, Bone, true, 1120, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 934, 18, Smoke, false, 820);
            PosterDrawing.DrawCenteredText(canvas, "ONE COURT  •  THREE TEAMS  •  NO RETAKES", 720, 1694, 15, Amber, true, 900);
            return PosterDrawing.Encode(surface);
        }

        var accents = new[] { Crimson, Ice, Amber };
        var heroRects = BuildHeroRects(visible.Count);
        for (var i = 0; i < visible.Count; i += 1)
            DrawFaction(canvas, visible[i], i, accents[i], heroRects[i], visible.Count);

        if (visible.Count == 3)
            DrawVersusMark(canvas);

        DrawBillingBlock(canvas, visible);
        PosterDrawing.DrawCenteredText(canvas, "ONE COURT  •  THREE TEAMS  •  NO RETAKES", 720, 1692, 15, PosterDrawing.WithAlpha(Bone, 155), true, 900);
        PosterDrawing.DrawCenteredText(canvas, "A VOLLEY DRAFT MATCHDAY PICTURE", 720, 1731, 11, PosterDrawing.WithAlpha(Amber, 170), true, 700);
        return PosterDrawing.Encode(surface);
    }

    private static IReadOnlyList<SKRect> BuildHeroRects(int count)
    {
        if (count == 1)
            return [new SKRect(480, 500, 960, 1030)];
        if (count == 2)
            return [new SKRect(90, 510, 540, 1010), new SKRect(900, 510, 1350, 1010)];
        return
        [
            new SKRect(70, 470, 500, 915),
            new SKRect(940, 470, 1370, 915),
            new SKRect(505, 945, 935, 1390)
        ];
    }

    private static void DrawFaction(SKCanvas canvas, TeamCardTeam team, int index, SKColor accent, SKRect portraitRect, int teamCount)
    {
        DrawPortraitAura(canvas, portraitRect, accent);
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        PosterDrawing.DrawAvatar(canvas, captain, portraitRect, accent, PosterAvatarShape.RoundedSquare, true, grayscale: true);
        DrawPortraitFade(canvas, portraitRect);
        DrawFilmScratch(canvas, portraitRect, index);

        if (teamCount == 3 && index < 2)
        {
            var rightSide = index == 1;
            var x = rightSide ? portraitRect.Right - 4 : portraitRect.Left + 4;
            var align = rightSide ? SKTextAlign.Right : SKTextAlign.Left;
            PosterDrawing.DrawText(canvas, $"0{index + 1}", x, portraitRect.Top - 24, 64, PosterDrawing.WithAlpha(accent, 82), true, 120, PosterDrawing.BlackTypeface, align);
            PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), x, portraitRect.Bottom + 54, 42, Bone, true, portraitRect.Width, PosterDrawing.BlackTypeface, align);
            PosterDrawing.DrawText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", x, portraitRect.Bottom + 87, 13, accent, true, portraitRect.Width, null, align);
            PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", x, portraitRect.Bottom + 114, 11, PosterDrawing.WithAlpha(Bone, 145), true, portraitRect.Width, null, align);
            DrawRosterCredits(canvas, team, portraitRect.Left, portraitRect.Bottom + 149, portraitRect.Width, accent, align);
        }
        else if (teamCount == 3)
        {
            PosterDrawing.DrawCenteredText(canvas, "03", portraitRect.MidX, portraitRect.Top - 18, 64, PosterDrawing.WithAlpha(accent, 75), true, 120, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), portraitRect.MidX, portraitRect.Bottom + 49, 44, Bone, true, 650, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", portraitRect.MidX, portraitRect.Bottom + 82, 13, accent, true, 600);
            PosterDrawing.DrawCenteredText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", portraitRect.MidX, portraitRect.Bottom + 108, 11, PosterDrawing.WithAlpha(Bone, 145), true, 600);
            DrawRosterCredits(canvas, team, 380, portraitRect.Bottom + 145, 680, accent, SKTextAlign.Center);
        }
        else
        {
            PosterDrawing.DrawCenteredText(canvas, $"0{index + 1}", portraitRect.MidX, portraitRect.Top - 24, 66, PosterDrawing.WithAlpha(accent, 75), true, 120, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), portraitRect.MidX, portraitRect.Bottom + 60, 48, Bone, true, 760, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}   •   POWER {PosterDrawing.TeamScore(team)}", portraitRect.MidX, portraitRect.Bottom + 98, 14, accent, true, 780);
            DrawRosterCredits(canvas, team, portraitRect.Left - 80, portraitRect.Bottom + 145, portraitRect.Width + 160, accent, SKTextAlign.Center);
        }
    }

    private static void DrawRosterCredits(SKCanvas canvas, TeamCardTeam team, float left, float top, float width, SKColor accent, SKTextAlign align)
    {
        var labels = PosterDrawing.VisibleSlots(team, 6)
            .Select(slot => slot.Players.Count > 1
                ? string.Join(" + ", slot.Players.Select(player => player.Name.ToUpperInvariant()))
                : (slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName).ToUpperInvariant())
            .ToList();

        var x = align switch
        {
            SKTextAlign.Right => left + width,
            SKTextAlign.Center => left + width / 2,
            _ => left
        };

        using var rule = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 115), StrokeWidth = 1.5f, IsAntialias = true };
        var ruleLeft = align == SKTextAlign.Right ? left + width - Math.Min(width, 245) : left;
        var ruleRight = align == SKTextAlign.Left ? left + Math.Min(width, 245) : left + width;
        if (align == SKTextAlign.Center)
        {
            ruleLeft = left + width * .25f;
            ruleRight = left + width * .75f;
        }
        canvas.DrawLine(ruleLeft, top - 13, ruleRight, top - 13, rule);

        for (var row = 0; row < 3; row++)
        {
            var first = row * 2;
            if (first >= labels.Count) break;
            var line = first + 1 < labels.Count ? $"{labels[first]}   •   {labels[first + 1]}" : labels[first];
            PosterDrawing.DrawText(canvas, line, x, top + row * 27, 12, PosterDrawing.WithAlpha(Bone, 190), true, width, null, align);
        }
    }

    private static void DrawPortraitAura(SKCanvas canvas, SKRect rect, SKColor accent)
    {
        var glowRect = new SKRect(rect.Left - 70, rect.Top - 70, rect.Right + 70, rect.Bottom + 70);
        using var aura = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(rect.MidX, rect.MidY), Math.Max(rect.Width, rect.Height) * .72f, [PosterDrawing.WithAlpha(accent, 105), PosterDrawing.WithAlpha(accent, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawOval(glowRect, aura);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 28), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + 12, rect.Top + 24, rect.Right + 12, rect.Bottom + 24), 28, 28, shadow);
    }

    private static void DrawPortraitFade(SKCanvas canvas, SKRect rect)
    {
        using var fade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.MidX, rect.Top + rect.Height * .48f), new SKPoint(rect.MidX, rect.Bottom), [new SKColor(0, 0, 0, 0), new SKColor(2, 3, 7, 205)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, rect.Width * .18f, rect.Height * .18f, fade);
    }

    private static void DrawVersusMark(SKCanvas canvas)
    {
        using var halo = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(720, 720), 128, [new SKColor(244, 163, 72, 70), new SKColor(244, 163, 72, 0)], [0f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(720, 720, 128, halo);
        using var path = new SKPath();
        path.MoveTo(720, 654);
        path.LineTo(781, 760);
        path.LineTo(659, 760);
        path.Close();
        using var fill = new SKPaint { Color = new SKColor(10, 11, 16, 235), IsAntialias = true };
        using var border = new SKPaint { Color = PosterDrawing.WithAlpha(Amber, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, border);
        PosterDrawing.DrawCenteredText(canvas, "VS", 720, 735, 26, Bone, true, 80, PosterDrawing.BlackTypeface);
    }

    private static void DrawCinematicBackdrop(SKCanvas canvas, string sessionName)
    {
        using (var baseGradient = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1800), [new SKColor(9, 13, 23), new SKColor(20, 12, 16), new SKColor(3, 4, 8)], [0f, .48f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, 1440, 1800), baseGradient);

        DrawSpotlight(canvas, new SKPoint(220, 590), Crimson, 500);
        DrawSpotlight(canvas, new SKPoint(1220, 590), Ice, 500);
        DrawSpotlight(canvas, new SKPoint(720, 1120), Amber, 540);
        DrawLightBeam(canvas, 240, 0, 425, 910, Crimson);
        DrawLightBeam(canvas, 1200, 0, 1015, 910, Ice);
        DrawLightBeam(canvas, 720, 300, 720, 1420, Amber);
        DrawDust(canvas, sessionName);
        PosterDrawing.DrawCenteredText(canvas, "ONE NIGHT", 720, 1615, 116, new SKColor(255, 255, 255, 13), true, 1300, PosterDrawing.BlackTypeface);
    }

    private static void DrawSpotlight(SKCanvas canvas, SKPoint center, SKColor color, float radius)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(center, radius, [PosterDrawing.WithAlpha(color, 80), PosterDrawing.WithAlpha(color, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawCircle(center.X, center.Y, radius, paint);
    }

    private static void DrawLightBeam(SKCanvas canvas, float topX, float topY, float targetX, float targetY, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(topX - 42, topY);
        path.LineTo(topX + 42, topY);
        path.LineTo(targetX + 175, targetY);
        path.LineTo(targetX - 175, targetY);
        path.Close();
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(topX, topY), new SKPoint(targetX, targetY), [PosterDrawing.WithAlpha(color, 38), PosterDrawing.WithAlpha(color, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawPath(path, paint);
    }

    private static void DrawDust(SKCanvas canvas, string sessionName)
    {
        var random = new Random(PosterDrawing.StableSeed(sessionName) & int.MaxValue);
        for (var i = 0; i < 260; i++)
        {
            var x = random.Next(20, 1420);
            var y = random.Next(390, 1660);
            var alpha = (byte)random.Next(8, 34);
            var radius = random.Next(1, 4);
            using var dust = new SKPaint { Color = new SKColor(246, 231, 200, alpha), IsAntialias = true };
            canvas.DrawCircle(x, y, radius, dust);
        }
        for (var i = 0; i < 22; i++)
        {
            var x = random.Next(0, 1440);
            var y = random.Next(420, 1620);
            using var streak = new SKPaint { Color = new SKColor(255, 255, 255, (byte)random.Next(8, 24)), StrokeWidth = 1 };
            canvas.DrawLine(x, y, x + random.Next(-12, 13), y + random.Next(35, 120), streak);
        }
    }

    private static void DrawFilmScratch(SKCanvas canvas, SKRect rect, int seed)
    {
        var random = new Random(seed * 7919 + 73);
        for (var i = 0; i < 5; i++)
        {
            var x = rect.Left + random.Next(20, Math.Max(21, (int)rect.Width - 20));
            using var line = new SKPaint { Color = new SKColor(255, 255, 255, 15), StrokeWidth = 1 };
            canvas.DrawLine(x, rect.Top + 10, x + random.Next(-4, 5), rect.Bottom - 10, line);
        }
    }

    private static void DrawFilmFrame(SKCanvas canvas)
    {
        using var outer = new SKPaint { Color = new SKColor(245, 238, 221, 48), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
        canvas.DrawRect(new SKRect(25, 25, 1415, 1775), outer);
        using var inner = new SKPaint { Color = new SKColor(245, 238, 221, 16), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRect(new SKRect(36, 36, 1404, 1764), inner);
    }

    private static void DrawBillingBlock(SKCanvas canvas, IReadOnlyList<TeamCardTeam> teams)
    {
        if (teams.Count < 3) return;
        var captains = teams.Select(team => PosterDrawing.FindCaptain(team)?.Name.ToUpperInvariant() ?? "CAPTAIN").ToList();
        PosterDrawing.DrawCenteredText(canvas, $"STARRING  {captains[0]}   •   {captains[1]}   •   {captains[2]}", 720, 1648, 11, PosterDrawing.WithAlpha(Bone, 130), true, 1180);
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
