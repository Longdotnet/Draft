using System.Text.RegularExpressions;
using SkiaSharp;

namespace VolleyDraft.Api.Services;

internal enum ZaloGreetingCardIcon
{
    None,
    Sun,
    Moon,
    Sparkle,
    Heart,
    Smile,
    Team
}

internal static class ZaloGreetingCardRenderQuality
{
    private static readonly (string Token, ZaloGreetingCardIcon Icon)[] KnownIcons =
    [
        ("☀️", ZaloGreetingCardIcon.Sun),
        ("☀", ZaloGreetingCardIcon.Sun),
        ("🌙", ZaloGreetingCardIcon.Moon),
        ("✨", ZaloGreetingCardIcon.Sparkle),
        ("🤍", ZaloGreetingCardIcon.Heart),
        ("❤️", ZaloGreetingCardIcon.Heart),
        ("❤", ZaloGreetingCardIcon.Heart),
        ("😌", ZaloGreetingCardIcon.Smile),
        ("🤝", ZaloGreetingCardIcon.Team)
    ];

    public static string PrepareText(string? value, out ZaloGreetingCardIcon icon)
    {
        var text = value?.Trim() ?? string.Empty;
        icon = ZaloGreetingCardIcon.None;
        foreach (var (token, candidate) in KnownIcons)
        {
            if (!text.Contains(token, StringComparison.Ordinal))
                continue;
            if (icon == ZaloGreetingCardIcon.None)
                icon = candidate;
            text = text.Replace(token, "", StringComparison.Ordinal);
        }

        // Never let a missing Linux emoji glyph become a tofu square. Unknown supplementary
        // glyphs are omitted from raster text; approved card icons are drawn as Skia vectors.
        text = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", string.Empty);
        text = text.Replace("️", string.Empty, StringComparison.Ordinal)
            .Replace("‍", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static void DrawBackground(SKCanvas canvas, SKBitmap background, int width, int height)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
#pragma warning disable CS0618
            FilterQuality = SKFilterQuality.High
#pragma warning restore CS0618
        };
        canvas.DrawBitmap(background, new SKRect(0, 0, width, height), paint);
    }

    public static void DrawIcon(
        SKCanvas canvas,
        ZaloGreetingCardIcon icon,
        float left,
        float centerY,
        float size,
        SKColor color)
    {
        if (icon == ZaloGreetingCardIcon.None)
            return;

        var radius = size * 0.28f;
        var cx = left + size * 0.5f;
        var cy = centerY;
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2f, size * 0.075f),
            StrokeCap = SKStrokeCap.Round
        };

        switch (icon)
        {
            case ZaloGreetingCardIcon.Sun:
                canvas.DrawCircle(cx, cy, radius, stroke);
                for (var i = 0; i < 8; i++)
                {
                    var angle = (float)(Math.PI * i / 4d);
                    var inner = radius * 1.35f;
                    var outer = radius * 1.85f;
                    canvas.DrawLine(
                        cx + MathF.Cos(angle) * inner,
                        cy + MathF.Sin(angle) * inner,
                        cx + MathF.Cos(angle) * outer,
                        cy + MathF.Sin(angle) * outer,
                        stroke);
                }
                break;
            case ZaloGreetingCardIcon.Moon:
                using (var path = new SKPath())
                {
                    path.MoveTo(cx + radius * 0.65f, cy - radius * 1.15f);
                    path.CubicTo(cx - radius * 1.2f, cy - radius, cx - radius * 1.2f, cy + radius, cx + radius * 0.65f, cy + radius * 1.15f);
                    path.CubicTo(cx - radius * 0.15f, cy + radius * 0.45f, cx - radius * 0.15f, cy - radius * 0.45f, cx + radius * 0.65f, cy - radius * 1.15f);
                    canvas.DrawPath(path, fill);
                }
                break;
            case ZaloGreetingCardIcon.Heart:
                using (var path = new SKPath())
                {
                    path.MoveTo(cx, cy + radius);
                    path.CubicTo(cx - radius * 1.45f, cy + radius * 0.15f, cx - radius * 1.15f, cy - radius, cx, cy - radius * 0.35f);
                    path.CubicTo(cx + radius * 1.15f, cy - radius, cx + radius * 1.45f, cy + radius * 0.15f, cx, cy + radius);
                    canvas.DrawPath(path, stroke);
                }
                break;
            case ZaloGreetingCardIcon.Smile:
                canvas.DrawCircle(cx, cy, radius * 1.12f, stroke);
                canvas.DrawCircle(cx - radius * 0.38f, cy - radius * 0.2f, size * 0.035f, fill);
                canvas.DrawCircle(cx + radius * 0.38f, cy - radius * 0.2f, size * 0.035f, fill);
                using (var path = new SKPath())
                {
                    path.MoveTo(cx - radius * 0.48f, cy + radius * 0.2f);
                    path.QuadTo(cx, cy + radius * 0.72f, cx + radius * 0.48f, cy + radius * 0.2f);
                    canvas.DrawPath(path, stroke);
                }
                break;
            case ZaloGreetingCardIcon.Team:
                canvas.DrawCircle(cx - radius * 0.42f, cy, radius * 0.55f, stroke);
                canvas.DrawCircle(cx + radius * 0.42f, cy, radius * 0.55f, stroke);
                canvas.DrawLine(cx - radius * 0.08f, cy - radius * 0.38f, cx + radius * 0.08f, cy + radius * 0.38f, stroke);
                break;
            default:
                canvas.DrawLine(cx - radius, cy, cx + radius, cy, stroke);
                canvas.DrawLine(cx, cy - radius, cx, cy + radius, stroke);
                canvas.DrawLine(cx - radius * 0.7f, cy - radius * 0.7f, cx + radius * 0.7f, cy + radius * 0.7f, stroke);
                canvas.DrawLine(cx + radius * 0.7f, cy - radius * 0.7f, cx - radius * 0.7f, cy + radius * 0.7f, stroke);
                break;
        }
    }
}
