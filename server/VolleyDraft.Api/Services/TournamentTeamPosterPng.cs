using System.Globalization;
using SkiaSharp;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Tournament / esports-broadcast renderer for the @bot 10 team image.
/// The renderer intentionally stays deterministic and data-driven: names, avatars,
/// scores and shared slots always come from the draft state; only decorative geometry
/// is generated from a stable session seed.
/// </summary>
public static class TournamentTeamPosterPng
{
    public const int Width = 1440;
    public const int Height = 1800;

    private static readonly SKColor BackgroundA = new(4, 7, 18);
    private static readonly SKColor BackgroundB = new(8, 17, 35);
    private static readonly SKColor BackgroundC = new(11, 26, 50);
    private static readonly SKColor Ink = new(241, 245, 249);
    private static readonly SKColor Muted = new(148, 163, 184);
    private static readonly SKColor Soft = new(203, 213, 225);
    private static readonly SKTypeface RegularTypeface = FindTypeface(SKFontStyle.Normal);
    private static readonly SKTypeface BoldTypeface = FindTypeface(SKFontStyle.Bold);
    private static readonly SKTypeface BlackTypeface = FindTypeface(new SKFontStyle(900, 5, SKFontStyleSlant.Upright));

    private static readonly SKColor[] TeamColors =
    [
        new(34, 211, 238),   // electric cyan
        new(251, 146, 60),   // arena orange
        new(192, 132, 252)   // neon violet
    ];

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create tournament poster canvas.");
        var canvas = surface.Canvas;
        canvas.Clear(BackgroundA);

        var visibleTeams = teams.Take(3).ToList();
        DrawArenaBackground(canvas, sessionName, visibleTeams.Count);
        DrawHeader(canvas, sessionName, startTime, location);

        if (visibleTeams.Count == 0)
        {
            DrawEmptyState(canvas);
        }
        else
        {
            const float left = 56;
            const float right = Width - 56;
            const float top = 330;
            const float gap = 28;
            const float availableHeight = 1300;
            var panelHeight = visibleTeams.Count switch
            {
                1 => 580,
                2 => 555,
                _ => (availableHeight - gap * 2) / 3f
            };
            var totalHeight = visibleTeams.Count * panelHeight + Math.Max(0, visibleTeams.Count - 1) * gap;
            var y = top + Math.Max(0, (availableHeight - totalHeight) / 2f);
            for (var index = 0; index < visibleTeams.Count; index += 1)
            {
                DrawTeamPanel(
                    canvas,
                    new SKRect(left, y, right, y + panelHeight),
                    visibleTeams[index],
                    index,
                    TeamColors[index % TeamColors.Length]);
                y += panelHeight + gap;
            }
        }

        DrawFooter(canvas, visibleTeams);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    private static void DrawArenaBackground(SKCanvas canvas, string sessionName, int teamCount)
    {
        using (var gradient = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0),
                       new SKPoint(Width, Height),
                       [BackgroundA, BackgroundB, BackgroundC],
                       [0f, .48f, 1f],
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(new SKRect(0, 0, Width, Height), gradient);
        }

