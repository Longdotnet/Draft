using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloSocialMediaAssetService(
    VolleyDraftDbContext db,
    IConfiguration configuration)
{
    public async Task<string?> GetOrCreateGreetingCardUrlAsync(
        string adminUserId,
        ZaloDailyGreetingKind kind,
        ZaloDailyGreetingMood mood,
        CancellationToken cancellationToken = default)
    {
        var publicOrigin = ResolvePublicOrigin();
        if (string.IsNullOrWhiteSpace(publicOrigin) || string.IsNullOrWhiteSpace(adminUserId))
            return null;

        var fileName = $"social-greeting-{kind.ToString().ToLowerInvariant()}-{mood.ToString().ToLowerInvariant()}-v1.png";
        var existing = await db.ZaloBotImageAssets
            .AsNoTracking()
            .Where(item => item.AdminUserId == adminUserId && item.FileName == fileName)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
            return BuildPublicUrl(publicOrigin, existing);

        var data = ZaloSocialGreetingCardRenderer.Render(kind, mood);
        var asset = new ZaloBotImageAsset
        {
            AdminUserId = adminUserId,
            FileName = fileName,
            ContentType = "image/png",
            Size = data.LongLength,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotImageAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return BuildPublicUrl(publicOrigin, asset.Id);
    }

    private string? ResolvePublicOrigin()
    {
        var configured = configuration["Public:BaseUrl"]?.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
            return configuredUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

        // Render already needs a public Zalo webhook URL. Reuse its origin if a
        // separate Public:BaseUrl was not configured, so social images do not add a
        // new production environment variable requirement.
        var webhook = configuration["Zalo:WebhookUrl"]?.Trim();
        return Uri.TryCreate(webhook, UriKind.Absolute, out var webhookUri)
            ? webhookUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : null;
    }

    private static string BuildPublicUrl(string origin, string assetId) =>
        $"{origin.TrimEnd('/')}/api/public/bot-images/{Uri.EscapeDataString(assetId)}";
}

internal static class ZaloSocialGreetingCardRenderer
{
    public const int Width = 1080;
    public const int Height = 1080;

    private static readonly SKTypeface RegularTypeface = FindTypeface(SKFontStyle.Normal);
    private static readonly SKTypeface BoldTypeface = FindTypeface(SKFontStyle.Bold);

    public static byte[] Render(ZaloDailyGreetingKind kind, ZaloDailyGreetingMood mood)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create social greeting canvas.");
        var canvas = surface.Canvas;
        DrawBackground(canvas, kind, mood);
        DrawCourtLines(canvas, kind);
        DrawOrb(canvas, kind);
        DrawCopy(canvas, kind, mood);
        DrawFooter(canvas);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    private static void DrawBackground(SKCanvas canvas, ZaloDailyGreetingKind kind, ZaloDailyGreetingMood mood)
    {
        var (a, b, c) = kind == ZaloDailyGreetingKind.Morning
            ? mood switch
            {
                ZaloDailyGreetingMood.PlayfulRomantic =>
                    (new SKColor(255, 236, 219), new SKColor(255, 199, 174), new SKColor(255, 168, 133)),
                ZaloDailyGreetingMood.MenlySupportive =>
                    (new SKColor(225, 246, 255), new SKColor(145, 216, 237), new SKColor(73, 159, 190)),
                _ =>
                    (new SKColor(255, 248, 219), new SKColor(255, 214, 147), new SKColor(247, 174, 96))
            }
            : mood switch
            {
                ZaloDailyGreetingMood.PlayfulRomantic =>
                    (new SKColor(31, 29, 63), new SKColor(70, 48, 103), new SKColor(112, 72, 117)),
                ZaloDailyGreetingMood.MenlySupportive =>
                    (new SKColor(8, 19, 38), new SKColor(19, 47, 75), new SKColor(30, 76, 103)),
                _ =>
                    (new SKColor(12, 18, 38), new SKColor(31, 39, 78), new SKColor(54, 54, 102))
            };

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Width, Height),
                [a, b, c],
                [0f, .55f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), paint);
    }

    private static void DrawCourtLines(SKCanvas canvas, ZaloDailyGreetingKind kind)
    {
        using var line = new SKPaint
        {
            Color = kind == ZaloDailyGreetingKind.Morning
                ? new SKColor(255, 255, 255, 58)
                : new SKColor(255, 255, 255, 34),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            IsAntialias = true
        };
        var rect = new SKRect(78, 120, Width - 78, Height - 115);
        canvas.DrawRoundRect(rect, 38, 38, line);
        canvas.DrawLine(Width / 2f, rect.Top, Width / 2f, rect.Bottom, line);
        canvas.DrawLine(rect.Left, Height / 2f, rect.Right, Height / 2f, line);

        for (var x = 150; x < Width; x += 175)
            canvas.DrawLine(x, 0, x - 150, Height, line);
    }

    private static void DrawOrb(SKCanvas canvas, ZaloDailyGreetingKind kind)
    {
        var center = kind == ZaloDailyGreetingKind.Morning
            ? new SKPoint(835, 250)
            : new SKPoint(820, 245);
        using var glow = new SKPaint
        {
            Color = kind == ZaloDailyGreetingKind.Morning
                ? new SKColor(255, 255, 220, 90)
                : new SKColor(224, 231, 255, 55),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 45)
        };
        canvas.DrawCircle(center.X, center.Y, 125, glow);

        using var orb = new SKPaint
        {
            Color = kind == ZaloDailyGreetingKind.Morning
                ? new SKColor(255, 250, 214, 235)
                : new SKColor(231, 236, 255, 220),
            IsAntialias = true
        };
        canvas.DrawCircle(center.X, center.Y, kind == ZaloDailyGreetingKind.Morning ? 92 : 76, orb);

        if (kind == ZaloDailyGreetingKind.Night)
        {
            using var cutout = new SKPaint { Color = new SKColor(45, 42, 88), IsAntialias = true };
            canvas.DrawCircle(center.X + 34, center.Y - 22, 69, cutout);
        }
    }

    private static void DrawCopy(SKCanvas canvas, ZaloDailyGreetingKind kind, ZaloDailyGreetingMood mood)
    {
        var light = kind == ZaloDailyGreetingKind.Morning;
        var ink = light ? new SKColor(58, 47, 39) : new SKColor(244, 247, 255);
        var muted = light ? new SKColor(91, 72, 58, 205) : new SKColor(207, 216, 240, 205);
        var accent = light ? new SKColor(115, 74, 48) : new SKColor(205, 214, 255);

        var eyebrow = kind == ZaloDailyGreetingKind.Morning ? "VOLLEY GROUP  /  NEW DAY" : "VOLLEY GROUP  /  END OF DAY";
        var headline = kind == ZaloDailyGreetingKind.Morning ? "GOOD MORNING" : "NGỦ NGOAN NHA";
        var subtitle = (kind, mood) switch
        {
            (ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.PlayfulRomantic) => "CỨ SỐNG XỊN, DUYÊN TÍNH SAU",
            (ZaloDailyGreetingKind.Morning, ZaloDailyGreetingMood.MenlySupportive) => "BÌNH TĨNH • GỌN GÀNG • CHIẾN THÔI",
            (ZaloDailyGreetingKind.Morning, _) => "HÔM NAY CỨ SỐNG VUI TRƯỚC ĐÃ",
            (ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.PlayfulRomantic) => "NGƯỜI THƯƠNG TỪ TỪ • GIẤC NGỦ TRƯỚC",
            (ZaloDailyGreetingKind.Night, ZaloDailyGreetingMood.MenlySupportive) => "MAI CÒN VIỆC MAI MÌNH XỬ",
            _ => "MAI MÌNH LẠI VUI TIẾP"
        };

        DrawText(canvas, eyebrow, 90, 170, 25, accent, true, 650);
        DrawText(canvas, headline, 88, 520, kind == ZaloDailyGreetingKind.Morning ? 91 : 82, ink, true, 900);
        DrawText(canvas, subtitle, 92, 605, 30, muted, true, 855);

        using var rail = new SKPaint { Color = accent, StrokeWidth = 5, IsAntialias = true };
        canvas.DrawLine(92, 655, 360, 655, rail);

        var small = kind == ZaloDailyGreetingKind.Morning
            ? "Ăn sáng tử tế • giữ mood đẹp • tối còn sức chơi"
            : "Để chuyện chưa vui lại hôm nay • nghỉ cho thật tử tế";
        DrawText(canvas, small, 92, 730, 25, muted, false, 820);
    }

    private static void DrawFooter(SKCanvas canvas)
    {
        using var pill = new SKPaint { Color = new SKColor(255, 255, 255, 28), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(86, 895, 360, 957), 31, 31, pill);
        DrawText(canvas, "NPC • VOLLEY DRAFT", 116, 936, 20, new SKColor(255, 255, 255, 215), true, 225);
    }

    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        float fontSize,
        SKColor color,
        bool bold,
        float maxWidth)
    {
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = fontSize,
            Typeface = bold ? BoldTypeface : RegularTypeface,
            IsAntialias = true,
            SubpixelText = true
        };
        var fitted = text;
        while (fitted.Length > 1 && paint.MeasureText(fitted) > maxWidth)
            fitted = fitted[..^1].TrimEnd();
        if (fitted.Length < text.Length) fitted += "…";
        canvas.DrawText(fitted, x, baseline, paint);
    }

    private static SKTypeface FindTypeface(SKFontStyle style) =>
        SKTypeface.FromFamilyName("Noto Sans", style) ??
        SKTypeface.FromFamilyName("DejaVu Sans", style) ??
        SKTypeface.FromFamilyName("Arial", style) ??
        SKTypeface.FromFamilyName("Segoe UI", style) ??
        SKTypeface.Default;
}
