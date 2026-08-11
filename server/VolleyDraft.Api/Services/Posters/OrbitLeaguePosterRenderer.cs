using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

/// <summary>
/// Poster 03 — Orbit League.
/// A kinetic modern-sports composition with three color-coded team orbits, one large
/// captain portrait per team, Zalo-avatar supporting members, and a central volleyball.
/// </summary>
internal static class OrbitLeaguePosterRenderer
{
    private static readonly SKColor Night = new(3, 10, 22);
    private static readonly SKColor Deep = new(5, 17, 34);
    private static readonly SKColor Bone = new(244, 242, 234);
    private static readonly SKColor Muted = new(150, 164, 180);
    private static readonly SKColor Teal = new(30, 211, 194);
    private static readonly SKColor Orange = new(255, 103, 48);
    private static readonly SKColor Violet = new(169, 92, 255);
    private static readonly SKColor Blue = new(48, 161, 255);

    private static readonly SKColor[] TeamAccents = [Teal, Orange, Violet];

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = PosterDrawing.CreateSurface(Night);
        var canvas = surface.Canvas;

        DrawBackdrop(canvas, sessionName);
        DrawHeader(canvas, sessionName);

        var visibleTeams = teams.Take(3).ToList();
        if (visibleTeams.Count == 0)
        {
            DrawEmptyState(canvas);
            DrawFooter(canvas, startTime, location);
            return PosterDrawing.Encode(surface);
        }

        DrawGlobalOrbits(canvas);
        DrawCentralVolleyball(canvas, new SKPoint(720, 748), 174);

        if (visibleTeams.Count >= 1)
            DrawTeamZone(canvas, new SKRect(48, 325, 563, 1032), visibleTeams[0], 0, TeamZoneSide.Left);
        if (visibleTeams.Count >= 2)
            DrawTeamZone(canvas, new SKRect(877, 325, 1392, 1032), visibleTeams[1], 1, TeamZoneSide.Right);
        if (visibleTeams.Count >= 3)
            DrawTeamZone(canvas, new SKRect(270, 1040, 1170, 1568), visibleTeams[2], 2, TeamZoneSide.Bottom);

