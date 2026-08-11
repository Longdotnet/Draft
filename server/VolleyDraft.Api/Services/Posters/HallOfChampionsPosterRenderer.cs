using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Poster 02 — Hall of Champions.
/// Luxury Art-Deco championship composition with ceremonial arches, gold foil geometry,
/// hero captain portraits, Zalo-avatar roster rows and a volleyball trophy centerpiece.
/// Poster 02 stays data-driven and does not change assignment/rotation behavior.
/// </summary>
internal static class HallOfChampionsPosterRenderer
{
    private static readonly SKColor Obsidian = new(5, 6, 7);
    private static readonly SKColor Midnight = new(11, 13, 15);
    private static readonly SKColor Gold = new(220, 177, 77);
    private static readonly SKColor PaleGold = new(244, 214, 145);
    private static readonly SKColor Champagne = new(242, 229, 196);
    private static readonly SKColor Bronze = new(158, 104, 50);
    private static readonly SKColor Muted = new(168, 151, 116);

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Obsidian);
        var canvas = surface.Canvas;

        DrawLuxuryBackdrop(canvas, sessionName);
        DrawDecoFrame(canvas);
        DrawHeader(canvas, sessionName, startTime, location);

        var visibleTeams = teams.Take(3).ToList();
        if (visibleTeams.Count == 0)
        {
            DrawEmptyState(canvas);
        }
        else
        {
            DrawTeamHall(canvas, visibleTeams);
        }

        DrawVolleyballTrophy(canvas);
        DrawFooter(canvas);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawLuxuryBackdrop(SKCanvas canvas, string sessionName)
    {
        using (var gradient = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateRadialGradient(
                       new SKPoint(720, 710),
                       1120,
                       [new SKColor(33, 27, 15), Midnight, Obsidian],
                       [0f, .54f, 1f],
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(new SKRect(0, 0, PosterDrawing.Width, PosterDrawing.Height), gradient);
        }

        // Subtle black-marble veins.
        var random = new Random(PosterDrawing.StableSeed($"hall-of-champions:{sessionName}") & int.MaxValue);
        using var vein = new SKPaint
        {
            Color = new SKColor(218, 191, 129, 13),
            StrokeWidth = 1.1f,
            IsAntialias = true
        };
        for (var lineIndex = 0; lineIndex < 28; lineIndex++)
        {
            var x = random.Next(-80, PosterDrawing.Width + 80);
            var y = random.Next(80, PosterDrawing.Height - 80);
            using var path = new SKPath();
            path.MoveTo(x, y);
            for (var step = 1; step <= 5; step++)
            {
                var px = x + step * random.Next(45, 100) * (random.Next(0, 2) == 0 ? -1 : 1);
                var py = y + step * random.Next(35, 90);
                path.LineTo(px, py);
            }
            canvas.DrawPath(path, vein);
        }

        // Gold dust / foil flecks concentrated near the stage.
        using var dust = new SKPaint { IsAntialias = true };
        for (var index = 0; index < 520; index++)
        {
            var x = random.Next(35, PosterDrawing.Width - 35);
            var y = random.Next(310, 1660);
            var radius = random.NextDouble() < .88 ? .8f : 1.8f;
            dust.Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, (byte)random.Next(14, 58));
            canvas.DrawCircle(x, y, radius, dust);
        }
    }

    private static void DrawDecoFrame(SKCanvas canvas)
    {
        DrawFrameRect(canvas, new SKRect(18, 18, PosterDrawing.Width - 18, PosterDrawing.Height - 18), Gold, 2.4f);
        DrawFrameRect(canvas, new SKRect(31, 31, PosterDrawing.Width - 31, PosterDrawing.Height - 31), new SKColor(Gold.Red, Gold.Green, Gold.Blue, 128), 1.1f);
        DrawFrameRect(canvas, new SKRect(47, 47, PosterDrawing.Width - 47, PosterDrawing.Height - 47), new SKColor(Gold.Red, Gold.Green, Gold.Blue, 88), 1f);

        DrawDecoCorner(canvas, 55, 56, 1, 1);
        DrawDecoCorner(canvas, PosterDrawing.Width - 55, 56, -1, 1);
        DrawDecoCorner(canvas, 55, PosterDrawing.Height - 56, 1, -1);
        DrawDecoCorner(canvas, PosterDrawing.Width - 55, PosterDrawing.Height - 56, -1, -1);

        // Grand upper arch.
        using var arch = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 150),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.2f,
            IsAntialias = true
        };
        using var archPath = new SKPath();
        archPath.MoveTo(76, 405);
        archPath.CubicTo(164, 172, 370, 72, 720, 72);
        archPath.CubicTo(1070, 72, 1276, 172, 1364, 405);
        canvas.DrawPath(archPath, arch);

        using var arch2 = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 64),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        using var archPath2 = new SKPath();
        archPath2.MoveTo(90, 410);
        archPath2.CubicTo(180, 194, 388, 92, 720, 92);
        archPath2.CubicTo(1052, 92, 1260, 194, 1350, 410);
        canvas.DrawPath(archPath2, arch2);
    }

    private static void DrawHeader(
        SKCanvas canvas,
        string sessionName,
        DateTimeOffset? startTime,
        string? location)
    {
        DrawTrophyEmblem(canvas, 720, 70, .78f);
        DrawSunburst(canvas, new SKPoint(720, 116), 130, 22, new SKColor(Gold.Red, Gold.Green, Gold.Blue, 44));

        PosterDrawing.DrawCenteredText(canvas, "HALL OF", 720, 154, 44, PaleGold, true, 800, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "CHAMPIONS", 720, 252, 105, Gold, true, 1210, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT  •  MATCHDAY EDITION", 720, 300, 19, Champagne, true, 740);

        DrawOrnamentalRule(canvas, 340, 329, 1100);
        PosterDrawing.DrawCenteredText(canvas, sessionName.ToUpperInvariant(), 720, 365, 26, PaleGold, true, 1000, PosterDrawing.BoldTypeface);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.BuildMetadata(startTime, location), 720, 401, 16, Muted, false, 1040);
    }

    private static void DrawTeamHall(SKCanvas canvas, IReadOnlyList<TeamCardTeam> teams)
    {
        const float margin = 62;
        const float gap = 18;
        const float top = 430;
        const float bottom = 1480;
        var width = (PosterDrawing.Width - margin * 2 - gap * 2) / 3f;

        for (var index = 0; index < teams.Count; index++)
        {
            var left = margin + index * (width + gap);
            DrawChampionColumn(canvas, new SKRect(left, top, left + width, bottom), teams[index], index);
        }
    }

    private static void DrawChampionColumn(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index)
    {
        var number = (index + 1).ToString("00");
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer(team.CaptainName ?? "CAPTAIN", IsCaptain: true);
        var supportingSlots = BuildSupportingSlots(team, captain).Take(6).ToList();

        using var archPath = BuildColumnArch(rect);
        using (var fill = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(rect.Left, rect.Top),
                       new SKPoint(rect.Right, rect.Bottom),
                       [new SKColor(31, 26, 17, 232), new SKColor(8, 9, 10, 246)],
                       [0f, 1f],
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawPath(archPath, fill);
        }
        using (var border = new SKPaint
               {
                   Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 155),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 2,
                   IsAntialias = true
               })
        {
            canvas.DrawPath(archPath, border);
        }

        var centerX = rect.MidX;
        DrawSunburst(canvas, new SKPoint(centerX, rect.Top + 192), 165, 34, new SKColor(Gold.Red, Gold.Green, Gold.Blue, 42));
        PosterDrawing.DrawCenteredText(canvas, number, centerX, rect.Top + 105, 86, new SKColor(Gold.Red, Gold.Green, Gold.Blue, 190), true, 180, PosterDrawing.BlackTypeface);

        var heroRect = new SKRect(rect.Left + 65, rect.Top + 102, rect.Right - 65, rect.Top + 392);
        DrawCaptainHero(canvas, captain, heroRect);
        DrawLaurelPair(canvas, centerX, rect.Top + 318, 118, 132);

        // Team name plaque.
        var plaque = new SKRect(rect.Left + 30, rect.Top + 390, rect.Right - 30, rect.Top + 486);
        DrawDecoPlaque(canvas, plaque, new SKColor(10, 11, 12, 244), Gold);
        PosterDrawing.DrawCenteredText(canvas, "TEAM", centerX, rect.Top + 417, 12, Muted, true, plaque.Width - 30);
        PosterDrawing.DrawCenteredText(canvas, team.Name.ToUpperInvariant(), centerX, rect.Top + 464, 36, PaleGold, true, plaque.Width - 42, PosterDrawing.BlackTypeface);

        PosterDrawing.DrawCenteredText(canvas, "CAPTAIN", centerX, rect.Top + 518, 11, Muted, true, 130);
        PosterDrawing.DrawCenteredText(canvas, captain.Name.ToUpperInvariant(), centerX, rect.Top + 548, 19, Champagne, true, rect.Width - 72, PosterDrawing.BoldTypeface);
        PosterDrawing.DrawCenteredText(canvas, "POWER", centerX, rect.Top + 583, 11, Muted, true, 100);
        PosterDrawing.DrawCenteredText(canvas, PosterDrawing.TeamScore(team), centerX, rect.Top + 632, 48, Gold, true, 130, PosterDrawing.BlackTypeface);

        var rosterRect = new SKRect(rect.Left + 34, rect.Top + 660, rect.Right - 34, rect.Bottom - 38);
        DrawRosterPlaque(canvas, rosterRect, supportingSlots);
    }

    private static void DrawCaptainHero(SKCanvas canvas, TeamCardPlayer captain, SKRect rect)
    {
        // Gold halo behind the portrait.
        using var halo = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 38),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 28),
            IsAntialias = true
        };
        canvas.DrawOval(rect, halo);

        PosterDrawing.DrawAvatar(
            canvas,
            captain,
            rect,
            Gold,
            PosterAvatarShape.Square,
            strongBorder: true,
            grayscale: true);

        // Champagne overlay keeps real Zalo avatar identity but turns it into a ceremonial portrait.
        using var tint = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 27),
            BlendMode = SKBlendMode.Screen,
            IsAntialias = true
        };
        canvas.DrawRect(rect, tint);

        // Engraved horizontal micro-lines.
        using var scan = new SKPaint
        {
            Color = new SKColor(255, 232, 178, 23),
            StrokeWidth = 1,
            IsAntialias = true
        };
        for (var y = rect.Top + 6; y < rect.Bottom; y += 8)
            canvas.DrawLine(rect.Left + 4, y, rect.Right - 4, y, scan);
    }

    private static IReadOnlyList<SupportingSlot> BuildSupportingSlots(TeamCardTeam team, TeamCardPlayer captain)
    {
        var rows = new List<SupportingSlot>();
        foreach (var slot in team.Slots)
        {
            var players = slot.Players
                .Where(player => !ReferenceEquals(player, captain))
                .Where(player => !player.IsCaptain)
                .Where(player => string.IsNullOrWhiteSpace(team.CaptainName) ||
                                 !string.Equals(player.Name, team.CaptainName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (players.Count == 0) continue;
            rows.Add(new SupportingSlot(slot.DisplayName, players));
        }
        return rows;
    }

    private static void DrawRosterPlaque(SKCanvas canvas, SKRect rect, IReadOnlyList<SupportingSlot> rows)
    {
        DrawDecoPlaque(canvas, rect, new SKColor(9, 10, 11, 238), new SKColor(Gold.Red, Gold.Green, Gold.Blue, 145));
        PosterDrawing.DrawCenteredText(canvas, "ROSTER", rect.MidX, rect.Top + 29, 13, Gold, true, rect.Width - 30);
        DrawOrnamentalRule(canvas, rect.Left + 28, rect.Top + 44, rect.Right - 28);

        if (rows.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "LINEUP PENDING", rect.MidX, rect.MidY + 8, 17, Muted, true, rect.Width - 46);
            return;
        }

        var contentTop = rect.Top + 58;
        var contentBottom = rect.Bottom - 18;
        var rowHeight = Math.Min(62f, (contentBottom - contentTop) / Math.Max(1, rows.Count));

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var centerY = contentTop + rowHeight * index + rowHeight / 2f;
            DrawSupportingRow(canvas, rect, centerY, rowHeight, row, index);
        }
    }

    private static void DrawSupportingRow(
        SKCanvas canvas,
        SKRect rosterRect,
        float centerY,
        float rowHeight,
        SupportingSlot row,
        int index)
    {
        var top = centerY - rowHeight / 2f + 3;
        var bottom = centerY + rowHeight / 2f - 3;
        using var rowFill = new SKPaint
        {
            Color = index % 2 == 0 ? new SKColor(255, 241, 204, 9) : new SKColor(0, 0, 0, 16),
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(rosterRect.Left + 14, top, rosterRect.Right - 14, bottom), rowFill);

        var avatarSize = Math.Min(42f, rowHeight - 10);
        var avatarX = rosterRect.Left + 25;
        if (row.Players.Count > 1)
        {
            PosterDrawing.DrawOverlappingAvatars(
                canvas,
                row.Players,
                avatarX,
                centerY,
                avatarSize,
                Gold,
                PosterAvatarShape.Circle,
                grayscale: false);
        }
        else
        {
            var avatarRect = new SKRect(
                avatarX,
                centerY - avatarSize / 2f,
                avatarX + avatarSize,
                centerY + avatarSize / 2f);
            PosterDrawing.DrawAvatar(
                canvas,
                row.Players[0],
                avatarRect,
                Gold,
                PosterAvatarShape.Circle,
                strongBorder: true,
                grayscale: false);
        }

        var nameX = avatarX + (row.Players.Count > 1 ? avatarSize * 1.65f : avatarSize) + 15;
        var name = row.Players.Count > 1
            ? string.Join(" + ", row.Players.Take(2).Select(player => player.Name))
            : row.Players[0].Name;

        PosterDrawing.DrawText(canvas, (index + 1).ToString("00"), nameX, centerY + 5, 11, Muted, true, 25);
        PosterDrawing.DrawText(canvas, name, nameX + 31, centerY + 6, 14, Champagne, true, rosterRect.Right - nameX - 44);

        if (row.Players.Count > 1)
            PosterDrawing.DrawText(canvas, "SHARED", rosterRect.Right - 72, centerY - 10, 8, Gold, true, 58);

        using var separator = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 38),
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawLine(rosterRect.Left + 18, bottom + 2, rosterRect.Right - 18, bottom + 2, separator);
    }

    private static void DrawVolleyballTrophy(SKCanvas canvas)
    {
        // Laurels behind the pedestal.
        DrawLaurelPair(canvas, 720, 1548, 124, 138);

        using var glow = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 35),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 28),
            IsAntialias = true
        };
        canvas.DrawCircle(720, 1556, 112, glow);

        // Pedestal.
        using var pedestal = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 1514),
                new SKPoint(0, 1636),
                [new SKColor(91, 62, 29), new SKColor(17, 15, 12), new SKColor(83, 52, 23)],
                [0f, .55f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawOval(new SKRect(588, 1584, 852, 1640), pedestal);
        canvas.DrawRoundRect(new SKRect(620, 1600, 820, 1652), 8, 8, pedestal);
        DrawFrameRect(canvas, new SKRect(635, 1610, 805, 1643), new SKColor(Gold.Red, Gold.Green, Gold.Blue, 165), 1.5f);
        PosterDrawing.DrawCenteredText(canvas, "VOLLEY DRAFT", 720, 1635, 14, Gold, true, 150);

        DrawVolleyball(canvas, new SKPoint(720, 1536), 93);
    }

    private static void DrawVolleyball(SKCanvas canvas, SKPoint center, float radius)
    {
        using var ball = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(center.X - radius * .28f, center.Y - radius * .32f),
                radius * 1.2f,
                [new SKColor(255, 246, 220), new SKColor(219, 194, 141), new SKColor(75, 59, 37)],
                [0f, .58f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(center.X, center.Y, radius, ball);

        using var seam = new SKPaint
        {
            Color = new SKColor(14, 14, 14, 225),
            StrokeWidth = 10,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        var outer = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        canvas.DrawArc(outer, -58, 122, false, seam);
        canvas.DrawArc(new SKRect(center.X - radius * .72f, center.Y - radius, center.X + radius * .55f, center.Y + radius), 72, 144, false, seam);
        canvas.DrawArc(new SKRect(center.X - radius, center.Y - radius * .58f, center.X + radius, center.Y + radius * .64f), 184, 112, false, seam);

        using var outline = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 195),
            StrokeWidth = 3,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawCircle(center.X, center.Y, radius, outline);
    }

    private static void DrawFooter(SKCanvas canvas)
    {
        DrawOrnamentalRule(canvas, 245, 1690, 1195);
        PosterDrawing.DrawCenteredText(
            canvas,
            "THREE TEAMS  •  ONE COURT  •  ONE LEGACY",
            720,
            1740,
            18,
            PaleGold,
            true,
            920,
            PosterDrawing.BoldTypeface);
        PosterDrawing.DrawCenteredText(canvas, "HALL OF CHAMPIONS  /  VOLLEY DRAFT", 720, 1772, 10, Muted, true, 500);
    }

    private static void DrawEmptyState(SKCanvas canvas)
    {
        PosterDrawing.DrawCenteredText(canvas, "THE HALL AWAITS ITS CHAMPIONS", 720, 870, 44, PaleGold, true, 1040, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 920, 18, Muted, false, 760);
    }

    private static SKPath BuildColumnArch(SKRect rect)
    {
        var path = new SKPath();
        var shoulderY = rect.Top + 120;
        path.MoveTo(rect.Left, rect.Bottom);
        path.LineTo(rect.Left, shoulderY);
        path.CubicTo(rect.Left + 22, rect.Top + 38, rect.Left + 112, rect.Top, rect.MidX, rect.Top);
        path.CubicTo(rect.Right - 112, rect.Top, rect.Right - 22, rect.Top + 38, rect.Right, shoulderY);
        path.LineTo(rect.Right, rect.Bottom);
        path.Close();
        return path;
    }

    private static void DrawSunburst(SKCanvas canvas, SKPoint center, float radius, int rays, SKColor color)
    {
        using var paint = new SKPaint { Color = color, StrokeWidth = 1.2f, IsAntialias = true };
        for (var index = 0; index < rays; index++)
        {
            var angle = index * MathF.PI * 2f / rays;
            var inner = radius * .22f;
            canvas.DrawLine(
                center.X + MathF.Cos(angle) * inner,
                center.Y + MathF.Sin(angle) * inner,
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius,
                paint);
        }
    }

    private static void DrawLaurelPair(SKCanvas canvas, float centerX, float centerY, float width, float height)
    {
        DrawLaurel(canvas, centerX - width * .54f, centerY, width * .45f, height, -1);
        DrawLaurel(canvas, centerX + width * .54f, centerY, width * .45f, height, 1);
    }

    private static void DrawLaurel(SKCanvas canvas, float x, float y, float width, float height, int direction)
    {
        using var stem = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 150),
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawLine(x, y + height * .45f, x + direction * width * .46f, y - height * .42f, stem);

        using var leaf = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 170),
            IsAntialias = true
        };
        for (var index = 0; index < 7; index++)
        {
            var t = index / 6f;
            var cx = x + direction * width * .42f * t;
            var cy = y + height * .42f - height * .82f * t;
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(direction * (-38 + index * 3));
            canvas.DrawOval(new SKRect(-5, -14, 5, 14), leaf);
            canvas.Restore();
        }
    }

    private static void DrawDecoPlaque(SKCanvas canvas, SKRect rect, SKColor fill, SKColor border)
    {
        const float cut = 16;
        using var path = new SKPath();
        path.MoveTo(rect.Left + cut, rect.Top);
        path.LineTo(rect.Right - cut, rect.Top);
        path.LineTo(rect.Right, rect.MidY);
        path.LineTo(rect.Right - cut, rect.Bottom);
        path.LineTo(rect.Left + cut, rect.Bottom);
        path.LineTo(rect.Left, rect.MidY);
        path.Close();

        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawPath(path, fillPaint);
        using var borderPaint = new SKPaint
        {
            Color = border,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawPath(path, borderPaint);
    }

    private static void DrawOrnamentalRule(SKCanvas canvas, float left, float y, float right)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 145),
            StrokeWidth = 1.4f,
            IsAntialias = true
        };
        var center = (left + right) / 2f;
        canvas.DrawLine(left, y, center - 24, y, paint);
        canvas.DrawLine(center + 24, y, right, y, paint);

        using var diamond = new SKPath();
        diamond.MoveTo(center, y - 8);
        diamond.LineTo(center + 10, y);
        diamond.LineTo(center, y + 8);
        diamond.LineTo(center - 10, y);
        diamond.Close();
        using var fill = new SKPaint { Color = Gold, IsAntialias = true };
        canvas.DrawPath(diamond, fill);
    }

    private static void DrawTrophyEmblem(SKCanvas canvas, float centerX, float topY, float scale)
    {
        var cupTop = topY + 10 * scale;
        var cupWidth = 54 * scale;
        var cupHeight = 42 * scale;
        using var gold = new SKPaint
        {
            Color = Gold,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3 * scale,
            IsAntialias = true
        };
        using var fill = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 55),
            IsAntialias = true
        };

        var cup = new SKRect(centerX - cupWidth / 2, cupTop, centerX + cupWidth / 2, cupTop + cupHeight);
        canvas.DrawRoundRect(cup, 5 * scale, 5 * scale, fill);
        canvas.DrawRoundRect(cup, 5 * scale, 5 * scale, gold);
        canvas.DrawArc(new SKRect(cup.Left - 18 * scale, cup.Top + 6 * scale, cup.Left + 10 * scale, cup.Bottom), 88, 182, false, gold);
        canvas.DrawArc(new SKRect(cup.Right - 10 * scale, cup.Top + 6 * scale, cup.Right + 18 * scale, cup.Bottom), -90, 182, false, gold);
        canvas.DrawLine(centerX, cup.Bottom, centerX, cup.Bottom + 20 * scale, gold);
        canvas.DrawLine(centerX - 18 * scale, cup.Bottom + 20 * scale, centerX + 18 * scale, cup.Bottom + 20 * scale, gold);

        using var star = new SKPaint { Color = PaleGold, IsAntialias = true };
        canvas.DrawCircle(centerX, topY, 4.5f * scale, star);
    }

    private static void DrawDecoCorner(SKCanvas canvas, float x, float y, int dx, int dy)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(Gold.Red, Gold.Green, Gold.Blue, 140),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true
        };
        for (var index = 0; index < 4; index++)
        {
            var offset = index * 13;
            canvas.DrawLine(x, y + dy * offset, x + dx * (88 - offset), y + dy * offset, paint);
            canvas.DrawLine(x + dx * offset, y, x + dx * offset, y + dy * (88 - offset), paint);
        }

        using var fan = new SKPath();
        fan.MoveTo(x, y);
        fan.LineTo(x + dx * 86, y);
        fan.LineTo(x, y + dy * 86);
        fan.Close();
        canvas.DrawPath(fan, paint);
    }

    private static void DrawFrameRect(SKCanvas canvas, SKRect rect, SKColor color, float width)
    {
        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            IsAntialias = true
        };
        canvas.DrawRect(rect, paint);
    }

    private sealed record SupportingSlot(string DisplayName, IReadOnlyList<TeamCardPlayer> Players);
}