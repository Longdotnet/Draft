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

        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT PRESENTS", 720, 67, 15, PosterDrawing.WithAlpha(Bone, 165), true, 560);
        PosterDrawing.DrawCenteredText(canvas, "TRIPLE", 720, 174, 92, Bone, true, 1180, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "THREAT", 720, 276, 112, Amber, true, 1220, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, sessionName.ToUpperInvariant(), 720, 329, 24, Bone, true, 1140);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 367, 15, PosterDrawing.WithAlpha(Bone, 140), false, 1120);

        var visible = teams.Take(3).ToList();
        if (visible.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "THE CAST HAS NOT BEEN REVEALED", 720, 875, 38, Bone, true, 1120, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 924, 18, Smoke, false, 820);
            return PosterDrawing.Encode(surface);
        }

        var accents = new[] { Crimson, Ice, Amber };
        var heroRects = BuildHeroRects(visible.Count);

        // Ghost typography is painted before the portraits so the cast feels embedded into a film one-sheet,
        // not placed inside UI cards.
        for (var i = 0; i < visible.Count; i += 1)
            DrawGhostTeamWord(canvas, visible[i], i, accents[i], visible.Count);

        // Back cast first, center/front cast last for a proper ensemble-poster overlap.
        for (var i = 0; i < visible.Count; i += 1)
            DrawFaction(canvas, visible[i], i, accents[i], heroRects[i], visible.Count);

        DrawBillingBlock(canvas, visible);
        PosterDrawing.DrawCenteredText(canvas, "ONE COURT  •  THREE TEAMS  •  NO RETAKES", 720, 1693, 15, PosterDrawing.WithAlpha(Bone, 155), true, 900);
        PosterDrawing.DrawCenteredText(canvas, "A VOLLEY DRAFT MATCHDAY PICTURE", 720, 1730, 11, PosterDrawing.WithAlpha(Amber, 170), true, 700);
        return PosterDrawing.Encode(surface);
    }

    private static IReadOnlyList<SKRect> BuildHeroRects(int count)
    {
        if (count == 1)
            return [new SKRect(330, 440, 1110, 1250)];
        if (count == 2)
            return [new SKRect(-20, 440, 665, 1160), new SKRect(775, 440, 1460, 1160)];
        return
        [
            new SKRect(-35, 425, 620, 1090),
            new SKRect(820, 425, 1475, 1090),
            new SKRect(345, 785, 1095, 1510)
        ];
    }

    private static void DrawFaction(SKCanvas canvas, TeamCardTeam team, int index, SKColor accent, SKRect portraitRect, int teamCount)
    {
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer("CAPTAIN");
        DrawCinematicPortrait(canvas, captain, portraitRect, accent, index == 0 ? -1 : index == 1 ? 1 : 0);

        if (teamCount == 3 && index == 0)
        {
            PosterDrawing.DrawText(canvas, "01", 54, 497, 68, PosterDrawing.WithAlpha(accent, 115), true, 130, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), 55, 957, 48, Bone, true, 510, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", 58, 993, 13, accent, true, 500);
            PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", 58, 1021, 11, PosterDrawing.WithAlpha(Bone, 145), true, 500);
            DrawRosterCredits(canvas, team, 58, 1062, 500, accent, SKTextAlign.Left);
        }
        else if (teamCount == 3 && index == 1)
        {
            PosterDrawing.DrawText(canvas, "02", 1386, 497, 68, PosterDrawing.WithAlpha(accent, 115), true, 130, PosterDrawing.BlackTypeface, SKTextAlign.Right);
            PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), 1385, 957, 48, Bone, true, 510, PosterDrawing.BlackTypeface, SKTextAlign.Right);
            PosterDrawing.DrawText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", 1382, 993, 13, accent, true, 500, null, SKTextAlign.Right);
            PosterDrawing.DrawText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", 1382, 1021, 11, PosterDrawing.WithAlpha(Bone, 145), true, 500, null, SKTextAlign.Right);
            DrawRosterCredits(canvas, team, 882, 1062, 500, accent, SKTextAlign.Right);
        }
        else if (teamCount == 3)
        {
            PosterDrawing.DrawCenteredText(canvas, "03", 720, 861, 70, PosterDrawing.WithAlpha(accent, 120), true, 130, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), 720, 1409, 51, Bone, true, 690, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", 720, 1445, 13, accent, true, 650);
            PosterDrawing.DrawCenteredText(canvas, $"POWER {PosterDrawing.TeamScore(team)}   •   {PosterDrawing.PlayerCount(team)} PLAYERS", 720, 1472, 11, PosterDrawing.WithAlpha(Bone, 145), true, 650);
            DrawRosterCredits(canvas, team, 395, 1514, 650, accent, SKTextAlign.Center);
        }
        else
        {
            PosterDrawing.DrawCenteredText(canvas, $"0{index + 1}", portraitRect.MidX, portraitRect.Top + 65, 68, PosterDrawing.WithAlpha(accent, 105), true, 120, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), portraitRect.MidX, portraitRect.Bottom - 135, 51, Bone, true, 730, PosterDrawing.BlackTypeface);
            PosterDrawing.DrawCenteredText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}   •   POWER {PosterDrawing.TeamScore(team)}", portraitRect.MidX, portraitRect.Bottom - 95, 14, accent, true, 760);
            DrawRosterCredits(canvas, team, portraitRect.Left + 70, portraitRect.Bottom - 50, portraitRect.Width - 140, accent, SKTextAlign.Center);
        }
    }

    private static void DrawCinematicPortrait(SKCanvas canvas, TeamCardPlayer player, SKRect rect, SKColor accent, int slant)
    {
        DrawPortraitAura(canvas, rect, accent);

        using var clip = BuildPortraitClip(rect, slant);
        var save = canvas.Save();
        canvas.ClipPath(clip, antialias: true);

        var drawn = false;
        if (player.AvatarData is { Length: > 0 })
        {
            try
            {
                using var bitmap = SKBitmap.Decode(player.AvatarData);
                if (bitmap is not null && bitmap.Width > 0 && bitmap.Height > 0)
                {
                    var source = CropToAspect(bitmap, rect.Width / rect.Height);
                    using var imagePaint = new SKPaint
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
                    canvas.DrawBitmap(bitmap, source, rect, imagePaint);
                    drawn = true;
                }
            }
            catch
            {
                drawn = false;
            }
        }

        if (!drawn)
        {
            using var fallback = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                    [PosterDrawing.WithAlpha(accent, 210), new SKColor(18, 19, 24)], [0f, 1f], SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(rect, fallback);
            var initial = string.IsNullOrWhiteSpace(player.Name) ? "?" : player.Name.Trim()[0].ToString().ToUpperInvariant();
            PosterDrawing.DrawCenteredText(canvas, initial, rect.MidX, rect.MidY + 70, 210, new SKColor(255, 255, 255, 110), true, rect.Width * .7f, PosterDrawing.BlackTypeface);
        }

        DrawPortraitEdgeFades(canvas, rect);
        DrawFilmScratch(canvas, rect, slant + 2);
        canvas.RestoreToCount(save);
    }

    private static SKPath BuildPortraitClip(SKRect rect, int slant)
    {
        var path = new SKPath();
        if (slant < 0)
        {
            path.MoveTo(rect.Left, rect.Top);
            path.LineTo(rect.Right - 95, rect.Top);
            path.LineTo(rect.Right + 12, rect.Bottom);
            path.LineTo(rect.Left, rect.Bottom);
        }
        else if (slant > 0)
        {
            path.MoveTo(rect.Left + 95, rect.Top);
            path.LineTo(rect.Right, rect.Top);
            path.LineTo(rect.Right, rect.Bottom);
            path.LineTo(rect.Left - 12, rect.Bottom);
        }
        else
        {
            path.MoveTo(rect.Left + 82, rect.Top);
            path.LineTo(rect.Right - 82, rect.Top);
            path.LineTo(rect.Right + 8, rect.Bottom);
            path.LineTo(rect.Left - 8, rect.Bottom);
        }
        path.Close();
        return path;
    }

    private static void DrawPortraitEdgeFades(SKCanvas canvas, SKRect rect)
    {
        using (var horizontal = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.MidY), new SKPoint(rect.Right, rect.MidY),
                       [new SKColor(6, 8, 14, 205), new SKColor(6, 8, 14, 0), new SKColor(6, 8, 14, 0), new SKColor(6, 8, 14, 205)],
                       [0f, .16f, .84f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(rect, horizontal);

        using var vertical = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.MidX, rect.Bottom),
                [new SKColor(6, 8, 14, 70), new SKColor(6, 8, 14, 0), new SKColor(6, 8, 14, 235)],
                [0f, .55f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, vertical);
    }

    private static SKRectI CropToAspect(SKBitmap bitmap, float targetAspect)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        if (sourceAspect > targetAspect)
        {
            var width = (int)(bitmap.Height * targetAspect);
            var left = Math.Max(0, (bitmap.Width - width) / 2);
            return new SKRectI(left, 0, Math.Min(bitmap.Width, left + width), bitmap.Height);
        }
        var height = (int)(bitmap.Width / Math.Max(.01f, targetAspect));
        var top = Math.Max(0, (bitmap.Height - height) / 2);
        return new SKRectI(0, top, bitmap.Width, Math.Min(bitmap.Height, top + height));
    }

    private static void DrawRosterCredits(SKCanvas canvas, TeamCardTeam team, float left, float top, float width, SKColor accent, SKTextAlign align)
    {
        var labels = PosterDrawing.VisibleSlots(team, 6)
            .Select(slot => slot.Players.Count > 1
                ? string.Join(" + ", slot.Players.Select(player => player.Name.ToUpperInvariant()))
                : (slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName).ToUpperInvariant())
            .ToList();
        var x = align switch { SKTextAlign.Right => left + width, SKTextAlign.Center => left + width / 2, _ => left };
        using var rule = new SKPaint { Color = PosterDrawing.WithAlpha(accent, 120), StrokeWidth = 1.4f, IsAntialias = true };
        if (align == SKTextAlign.Center)
            canvas.DrawLine(left + width * .27f, top - 13, left + width * .73f, top - 13, rule);
        else if (align == SKTextAlign.Right)
            canvas.DrawLine(left + width - Math.Min(width, 230), top - 13, left + width, top - 13, rule);
        else
            canvas.DrawLine(left, top - 13, left + Math.Min(width, 230), top - 13, rule);

        for (var row = 0; row < 3; row++)
        {
            var first = row * 2;
            if (first >= labels.Count) break;
            var line = first + 1 < labels.Count ? $"{labels[first]}   •   {labels[first + 1]}" : labels[first];
            PosterDrawing.DrawText(canvas, line, x, top + row * 27, 12, PosterDrawing.WithAlpha(Bone, 192), true, width, null, align);
        }
    }

    private static void DrawGhostTeamWord(SKCanvas canvas, TeamCardTeam team, int index, SKColor accent, int teamCount)
    {
        if (teamCount < 3) return;
        if (index == 0)
            PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), 10, 560, 76, PosterDrawing.WithAlpha(accent, 22), true, 650, PosterDrawing.BlackTypeface);
        else if (index == 1)
            PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), 1430, 560, 76, PosterDrawing.WithAlpha(accent, 22), true, 650, PosterDrawing.BlackTypeface, SKTextAlign.Right);
        else
            PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), 720, 920, 88, PosterDrawing.WithAlpha(accent, 22), true, 920, PosterDrawing.BlackTypeface);
    }

    private static void DrawPortraitAura(SKCanvas canvas, SKRect rect, SKColor accent)
    {
        var glowRect = new SKRect(rect.Left - 100, rect.Top - 100, rect.Right + 100, rect.Bottom + 100);
        using var aura = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(rect.MidX, rect.MidY), Math.Max(rect.Width, rect.Height) * .74f,
                [PosterDrawing.WithAlpha(accent, 82), PosterDrawing.WithAlpha(accent, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawOval(glowRect, aura);
    }

    private static void DrawCinematicBackdrop(SKCanvas canvas, string sessionName)
    {
        using (var baseGradient = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1800),
                       [new SKColor(9, 13, 23), new SKColor(21, 12, 17), new SKColor(3, 4, 8)], [0f, .50f, 1f], SKShaderTileMode.Clamp)
               })
            canvas.DrawRect(new SKRect(0, 0, 1440, 1800), baseGradient);

        DrawSpotlight(canvas, new SKPoint(150, 650), Crimson, 560);
        DrawSpotlight(canvas, new SKPoint(1290, 650), Ice, 560);
        DrawSpotlight(canvas, new SKPoint(720, 1120), Amber, 610);
        DrawLightBeam(canvas, 210, 0, 400, 1060, Crimson);
        DrawLightBeam(canvas, 1230, 0, 1040, 1060, Ice);
        DrawLightBeam(canvas, 720, 250, 720, 1510, Amber);
        DrawDust(canvas, sessionName);
        PosterDrawing.DrawCenteredText(canvas, "ONE NIGHT", 720, 1620, 120, new SKColor(255, 255, 255, 12), true, 1320, PosterDrawing.BlackTypeface);
    }

    private static void DrawSpotlight(SKCanvas canvas, SKPoint center, SKColor color, float radius)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(center, radius, [PosterDrawing.WithAlpha(color, 72), PosterDrawing.WithAlpha(color, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawCircle(center.X, center.Y, radius, paint);
    }

    private static void DrawLightBeam(SKCanvas canvas, float topX, float topY, float targetX, float targetY, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(topX - 48, topY);
        path.LineTo(topX + 48, topY);
        path.LineTo(targetX + 210, targetY);
        path.LineTo(targetX - 210, targetY);
        path.Close();
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(topX, topY), new SKPoint(targetX, targetY),
                [PosterDrawing.WithAlpha(color, 34), PosterDrawing.WithAlpha(color, 0)], [0f, 1f], SKShaderTileMode.Clamp),
            IsAntialias = true
        };
        canvas.DrawPath(path, paint);
    }

    private static void DrawDust(SKCanvas canvas, string sessionName)
    {
        var random = new Random(PosterDrawing.StableSeed(sessionName) & int.MaxValue);
        for (var i = 0; i < 300; i++)
        {
            var x = random.Next(10, 1430);
            var y = random.Next(390, 1660);
            var alpha = (byte)random.Next(7, 30);
            var radius = random.Next(1, 4);
            using var dust = new SKPaint { Color = new SKColor(246, 231, 200, alpha), IsAntialias = true };
            canvas.DrawCircle(x, y, radius, dust);
        }
    }

    private static void DrawFilmScratch(SKCanvas canvas, SKRect rect, int seed)
    {
        var random = new Random(seed * 7919 + 73);
        for (var i = 0; i < 6; i++)
        {
            var x = rect.Left + random.Next(20, Math.Max(21, (int)rect.Width - 20));
            using var line = new SKPaint { Color = new SKColor(255, 255, 255, 13), StrokeWidth = 1 };
            canvas.DrawLine(x, rect.Top + 10, x + random.Next(-4, 5), rect.Bottom - 10, line);
        }
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