        DrawFooter(canvas, startTime, location);
        return PosterDrawing.Encode(surface);
    }

    private static void DrawBackdrop(SKCanvas canvas, string sessionName)
    {
        using (var gradient = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateRadialGradient(
                       new SKPoint(720, 730),
                       1080,
                       [new SKColor(13, 38, 64), Deep, Night],
                       [0f, .50f, 1f],
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(new SKRect(0, 0, PosterDrawing.Width, PosterDrawing.Height), gradient);
        }

        var random = new Random(PosterDrawing.StableSeed($"orbit-league:{sessionName}") & int.MaxValue);
        using var star = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 420; i++)
        {
            var x = random.Next(18, PosterDrawing.Width - 18);
            var y = random.Next(20, 1650);
            var bright = random.NextDouble() > .83;
            star.Color = bright
                ? new SKColor(210, 235, 255, (byte)random.Next(80, 175))
                : new SKColor(105, 159, 196, (byte)random.Next(18, 72));
            canvas.DrawCircle(x, y, bright ? 1.55f : .85f, star);
        }

        using var streak = new SKPaint
        {
            StrokeWidth = 2,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        for (var i = 0; i < 38; i++)
        {
            var color = TeamAccents[i % TeamAccents.Length];
            streak.Color = PosterDrawing.WithAlpha(color, (byte)random.Next(18, 64));
            var x = random.Next(-80, PosterDrawing.Width);
            var y = random.Next(80, 1510);
            var length = random.Next(45, 155);
            canvas.DrawLine(x, y, x + length, y - random.Next(5, 45), streak);
        }

        DrawEdgeRibbon(canvas, true, Teal);
        DrawEdgeRibbon(canvas, false, Orange);
    }

    private static void DrawHeader(SKCanvas canvas, string sessionName)
    {
        PosterDrawing.DrawCenteredText(canvas, "DRAFT YOUR LEGACY", 720, 62, 16, new SKColor(246, 197, 151), true, 620);
        PosterDrawing.DrawCenteredText(canvas, "VOLLEY", 720, 166, 88, Bone, true, 1000, PosterDrawing.BlackTypeface);

        using var draftPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 112,
            Typeface = PosterDrawing.BlackTypeface,
            TextAlign = SKTextAlign.Center,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(430, 190),
                new SKPoint(1010, 285),
                [new SKColor(72, 198, 255), new SKColor(36, 113, 255)],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawText("DRAFT", 720, 273, draftPaint);

        PosterDrawing.DrawCenteredText(canvas, "ORBIT  ◯  LEAGUE", 720, 321, 26, new SKColor(201, 215, 228), true, 620);
        PosterDrawing.DrawCenteredText(canvas, sessionName.ToUpperInvariant(), 720, 354, 14, PosterDrawing.WithAlpha(Bone, 150), true, 900);
    }

    private static void DrawGlobalOrbits(SKCanvas canvas)
    {
        DrawOrbit(canvas, new SKRect(275, 396, 1165, 1102), -18, 224, new SKColor(102, 203, 255, 80), 2.2f);
        DrawOrbit(canvas, new SKRect(356, 468, 1084, 1040), 13, 254, new SKColor(255, 130, 81, 65), 1.8f);
        DrawOrbit(canvas, new SKRect(435, 512, 1005, 975), -32, 294, new SKColor(186, 113, 255, 82), 2.0f);

        using var node = new SKPaint { IsAntialias = true };
        var nodes = new[]
        {
            (new SKPoint(343, 662), Teal),
            (new SKPoint(1097, 582), Orange),
            (new SKPoint(938, 972), Violet),
            (new SKPoint(501, 934), Blue)
        };
        foreach (var (point, color) in nodes)
        {
            node.Color = PosterDrawing.WithAlpha(color, 80);
            canvas.DrawCircle(point.X, point.Y, 10, node);
            node.Color = color;
            canvas.DrawCircle(point.X, point.Y, 3.4f, node);
        }
    }

    private static void DrawTeamZone(
        SKCanvas canvas,
        SKRect rect,
        TeamCardTeam team,
        int index,
        TeamZoneSide side)
    {
        var accent = TeamAccents[index];
        var captain = PosterDrawing.FindCaptain(team) ?? new TeamCardPlayer(team.CaptainName ?? "CAPTAIN", IsCaptain: true);
        var supportSlots = BuildSupportingSlots(team, captain).Take(6).ToList();

        DrawZoneGlow(canvas, rect, accent, side);
        DrawZoneOrbit(canvas, rect, accent, side);

        if (side == TeamZoneSide.Bottom)
            DrawBottomTeam(canvas, rect, team, captain, supportSlots, accent, index);
        else
            DrawSideTeam(canvas, rect, team, captain, supportSlots, accent, index, side);
    }

    private static void DrawSideTeam(
        SKCanvas canvas,
        SKRect rect,
        TeamCardTeam team,
        TeamCardPlayer captain,
        IReadOnlyList<TeamCardSlot> supportSlots,
        SKColor accent,
        int index,
        TeamZoneSide side)
    {
        var captainRect = side == TeamZoneSide.Left
            ? new SKRect(rect.Left + 10, rect.Top + 14, rect.Left + 285, rect.Top + 355)
            : new SKRect(rect.Right - 285, rect.Top + 14, rect.Right - 10, rect.Top + 355);

        DrawCaptainGlow(canvas, captainRect, accent);
        PosterDrawing.DrawAvatar(canvas, captain, captainRect, accent, PosterAvatarShape.RoundedSquare, true);
        DrawCaptainOverlay(canvas, captainRect, accent, side);

        var textLeft = side == TeamZoneSide.Left ? rect.Left + 28 : rect.Left + 18;
        var textRight = side == TeamZoneSide.Left ? rect.Right - 18 : rect.Right - 28;
        var align = side == TeamZoneSide.Left ? SKTextAlign.Left : SKTextAlign.Right;
        var anchor = side == TeamZoneSide.Left ? textLeft : textRight;

        PosterDrawing.DrawText(canvas, $"ORBIT {(index + 1):00}", anchor, rect.Top + 398, 13, accent, true, rect.Width - 50, null, align);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), anchor, rect.Top + 448, 42, Bone, true, rect.Width - 52, PosterDrawing.BlackTypeface, align);
        PosterDrawing.DrawText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", anchor, rect.Top + 480, 14, accent, true, rect.Width - 52, null, align);
        PosterDrawing.DrawText(canvas, $"POWER  {PosterDrawing.TeamScore(team)}", anchor, rect.Top + 516, 25, Bone, true, rect.Width - 52, PosterDrawing.BlackTypeface, align);

        DrawSupportAvatarGrid(canvas, supportSlots, new SKRect(rect.Left + 30, rect.Top + 545, rect.Right - 30, rect.Bottom - 16), accent, 3, side);
    }

    private static void DrawBottomTeam(
        SKCanvas canvas,
        SKRect rect,
        TeamCardTeam team,
        TeamCardPlayer captain,
        IReadOnlyList<TeamCardSlot> supportSlots,
        SKColor accent,
        int index)
    {
        var captainRect = new SKRect(rect.Left + 70, rect.Top + 50, rect.Left + 355, rect.Bottom - 38);
        DrawCaptainGlow(canvas, captainRect, accent);
        PosterDrawing.DrawAvatar(canvas, captain, captainRect, accent, PosterAvatarShape.RoundedSquare, true);
        DrawCaptainOverlay(canvas, captainRect, accent, TeamZoneSide.Bottom);

        PosterDrawing.DrawText(canvas, $"ORBIT {(index + 1):00}", rect.Left + 390, rect.Top + 82, 14, accent, true, 200);
        PosterDrawing.DrawText(canvas, team.Name.ToUpperInvariant(), rect.Left + 390, rect.Top + 137, 47, accent, true, rect.Width - 430, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawText(canvas, $"CAPTAIN  {captain.Name.ToUpperInvariant()}", rect.Left + 390, rect.Top + 173, 15, Bone, true, rect.Width - 430);
        PosterDrawing.DrawText(canvas, $"POWER  {PosterDrawing.TeamScore(team)}", rect.Right - 40, rect.Top + 137, 28, Bone, true, 220, PosterDrawing.BlackTypeface, SKTextAlign.Right);

        DrawSupportAvatarGrid(canvas, supportSlots, new SKRect(rect.Left + 390, rect.Top + 205, rect.Right - 36, rect.Bottom - 34), accent, 3, TeamZoneSide.Bottom);
    }

    private static void DrawSupportAvatarGrid(
        SKCanvas canvas,
        IReadOnlyList<TeamCardSlot> slots,
        SKRect rect,
        SKColor accent,
        int columns,
        TeamZoneSide side)
    {
        if (slots.Count == 0)
        {
            PosterDrawing.DrawCenteredText(canvas, "LINEUP PENDING", rect.MidX, rect.MidY, 18, PosterDrawing.WithAlpha(Bone, 120), true, rect.Width - 20);
            return;
        }

        var rows = Math.Max(1, (int)Math.Ceiling(slots.Count / (double)columns));
        var cellWidth = rect.Width / columns;
        var cellHeight = rect.Height / rows;
        var avatarSize = Math.Min(side == TeamZoneSide.Bottom ? 64 : 58, Math.Min(cellWidth * .58f, cellHeight * .58f));

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var col = i % columns;
            var row = i / columns;
            var cx = rect.Left + cellWidth * (col + .5f);
            var cy = rect.Top + cellHeight * row + avatarSize * .56f;

            if (slot.Players.Count > 1)
            {
                var groupWidth = avatarSize + Math.Min(2, slot.Players.Count - 1) * avatarSize * .48f;
                PosterDrawing.DrawOverlappingAvatars(
                    canvas,
                    slot.Players,
                    cx - groupWidth / 2,
                    cy,
                    avatarSize,
                    accent,
                    PosterAvatarShape.Circle);
            }
            else
            {
                var player = slot.Players.FirstOrDefault() ?? new TeamCardPlayer(slot.DisplayName);
                var avatarRect = new SKRect(cx - avatarSize / 2, cy - avatarSize / 2, cx + avatarSize / 2, cy + avatarSize / 2);
                PosterDrawing.DrawAvatar(canvas, player, avatarRect, accent, PosterAvatarShape.Circle, true);
            }

            var label = slot.Players.Count > 1
                ? string.Join(" + ", slot.Players.Take(2).Select(player => ShortName(player.Name)))
                : ShortName(slot.Players.FirstOrDefault()?.Name ?? slot.DisplayName);
            PosterDrawing.DrawCenteredText(canvas, label.ToUpperInvariant(), cx, cy + avatarSize / 2 + 21, 11, Bone, true, cellWidth - 10);

            if (slot.Players.Count > 1)
                PosterDrawing.DrawCenteredText(canvas, "SHARED", cx, cy + avatarSize / 2 + 37, 9, accent, true, cellWidth - 10);
        }
    }

    private static IReadOnlyList<TeamCardSlot> BuildSupportingSlots(TeamCardTeam team, TeamCardPlayer captain)
    {
        var result = new List<TeamCardSlot>();
        foreach (var slot in team.Slots)
        {
            var support = slot.Players
                .Where(player => !player.IsCaptain && !ReferenceEquals(player, captain) &&
                                 !string.Equals(player.Name, captain.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (support.Count == 0) continue;
            result.Add(new TeamCardSlot(slot.DisplayName, support, false));
        }
        return result;
    }

    private static string ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "PLAYER";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 2) return string.Join(' ', parts);
        return string.Join(' ', parts.TakeLast(2));
    }

    private static void DrawZoneGlow(SKCanvas canvas, SKRect rect, SKColor accent, TeamZoneSide side)
    {
        using var glow = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 42),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 38),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, side == TeamZoneSide.Bottom ? 110 : 90, side == TeamZoneSide.Bottom ? 110 : 90, glow);

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Bottom),
                [PosterDrawing.WithAlpha(accent, 36), new SKColor(5, 12, 25, 225)],
                [0f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(rect, side == TeamZoneSide.Bottom ? 110 : 90, side == TeamZoneSide.Bottom ? 110 : 90, fill);
    }

    private static void DrawZoneOrbit(SKCanvas canvas, SKRect rect, SKColor accent, TeamZoneSide side)
    {
        using var border = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 170),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, side == TeamZoneSide.Bottom ? 110 : 90, side == TeamZoneSide.Bottom ? 110 : 90, border);

        var inset = side == TeamZoneSide.Bottom ? 18 : 14;
        using var inner = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 62),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8, 10], 0)
        };
        var innerRect = new SKRect(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
        canvas.DrawRoundRect(innerRect, side == TeamZoneSide.Bottom ? 95 : 76, side == TeamZoneSide.Bottom ? 95 : 76, inner);
    }

    private static void DrawCaptainGlow(SKCanvas canvas, SKRect rect, SKColor accent)
    {
        using var glow = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 80),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 24),
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRect(rect.Left - 10, rect.Top - 10, rect.Right + 10, rect.Bottom + 10), 36, 36, glow);
    }

    private static void DrawCaptainOverlay(SKCanvas canvas, SKRect rect, SKColor accent, TeamZoneSide side)
    {
        using var wash = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Left, rect.Bottom),
                [SKColors.Transparent, PosterDrawing.WithAlpha(Night, 195)],
                [0.55f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(rect, 32, 32, wash);

        var tagWidth = 112f;
        var tagRect = side == TeamZoneSide.Right
            ? new SKRect(rect.Right - tagWidth - 14, rect.Bottom - 42, rect.Right - 14, rect.Bottom - 14)
            : new SKRect(rect.Left + 14, rect.Bottom - 42, rect.Left + tagWidth + 14, rect.Bottom - 14);
        PosterDrawing.DrawPill(canvas, "CAPTAIN", tagRect, accent, Night, PosterDrawing.WithAlpha(Bone, 90), 11);
    }

    private static void DrawCentralVolleyball(SKCanvas canvas, SKPoint center, float radius)
    {
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 150),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 32),
            IsAntialias = true
        };
        canvas.DrawCircle(center.X + 8, center.Y + 20, radius + 8, shadow);

        using var ball = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(center.X - radius * .32f, center.Y - radius * .38f),
                radius * 1.35f,
                [SKColors.White, new SKColor(218, 225, 236), new SKColor(128, 145, 166)],
                [0f, .68f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(center, radius, ball);

        var clip = canvas.Save();
        using var circlePath = new SKPath();
        circlePath.AddCircle(center.X, center.Y, radius);
        canvas.ClipPath(circlePath, antialias: true);

        using var blue = new SKPaint { Color = new SKColor(30, 88, 205), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 72 };
        using var orange = new SKPaint { Color = new SKColor(238, 94, 36), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 66 };
        using var seam = new SKPaint { Color = new SKColor(20, 34, 54, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };

        canvas.DrawArc(new SKRect(center.X - 225, center.Y - 148, center.X + 172, center.Y + 218), -72, 132, false, blue);
        canvas.DrawArc(new SKRect(center.X - 152, center.Y - 240, center.X + 218, center.Y + 142), 34, 126, false, orange);
        canvas.DrawArc(new SKRect(center.X - 228, center.Y - 18, center.X + 196, center.Y + 280), 182, 115, false, blue);
        canvas.DrawArc(new SKRect(center.X - 202, center.Y - 220, center.X + 196, center.Y + 176), 206, 108, false, seam);
        canvas.RestoreToCount(clip);

        using var outline = new SKPaint { Color = new SKColor(235, 246, 255, 190), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        canvas.DrawCircle(center, radius, outline);

        DrawOrbit(canvas, new SKRect(center.X - radius - 96, center.Y - radius + 40, center.X + radius + 96, center.Y + radius - 40), -18, 280, new SKColor(120, 220, 255, 155), 2.4f);
        DrawOrbit(canvas, new SKRect(center.X - radius - 68, center.Y - radius - 70, center.X + radius + 68, center.Y + radius + 70), 27, 270, new SKColor(255, 117, 64, 135), 2.0f);
    }

    private static void DrawOrbit(SKCanvas canvas, SKRect rect, float start, float sweep, SKColor color, float width)
    {
        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawArc(rect, start, sweep, false, paint);
    }

    private static void DrawFooter(SKCanvas canvas, DateTimeOffset? startTime, string? location)
    {
        using var line = new SKPaint { Color = new SKColor(120, 150, 180, 70), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(72, 1632, 1368, 1632, line);

        var metadata = PosterDrawing.BuildMetadata(startTime, location);
        PosterDrawing.DrawCenteredText(canvas, metadata, 720, 1680, 18, new SKColor(211, 220, 229), true, 1180);
        PosterDrawing.DrawCenteredText(canvas, "THREE TEAMS  •  ONE COURT  •  ALL IN", 720, 1742, 31, Bone, true, 1080, PosterDrawing.BlackTypeface);

        PosterDrawing.DrawText(canvas, "03 / ORBIT LEAGUE", 70, 1776, 11, PosterDrawing.WithAlpha(Teal, 180), true, 250);
        PosterDrawing.DrawText(canvas, "VOLLEY DRAFT", 1370, 1776, 11, PosterDrawing.WithAlpha(Orange, 180), true, 250, null, SKTextAlign.Right);
    }

    private static void DrawEmptyState(SKCanvas canvas)
    {
        DrawGlobalOrbits(canvas);
        DrawCentralVolleyball(canvas, new SKPoint(720, 820), 190);
        PosterDrawing.DrawCenteredText(canvas, "LINEUP NOT IN ORBIT YET", 720, 1210, 42, Bone, true, 980, PosterDrawing.BlackTypeface);
        PosterDrawing.DrawCenteredText(canvas, "Draft xong rồi gọi lại @bot 10", 720, 1255, 19, Muted, false, 760);
    }

    private static void DrawEdgeRibbon(SKCanvas canvas, bool left, SKColor accent)
    {
        using var paint = new SKPaint
        {
            Color = PosterDrawing.WithAlpha(accent, 48),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 18,
            IsAntialias = true
        };
        using var path = new SKPath();
        if (left)
        {
            path.MoveTo(-80, 1320);
            path.CubicTo(40, 1160, 80, 980, 30, 820);
            path.CubicTo(-20, 650, 45, 530, 130, 430);
        }
        else
        {
            path.MoveTo(1520, 1320);
            path.CubicTo(1400, 1160, 1360, 980, 1410, 820);
            path.CubicTo(1460, 650, 1395, 530, 1310, 430);
        }
        canvas.DrawPath(path, paint);
    }

    private enum TeamZoneSide
    {
        Left,
        Right,
        Bottom
    }
}
