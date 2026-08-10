using System.Globalization;
using System.Text;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Posters;

internal enum PosterAvatarShape
{
    Circle,
    RoundedSquare,
    Square
}

internal static class PosterDrawing
{
    public const int Width = 1440;
    public const int Height = 1800;

    public static readonly SKTypeface RegularTypeface = FindTypeface(SKFontStyle.Normal);
    public static readonly SKTypeface BoldTypeface = FindTypeface(SKFontStyle.Bold);
    public static readonly SKTypeface BlackTypeface = FindTypeface(new SKFontStyle(900, 5, SKFontStyleSlant.Upright));

    public static SKSurface CreateSurface(SKColor clear)
    {
        var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create team poster canvas.");
        surface.Canvas.Clear(clear);
        return surface;
    }

    public static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    public static void DrawText(
        SKCanvas canvas,
        string? value,
        float x,
        float y,
        float size,
        SKColor color,
        bool bold = false,
        float maxWidth = float.PositiveInfinity,
        SKTypeface? typeface = null,
        SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = value.Trim();
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            TextSize = size,
            Typeface = typeface ?? (bold ? BoldTypeface : RegularTypeface),
            TextAlign = align,
            SubpixelText = true
        };
        if (!float.IsInfinity(maxWidth) && maxWidth > 8)
            text = FitText(text, paint, maxWidth);
        canvas.DrawText(text, x, y, paint);
    }

    public static void DrawCenteredText(
        SKCanvas canvas,
        string value,
        float centerX,
        float y,
        float size,
        SKColor color,
        bool bold = false,
        float maxWidth = float.PositiveInfinity,
        SKTypeface? typeface = null) =>
        DrawText(canvas, value, centerX, y, size, color, bold, maxWidth, typeface, SKTextAlign.Center);

    public static void DrawPill(
        SKCanvas canvas,
        string text,
        SKRect rect,
        SKColor fill,
        SKColor textColor,
        SKColor? border = null,
        float textSize = 14)
    {
        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, fillPaint);
        if (border is not null)
        {
            using var borderPaint = new SKPaint
            {
                Color = border.Value,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f
            };
            canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, borderPaint);
        }
        DrawCenteredText(canvas, text, rect.MidX, rect.MidY + textSize * .34f, textSize, textColor, true, rect.Width - 16);
    }

    public static void DrawAvatar(
        SKCanvas canvas,
        TeamCardPlayer player,
        SKRect rect,
        SKColor accent,
        PosterAvatarShape shape = PosterAvatarShape.Circle,
        bool strongBorder = false,
        bool grayscale = false)
    {
        using var fallbackPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Bottom),
                [WithAlpha(accent, 235), Darken(accent, .55f)],
                null,
                SKShaderTileMode.Clamp)
        };
        DrawShape(canvas, rect, shape, fallbackPaint);

        if (player.AvatarData is { Length: > 0 })
        {
            try
            {
                using var bitmap = SKBitmap.Decode(player.AvatarData);
                if (bitmap is not null && bitmap.Width > 0 && bitmap.Height > 0)
                {
                    var save = canvas.Save();
                    ClipShape(canvas, rect, shape);
                    var source = CropToAspect(bitmap, rect.Width / rect.Height);
                    using var imagePaint = new SKPaint
                    {
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High,
                        ColorFilter = grayscale
                            ? SKColorFilter.CreateColorMatrix(new float[]
                            {
                                .299f, .587f, .114f, 0, 0,
                                .299f, .587f, .114f, 0, 0,
                                .299f, .587f, .114f, 0, 0,
                                0, 0, 0, 1, 0
                            })
                            : null
                    };
                    canvas.DrawBitmap(bitmap, source, rect, imagePaint);
                    canvas.RestoreToCount(save);
                }
                else
                {
                    DrawInitials(canvas, player.Name, rect, shape);
                }
            }
            catch
            {
                DrawInitials(canvas, player.Name, rect, shape);
            }
        }
        else
        {
            DrawInitials(canvas, player.Name, rect, shape);
        }

        using var border = new SKPaint
        {
            Color = strongBorder ? accent : WithAlpha(accent, 150),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strongBorder ? 4 : 2
        };
        DrawShape(canvas, rect, shape, border);
    }

    public static void DrawOverlappingAvatars(
        SKCanvas canvas,
        IReadOnlyList<TeamCardPlayer> players,
        float x,
        float centerY,
        float size,
        SKColor accent,
        PosterAvatarShape shape = PosterAvatarShape.Circle,
        bool grayscale = false)
    {
        var count = Math.Min(3, Math.Max(1, players.Count));
        var offset = size * .56f;
        for (var index = count - 1; index >= 0; index -= 1)
        {
            var player = players.Count > index ? players[index] : new TeamCardPlayer("?");
            var rect = new SKRect(x + index * offset, centerY - size / 2, x + index * offset + size, centerY + size / 2);
            DrawAvatar(canvas, player, rect, accent, shape, index == 0, grayscale);
        }
    }

    public static TeamCardPlayer? FindCaptain(TeamCardTeam team)
    {
        var direct = team.Slots.SelectMany(slot => slot.Players).FirstOrDefault(player => player.IsCaptain);
        if (direct is not null) return direct;
        if (!string.IsNullOrWhiteSpace(team.CaptainName))
        {
            return team.Slots.SelectMany(slot => slot.Players)
                .FirstOrDefault(player => string.Equals(player.Name, team.CaptainName, StringComparison.OrdinalIgnoreCase));
        }
        return team.Slots.SelectMany(slot => slot.Players).FirstOrDefault();
    }

    public static IReadOnlyList<TeamCardSlot> VisibleSlots(TeamCardTeam team, int max = 6) =>
        team.Slots.Take(max).ToList();

    public static int PlayerCount(TeamCardTeam team) =>
        team.Slots.Sum(slot => Math.Max(1, slot.Players.Count));

    public static string TeamScore(TeamCardTeam team) =>
        team.AverageScore.ToString("0.0", CultureInfo.InvariantCulture);

    public static string BuildMetadata(DateTimeOffset? startTime, string? location)
    {
        var parts = new List<string>();
        if (startTime is not null)
        {
            var local = startTime.Value.ToOffset(TimeSpan.FromHours(7));
            parts.Add(local.ToString("ddd dd/MM • HH:mm", CultureInfo.GetCultureInfo("vi-VN")).ToUpperInvariant());
        }
        if (!string.IsNullOrWhiteSpace(location)) parts.Add(location.Trim());
        return parts.Count == 0 ? "MATCHDAY • VOLLEY DRAFT" : string.Join("  /  ", parts);
    }

    public static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    public static SKColor Lighten(SKColor color, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new SKColor(
            (byte)Math.Clamp(color.Red + (255 - color.Red) * amount, 0, 255),
            (byte)Math.Clamp(color.Green + (255 - color.Green) * amount, 0, 255),
            (byte)Math.Clamp(color.Blue + (255 - color.Blue) * amount, 0, 255),
            color.Alpha);
    }

    public static SKColor Darken(SKColor color, float factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new SKColor(
            (byte)(color.Red * factor),
            (byte)(color.Green * factor),
            (byte)(color.Blue * factor),
            color.Alpha);
    }

    public static void DrawDiagonalBand(SKCanvas canvas, SKRect rect, SKColor color, float slant = 80)
    {
        using var path = new SKPath();
        path.MoveTo(rect.Left + slant, rect.Top);
        path.LineTo(rect.Right, rect.Top);
        path.LineTo(rect.Right - slant, rect.Bottom);
        path.LineTo(rect.Left, rect.Bottom);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    public static void DrawCutCornerPanel(SKCanvas canvas, SKRect rect, float cut, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(rect.Left + cut, rect.Top);
        path.LineTo(rect.Right - cut, rect.Top);
        path.LineTo(rect.Right, rect.Top + cut);
        path.LineTo(rect.Right, rect.Bottom - cut);
        path.LineTo(rect.Right - cut, rect.Bottom);
        path.LineTo(rect.Left + cut, rect.Bottom);
        path.LineTo(rect.Left, rect.Bottom - cut);
        path.LineTo(rect.Left, rect.Top + cut);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    public static void DrawJaggedPaper(SKCanvas canvas, SKRect rect, SKColor fill, int seed)
    {
        var random = new Random(seed & int.MaxValue);
        using var path = new SKPath();
        path.MoveTo(rect.Left, rect.Top + random.Next(0, 12));
        for (var x = rect.Left; x <= rect.Right; x += 45)
            path.LineTo(Math.Min(x, rect.Right), rect.Top + random.Next(-9, 10));
        for (var y = rect.Top; y <= rect.Bottom; y += 42)
            path.LineTo(rect.Right + random.Next(-9, 10), Math.Min(y, rect.Bottom));
        for (var x = rect.Right; x >= rect.Left; x -= 45)
            path.LineTo(Math.Max(x, rect.Left), rect.Bottom + random.Next(-9, 10));
        for (var y = rect.Bottom; y >= rect.Top; y -= 42)
            path.LineTo(rect.Left + random.Next(-9, 10), Math.Max(y, rect.Top));
        path.Close();
        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    public static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in value ?? string.Empty) hash = hash * 31 + ch;
            return hash;
        }
    }

    private static void DrawInitials(SKCanvas canvas, string name, SKRect rect, PosterAvatarShape shape)
    {
        var initials = BuildInitials(name);
        var size = Math.Min(rect.Width, rect.Height) * .34f;
        DrawCenteredText(canvas, initials, rect.MidX, rect.MidY + size * .34f, size, SKColors.White, true, rect.Width - 10, BlackTypeface);
    }

    private static string BuildInitials(string value)
    {
        var words = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
        return string.Concat(words[0][0], words[^1][0]).ToUpperInvariant();
    }

    private static void DrawShape(SKCanvas canvas, SKRect rect, PosterAvatarShape shape, SKPaint paint)
    {
        switch (shape)
        {
            case PosterAvatarShape.Square:
                canvas.DrawRect(rect, paint);
                break;
            case PosterAvatarShape.RoundedSquare:
                canvas.DrawRoundRect(rect, rect.Width * .18f, rect.Height * .18f, paint);
                break;
            default:
                canvas.DrawOval(rect, paint);
                break;
        }
    }

    private static void ClipShape(SKCanvas canvas, SKRect rect, PosterAvatarShape shape)
    {
        switch (shape)
        {
            case PosterAvatarShape.Square:
                canvas.ClipRect(rect, antialias: true);
                break;
            case PosterAvatarShape.RoundedSquare:
                canvas.ClipRoundRect(new SKRoundRect(rect, rect.Width * .18f, rect.Height * .18f), antialias: true);
                break;
            default:
                using (var path = new SKPath())
                {
                    path.AddOval(rect);
                    canvas.ClipPath(path, antialias: true);
                }
                break;
        }
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

    private static string FitText(string value, SKPaint paint, float maxWidth)
    {
        if (paint.MeasureText(value) <= maxWidth) return value;
        const string ellipsis = "…";
        var builder = new StringBuilder(value);
        while (builder.Length > 1 && paint.MeasureText(builder + ellipsis) > maxWidth)
            builder.Length -= 1;
        return builder + ellipsis;
    }

    private static SKTypeface FindTypeface(SKFontStyle style)
    {
        var manager = SKFontManager.Default;
        var families = new[]
        {
            "Noto Sans",
            "Noto Sans Display",
            "DejaVu Sans",
            "Liberation Sans",
            "Arial"
        };
        foreach (var family in families)
        {
            var typeface = manager.MatchFamily(family, style);
            if (typeface is not null) return typeface;
        }
        return SKTypeface.Default;
    }
}