        // Arena-light bloom. Large blurred circles create broadcast depth without
        // requiring external raster assets or a browser runtime.
        for (var index = 0; index < 3; index += 1)
        {
            var color = TeamColors[index];
            var center = index switch
            {
                0 => new SKPoint(80, 420),
                1 => new SKPoint(Width - 40, 940),
                _ => new SKPoint(250, Height - 120)
            };
            using var glow = new SKPaint
            {
                Color = WithAlpha(color, teamCount == 0 ? 22 : 36),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 100)
            };
            canvas.DrawCircle(center.X, center.Y, 250, glow);
        }

        // Oversized tournament watermark.
        DrawText(canvas, "VOLLEY", -10, 225, 205, new SKColor(255, 255, 255, 8), true, Width + 20, BlackTypeface);
        DrawText(canvas, "DRAFT", 720, 1765, 190, new SKColor(255, 255, 255, 7), true, 700, BlackTypeface);

        // Technical grid / court geometry.
        using var grid = new SKPaint
        {
            Color = new SKColor(148, 163, 184, 18),
            StrokeWidth = 1,
            IsAntialias = true
        };
        for (var x = -360; x < Width + 360; x += 118)
            canvas.DrawLine(x, 0, x + 620, Height, grid);
        for (var y = 260; y < Height; y += 160)
            canvas.DrawLine(0, y, Width, y - 140, grid);

        // Stable particles / dashes inspired by esports broadcast overlays.
        var random = new Random(StableSeed(sessionName));
        for (var index = 0; index < 95; index += 1)
        {
            var x = random.Next(20, Width - 20);
            var y = random.Next(20, Height - 20);
            var length = random.Next(3, 15);
            var alpha = (byte)random.Next(14, 42);
            using var particle = new SKPaint
            {
                Color = new SKColor(226, 232, 240, alpha),
                StrokeWidth = random.Next(1, 3),
                IsAntialias = true
            };
            if (index % 4 == 0)
                canvas.DrawLine(x, y, x + length, y, particle);
            else
                canvas.DrawCircle(x, y, random.Next(1, 3), particle);
        }

        // Top / bottom energy rails.
        DrawEnergyRail(canvas, 0, 14, Width, TeamColors[0], TeamColors[2]);
        DrawEnergyRail(canvas, 0, Height - 14, Width, TeamColors[2], TeamColors[1]);
    }

    private static void DrawHeader(
        SKCanvas canvas,
        string sessionName,
        DateTimeOffset? startTime,
        string? location)
    {
        DrawText(canvas, "VOLLEY DRAFT  /  MATCHDAY", 58, 76, 24, TeamColors[0], true, 650);
        DrawText(canvas, "TOURNAMENT LINEUP", Width - 520, 76, 22, new SKColor(226, 232, 240, 170), true, 462);

        DrawText(canvas, sessionName, 56, 168, 68, Ink, true, Width - 112, BlackTypeface);
        var metadata = BuildMetadata(startTime, location);
        DrawText(canvas, metadata, 60, 222, 24, Soft, false, Width - 120);

        using var separator = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(56, 0),
                new SKPoint(Width - 56, 0),
                [WithAlpha(TeamColors[0], 210), new SKColor(255, 255, 255, 28), WithAlpha(TeamColors[2], 210)],
                [0f, .5f, 1f],
                SKShaderTileMode.Clamp),
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawLine(56, 274, Width - 56, 274, separator);

        DrawPill(canvas, "AUTO DRAFT RESULT", 58, 246, 188, 36, new SKColor(15, 23, 42, 220), TeamColors[0], 13);
        DrawText(canvas, "3 TEAM • READY TO PLAY", 268, 272, 16, new SKColor(148, 163, 184, 210), true, 330);
    }

    private static void DrawTeamPanel(SKCanvas canvas, SKRect rect, TeamCardTeam team, int index, SKColor accent)
    {
        // Glow / shadow layer.
        using (var glow = new SKPaint
               {
                   Color = WithAlpha(accent, 36),
                   IsAntialias = true,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26)
               })
        {
            canvas.DrawRoundRect(rect, 30, 30, glow);
        }

        using (var panel = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(rect.Left, rect.Top),
                       new SKPoint(rect.Right, rect.Bottom),
                       [new SKColor(10, 18, 34, 247), new SKColor(12, 24, 44, 238)],
                       null,
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRoundRect(rect, 28, 28, panel);
        }
        using (var border = new SKPaint
               {
                   Color = WithAlpha(accent, 126),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 2,
                   IsAntialias = true
               })
        {
            canvas.DrawRoundRect(rect, 28, 28, border);
        }

        DrawAccentWedge(canvas, rect, accent);
        var number = (index + 1).ToString("00", CultureInfo.InvariantCulture);
        DrawText(
            canvas,
            number,
            rect.Right - 250,
            rect.Bottom - 50,
            Math.Min(190, rect.Height * .48f),
            WithAlpha(accent, 26),
            true,
            230,
            BlackTypeface);

        // Team heading.
        DrawText(canvas, $"TEAM {number}", rect.Left + 34, rect.Top + 48, 18, accent, true, 160);
        DrawText(canvas, team.Name, rect.Left + 34, rect.Top + 93, 38, Ink, true, 530, BlackTypeface);

        var score = team.AverageScore.ToString("0.0", CultureInfo.InvariantCulture);
        DrawMetric(canvas, rect.Right - 330, rect.Top + 26, "TEAM POWER", score, accent);
        var playerCount = team.Slots.Sum(slot => Math.Max(1, slot.Players.Count));
        DrawMetric(canvas, rect.Right - 178, rect.Top + 26, "PLAYERS", playerCount.ToString(CultureInfo.InvariantCulture), accent);
        DrawMetric(canvas, rect.Right - 92, rect.Top + 26, "SLOTS", team.Slots.Count.ToString(CultureInfo.InvariantCulture), accent, 74);

        var captain = FindCaptain(team);
        var hero = new SKRect(rect.Left + 34, rect.Top + 120, rect.Left + 345, rect.Bottom - 28);
        DrawCaptainHero(canvas, hero, team, captain, accent);

        var rosterLeft = rect.Left + 378;
        var rosterRight = rect.Right - 32;
        var rosterTop = rect.Top + 120;
        var rosterBottom = rect.Bottom - 28;
        DrawRosterGrid(canvas, new SKRect(rosterLeft, rosterTop, rosterRight, rosterBottom), team, captain, accent);
    }

    private static void DrawAccentWedge(SKCanvas canvas, SKRect rect, SKColor accent)
    {
        using var fill = new SKPaint
        {
            Color = WithAlpha(accent, 30),
            IsAntialias = true
        };
        using var path = new SKPath();
        path.MoveTo(rect.Left, rect.Top + 28);
        path.LineTo(rect.Left + 116, rect.Top);
        path.LineTo(rect.Left + 38, rect.Bottom);
        path.LineTo(rect.Left, rect.Bottom - 26);
        path.Close();
        canvas.DrawPath(path, fill);

        using var line = new SKPaint { Color = WithAlpha(accent, 220), StrokeWidth = 5, IsAntialias = true };
        canvas.DrawLine(rect.Left + 10, rect.Top + 34, rect.Left + 10, rect.Bottom - 34, line);
    }

    private static TeamCardPlayer? FindCaptain(TeamCardTeam team)
    {
        var captain = team.Slots
            .SelectMany(slot => slot.Players)
            .FirstOrDefault(player => player.IsCaptain);
        if (captain is not null) return captain;
        if (!string.IsNullOrWhiteSpace(team.CaptainName))
        {
            captain = team.Slots
                .SelectMany(slot => slot.Players)
                .FirstOrDefault(player => string.Equals(player.Name, team.CaptainName, StringComparison.OrdinalIgnoreCase));
        }
        return captain;
    }

    private static void DrawCaptainHero(
        SKCanvas canvas,
        SKRect rect,
        TeamCardTeam team,
        TeamCardPlayer? captain,
        SKColor accent)
    {
        using (var hero = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(rect.Left, rect.Top),
                       new SKPoint(rect.Right, rect.Bottom),
                       [WithAlpha(accent, 42), new SKColor(3, 9, 21, 115)],
                       null,
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRoundRect(rect, 22, 22, hero);
        }
        using (var border = new SKPaint
               {
                   Color = WithAlpha(accent, 74),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1.5f,
                   IsAntialias = true
               })
        {
            canvas.DrawRoundRect(rect, 22, 22, border);
        }

        DrawPill(canvas, "CAPTAIN", rect.Left + 18, rect.Top + 17, 92, 27, WithAlpha(accent, 35), accent, 11);

        if (captain is not null)
        {
            var centerX = rect.Left + 88;
            var centerY = rect.Top + 128;
            DrawAvatar(canvas, centerX, centerY, 68, captain, accent, true);

            DrawText(canvas, captain.Name, rect.Left + 26, rect.Top + 235, 27, Ink, true, rect.Width - 52, BlackTypeface);
            DrawText(canvas, "ĐỘI TRƯỞNG", rect.Left + 26, rect.Top + 266, 13, accent, true, 150);
        }
        else
        {
            var centerX = rect.Left + 88;
            var centerY = rect.Top + 128;
            using var ring = new SKPaint
            {
                Color = WithAlpha(accent, 58),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                IsAntialias = true
            };
            canvas.DrawCircle(centerX, centerY, 68, ring);
            DrawText(canvas, "?", centerX - 19, centerY + 21, 54, WithAlpha(accent, 180), true, 50, BlackTypeface);
            DrawText(canvas, "CHƯA CHỌN CAPTAIN", rect.Left + 26, rect.Top + 235, 20, Soft, true, rect.Width - 52);
        }

        var average = team.AverageScore.ToString("0.0", CultureInfo.InvariantCulture);
        DrawText(canvas, "TEAM POWER", rect.Left + 26, rect.Bottom - 48, 12, Muted, true, 100);
        DrawText(canvas, average, rect.Right - 88, rect.Bottom - 40, 28, accent, true, 68, BlackTypeface);
    }

    private static void DrawRosterGrid(
        SKCanvas canvas,
        SKRect rect,
        TeamCardTeam team,
        TeamCardPlayer? captain,
        SKColor accent)
    {
        var rosterSlots = BuildRosterSlots(team, captain).Take(6).ToList();
        if (rosterSlots.Count == 0)
        {
            DrawText(canvas, "ROSTER ĐANG ĐƯỢC CẬP NHẬT", rect.Left + 30, rect.Top + 86, 24, Muted, true, rect.Width - 60);
            return;
        }

        const float columnGap = 16;
        const float rowGap = 12;
        var columnWidth = (rect.Width - columnGap) / 2f;
        var rowHeight = (rect.Height - rowGap * 2) / 3f;
        for (var index = 0; index < rosterSlots.Count; index += 1)
        {
            var column = index % 2;
            var row = index / 2;
            var x = rect.Left + column * (columnWidth + columnGap);
            var y = rect.Top + row * (rowHeight + rowGap);
            DrawRosterSlot(
                canvas,
                new SKRect(x, y, x + columnWidth, y + rowHeight),
                rosterSlots[index],
                index + 1,
                accent);
        }

        var hidden = Math.Max(0, BuildRosterSlots(team, captain).Count - 6);
        if (hidden > 0)
            DrawPill(canvas, $"+{hidden} SLOT", rect.Right - 88, rect.Bottom - 25, 82, 22, WithAlpha(accent, 36), accent, 10);
    }

    private static List<TeamCardSlot> BuildRosterSlots(TeamCardTeam team, TeamCardPlayer? captain)
    {
        var result = new List<TeamCardSlot>();
        foreach (var slot in team.Slots)
        {
            if (captain is null)
            {
                result.Add(slot);
                continue;
            }

            var remaining = slot.Players
                .Where(player => !ReferenceEquals(player, captain) &&
                                 !(player.IsCaptain && string.Equals(player.Name, captain.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (remaining.Count == slot.Players.Count)
            {
                result.Add(slot);
                continue;
            }
            if (remaining.Count > 0)
                result.Add(new TeamCardSlot(slot.DisplayName, remaining, false));
        }
        return result;
    }

    private static void DrawRosterSlot(SKCanvas canvas, SKRect rect, TeamCardSlot slot, int number, SKColor accent)
    {
        using (var fill = new SKPaint
               {
                   Color = new SKColor(15, 28, 48, 204),
                   IsAntialias = true
               })
        {
            canvas.DrawRoundRect(rect, 18, 18, fill);
        }
        using (var border = new SKPaint
               {
                   Color = new SKColor(148, 163, 184, 34),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
                   IsAntialias = true
               })
        {
            canvas.DrawRoundRect(rect, 18, 18, border);
        }

        DrawText(canvas, number.ToString("00", CultureInfo.InvariantCulture), rect.Left + 14, rect.Top + 24, 11, WithAlpha(accent, 180), true, 30);

        var players = slot.Players.Take(2).ToList();
        var avatarX = rect.Left + 48;
        var centerY = rect.MidY;
        for (var index = players.Count - 1; index >= 0; index -= 1)
            DrawAvatar(canvas, avatarX + index * 35, centerY, 29, players[index], accent, false);

        var nameX = avatarX + (players.Count > 1 ? 86 : 47);
        var names = string.Join(" / ", slot.Players.Select(player => player.Name));
        DrawText(canvas, names, nameX, rect.Top + 47, 20, Ink, true, rect.Right - nameX - 14);

        if (slot.Players.Count > 1)
        {
            DrawPill(canvas, "SHARED", nameX, rect.Top + 61, 68, 20, WithAlpha(accent, 30), accent, 9);
            DrawText(canvas, "thay phiên 1 slot", nameX + 78, rect.Top + 76, 11, Muted, false, rect.Right - nameX - 90);
        }
        else
        {
            DrawText(canvas, "ROSTER", nameX, rect.Top + 75, 10, Muted, true, 70);
        }
    }

    private static void DrawAvatar(
        SKCanvas canvas,
        float centerX,
        float centerY,
        float radius,
        TeamCardPlayer player,
        SKColor accent,
        bool hero)
    {
        if (hero)
        {
            using var glow = new SKPaint
            {
                Color = WithAlpha(accent, 72),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16)
            };
            canvas.DrawCircle(centerX, centerY, radius + 8, glow);
        }

        using (var outer = new SKPaint { Color = WithAlpha(accent, 230), IsAntialias = true })
            canvas.DrawCircle(centerX, centerY, radius + 4, outer);
        using (var inner = new SKPaint { Color = new SKColor(4, 10, 23), IsAntialias = true })
            canvas.DrawCircle(centerX, centerY, radius + 1.5f, inner);

        if (player.AvatarData is not null)
        {
            using var bitmap = SKBitmap.Decode(player.AvatarData);
            if (bitmap is not null)
            {
                using var clip = new SKPath();
                clip.AddCircle(centerX, centerY, radius);
                canvas.Save();
                canvas.ClipPath(clip, SKClipOperation.Intersect, true);
                var source = CropSquare(bitmap.Width, bitmap.Height);
                canvas.DrawBitmap(bitmap, source, new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius));
                canvas.Restore();
                return;
            }
        }

        using (var fallback = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(centerX - radius, centerY - radius),
                       new SKPoint(centerX + radius, centerY + radius),
                       [AvatarColor(player.Name, accent, .92f), AvatarColor(player.Name, accent, .54f)],
                       null,
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawCircle(centerX, centerY, radius, fallback);
        }
        var initials = Initials(player.Name);
        var size = hero ? 30 : 16;
        using var text = TextPaint(size, SKColors.White, true, BlackTypeface);
        var width = text.MeasureText(initials);
        canvas.DrawText(initials, centerX - width / 2, centerY + size * .36f, text);
    }

    private static void DrawMetric(
        SKCanvas canvas,
        float x,
        float y,
        string label,
        string value,
        SKColor accent,
        float width = 132)
    {
        var rect = new SKRect(x, y, x + width, y + 66);
        using (var fill = new SKPaint { Color = new SKColor(15, 23, 42, 170), IsAntialias = true })
            canvas.DrawRoundRect(rect, 14, 14, fill);
        using (var border = new SKPaint
               {
                   Color = new SKColor(148, 163, 184, 30),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
                   IsAntialias = true
               })
            canvas.DrawRoundRect(rect, 14, 14, border);
        DrawText(canvas, label, x + 10, y + 19, 9, Muted, true, width - 20);
        DrawText(canvas, value, x + 10, y + 50, 25, accent, true, width - 20, BlackTypeface);
    }

    private static void DrawEmptyState(SKCanvas canvas)
    {
        var rect = new SKRect(110, 540, Width - 110, 1260);
        using (var fill = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(rect.Left, rect.Top),
                       new SKPoint(rect.Right, rect.Bottom),
                       [new SKColor(15, 23, 42, 236), new SKColor(11, 22, 42, 222)],
                       null,
                       SKShaderTileMode.Clamp)
               })
            canvas.DrawRoundRect(rect, 34, 34, fill);
        using (var border = new SKPaint
               {
                   Color = new SKColor(125, 211, 252, 74),
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 2,
                   IsAntialias = true
               })
            canvas.DrawRoundRect(rect, 34, 34, border);

        DrawText(canvas, "00", rect.Left + 54, rect.Top + 250, 250, new SKColor(34, 211, 238, 24), true, 360, BlackTypeface);
        DrawText(canvas, "CHƯA CÓ KẾT QUẢ", rect.Left + 55, rect.Top + 370, 52, Ink, true, rect.Width - 110, BlackTypeface);
        DrawText(canvas, "Chạy draft xong rồi gọi @bot 10 để nhận poster đội hình.", rect.Left + 58, rect.Top + 425, 25, Soft, false, rect.Width - 116);
        DrawPill(canvas, "WAITING FOR DRAFT", rect.Left + 58, rect.Top + 485, 184, 36, new SKColor(34, 211, 238, 24), TeamColors[0], 12);
    }

    private static void DrawFooter(SKCanvas canvas, IReadOnlyList<TeamCardTeam> teams)
    {
        const float y = 1728;
        using var line = new SKPaint { Color = new SKColor(148, 163, 184, 30), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(56, y - 24, Width - 56, y - 24, line);

        DrawText(canvas, "VOLLEY DRAFT", 58, y + 12, 17, Ink, true, 180, BlackTypeface);
        DrawText(canvas, "TOURNAMENT POSTER • GENERATED BY @BOT 10", 223, y + 12, 13, Muted, true, 430);
        var totalPlayers = teams.Sum(team => team.Slots.Sum(slot => Math.Max(1, slot.Players.Count)));
        var footer = teams.Count == 0
            ? "AWAITING DRAFT"
            : $"{teams.Count} TEAMS  /  {totalPlayers} PLAYERS  /  READY";
        DrawText(canvas, footer, Width - 405, y + 12, 13, Soft, true, 345);
    }

    private static void DrawEnergyRail(SKCanvas canvas, float x, float y, float width, SKColor start, SKColor end)
    {
        using var rail = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, y),
                new SKPoint(x + width, y),
                [WithAlpha(start, 230), WithAlpha(end, 230)],
                null,
                SKShaderTileMode.Clamp),
            StrokeWidth = 4,
            IsAntialias = true
        };
        canvas.DrawLine(x, y, x + width, y, rail);
    }

    private static void DrawPill(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        float width,
        float height,
        SKColor background,
        SKColor foreground,
        float fontSize)
    {
        using (var fill = new SKPaint { Color = background, IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(x, y, x + width, y + height), height / 2, height / 2, fill);
        using var paint = TextPaint(fontSize, foreground, true, BoldTypeface);
        var textWidth = paint.MeasureText(text);
        canvas.DrawText(text, x + (width - textWidth) / 2, y + height / 2 + fontSize * .36f, paint);
    }

    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        float fontSize,
        SKColor color,
        bool bold,
        float maxWidth,
        SKTypeface? typeface = null)
    {
        using var paint = TextPaint(fontSize, color, bold, typeface);
        var fitted = FitText(text, paint, maxWidth);
        canvas.DrawText(fitted, x, baseline, paint);
    }

    private static SKPaint TextPaint(float fontSize, SKColor color, bool bold, SKTypeface? typeface = null) => new()
    {
        Color = color,
        TextSize = fontSize,
        Typeface = typeface ?? (bold ? BoldTypeface : RegularTypeface),
        IsAntialias = true,
        SubpixelText = true
    };

    private static string FitText(string value, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (paint.MeasureText(value) <= maxWidth) return value;
        var text = value.Trim();
        while (text.Length > 1 && paint.MeasureText(text + "…") > maxWidth)
            text = text[..^1].TrimEnd();
        return text + "…";
    }

    private static SKRect CropSquare(int width, int height)
    {
        var size = Math.Min(width, height);
        return new SKRect(
            (width - size) / 2f,
            (height - size) / 2f,
            (width + size) / 2f,
            (height + size) / 2f);
    }

    private static string BuildMetadata(DateTimeOffset? startTime, string? location)
    {
        var parts = new List<string>();
        if (startTime is not null)
        {
            var local = startTime.Value.ToOffset(TimeSpan.FromHours(7));
            parts.Add(local.ToString("HH:mm • dddd, dd/MM/yyyy", new CultureInfo("vi-VN")));
        }
        if (!string.IsNullOrWhiteSpace(location)) parts.Add(location.Trim());
        return parts.Count == 0 ? "MATCH INFO ĐANG ĐƯỢC CẬP NHẬT" : string.Join("  •  ", parts);
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "?";
        var first = parts[0][0].ToString();
        var last = parts.Length > 1 ? parts[^1][0].ToString() : string.Empty;
        return (first + last).ToUpper(new CultureInfo("vi-VN"));
    }

    private static SKColor AvatarColor(string name, SKColor fallback, float factor)
    {
        uint hash = 2166136261;
        foreach (var character in name)
        {
            hash ^= character;
            hash *= 16777619;
        }
        var jitter = .82f + (hash % 23) / 100f;
        var mixed = factor * jitter;
        return new SKColor(
            (byte)Math.Clamp(fallback.Red * mixed, 0, 255),
            (byte)Math.Clamp(fallback.Green * mixed, 0, 255),
            (byte)Math.Clamp(fallback.Blue * mixed, 0, 255));
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value ?? string.Empty)
                hash = hash * 31 + character;
            return hash;
        }
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    private static SKTypeface FindTypeface(SKFontStyle style) =>
        SKTypeface.FromFamilyName("Noto Sans", style) ??
        SKTypeface.FromFamilyName("DejaVu Sans", style) ??
        SKTypeface.FromFamilyName("Arial", style) ??
        SKTypeface.FromFamilyName("Segoe UI", style) ??
        SKTypeface.Default;
}
