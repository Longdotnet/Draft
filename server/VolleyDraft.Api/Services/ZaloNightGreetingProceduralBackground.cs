using SkiaSharp;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Full-resolution Night greeting artwork generated directly on the 1254x1254 card canvas.
/// This keeps the visual deck crisp without depending on aggressively-compressed embedded JPEGs.
/// </summary>
internal static class ZaloNightGreetingProceduralBackground
{
    private const int Width = ZaloNightGreetingCardRenderer.Width;
    private const int Height = ZaloNightGreetingCardRenderer.Height;

    public static SKBitmap Render(int backgroundId)
    {
        if (!ZaloNightGreetingBackgroundCatalog.IsActive(backgroundId))
            throw new ArgumentOutOfRangeException(nameof(backgroundId));

        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        DrawSky(canvas, backgroundId);
        DrawStars(canvas, backgroundId);

        switch (backgroundId)
        {
            case 1:
                DrawMoon(canvas, 1015, 132, 58);
                DrawTropicalHorizon(canvas, 860);
                DrawOutdoorCourt(canvas, horizonY: 872, city: false, warmLights: true);
                break;
            case 2:
                DrawMoon(canvas, 1038, 140, 52);
                DrawOceanHorizon(canvas, 914);
                DrawOutdoorCourt(canvas, horizonY: 914, city: false, warmLights: false);
                break;
            case 3:
                DrawIndoorHall(canvas);
                break;
            case 4:
                DrawMoon(canvas, 1064, 116, 48);
                DrawCityHorizon(canvas, 850);
                DrawOutdoorCourt(canvas, horizonY: 874, city: true, warmLights: true);
                break;
            case 5:
                DrawCrescent(canvas, 1018, 148, 74);
                DrawSoftVolleyballConstellations(canvas);
                DrawOceanHorizon(canvas, 902);
                DrawOutdoorCourt(canvas, horizonY: 902, city: false, warmLights: true);
                break;
        }

        DrawVignette(canvas);
        canvas.Flush();
        return bitmap;
    }

    private static void DrawSky(SKCanvas canvas, int id)
    {
        var palettes = new[]
        {
            new[] { new SKColor(5, 16, 67), new SKColor(24, 35, 111), new SKColor(89, 55, 143), new SKColor(31, 24, 74) },
            new[] { new SKColor(4, 18, 71), new SKColor(23, 35, 118), new SKColor(94, 59, 151), new SKColor(28, 28, 83) },
            new[] { new SKColor(17, 16, 35), new SKColor(34, 26, 54), new SKColor(79, 42, 54), new SKColor(22, 20, 36) },
            new[] { new SKColor(6, 17, 72), new SKColor(31, 35, 123), new SKColor(101, 58, 153), new SKColor(31, 25, 80) },
            new[] { new SKColor(7, 18, 73), new SKColor(32, 40, 126), new SKColor(94, 59, 153), new SKColor(26, 31, 86) }
        };
        var colors = palettes[id - 1];
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, Height),
            colors,
            new[] { 0f, .38f, .72f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), paint);

        using var glow = new SKPaint
        {
            Color = new SKColor(126, 73, 201, 30),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 90)
        };
        canvas.DrawCircle(230, 570, 270, glow);
    }

    private static void DrawStars(SKCanvas canvas, int id)
    {
        var random = new Random(9100 + id * 977);
        for (var i = 0; i < 170; i++)
        {
            var x = (float)(random.NextDouble() * Width);
            var y = (float)(random.NextDouble() * 690);
            var radius = i % 13 == 0 ? 2.1f : i % 5 == 0 ? 1.35f : .8f;
            var alpha = (byte)random.Next(80, 205);
            using var paint = new SKPaint
            {
                Color = new SKColor(240, 235, 255, alpha),
                IsAntialias = true
            };
            canvas.DrawCircle(x, y, radius, paint);
        }
    }

    private static void DrawMoon(SKCanvas canvas, float x, float y, float radius)
    {
        using var glow = new SKPaint
        {
            Color = new SKColor(206, 194, 255, 80),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 24)
        };
        canvas.DrawCircle(x, y, radius * 1.35f, glow);

        using var moon = new SKPaint { Color = new SKColor(236, 229, 255), IsAntialias = true };
        canvas.DrawCircle(x, y, radius, moon);
        using var crater = new SKPaint { Color = new SKColor(174, 163, 220, 72), IsAntialias = true };
        canvas.DrawCircle(x - radius * .25f, y - radius * .16f, radius * .16f, crater);
        canvas.DrawCircle(x + radius * .28f, y + radius * .18f, radius * .13f, crater);
        canvas.DrawCircle(x + radius * .05f, y - radius * .38f, radius * .09f, crater);
    }

    private static void DrawCrescent(SKCanvas canvas, float x, float y, float radius)
    {
        using var glow = new SKPaint
        {
            Color = new SKColor(235, 210, 255, 75),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22)
        };
        canvas.DrawCircle(x, y, radius * 1.18f, glow);
        using var moon = new SKPaint { Color = new SKColor(241, 220, 255), IsAntialias = true };
        canvas.DrawCircle(x, y, radius, moon);
        using var cutout = new SKPaint { Color = new SKColor(9, 21, 76), IsAntialias = true };
        canvas.DrawCircle(x - radius * .34f, y - radius * .2f, radius * .86f, cutout);
    }

    private static void DrawTropicalHorizon(SKCanvas canvas, float y)
    {
        using var haze = new SKPaint { Color = new SKColor(11, 23, 65, 210), IsAntialias = true };
        canvas.DrawRect(new SKRect(0, y, Width, Height), haze);
        using var palm = new SKPaint { Color = new SKColor(6, 20, 39, 240), IsAntialias = true, StrokeWidth = 9, StrokeCap = SKStrokeCap.Round };
        for (var i = 0; i < 8; i++)
        {
            var x = 930 + i * 48;
            var top = y - 70 - (i % 3) * 32;
            canvas.DrawLine(x, Height, x - 8, top, palm);
            for (var j = -2; j <= 2; j++)
                canvas.DrawLine(x - 8, top, x - 8 + j * 38, top - 15 - Math.Abs(j) * 9, palm);
        }
    }

    private static void DrawOceanHorizon(SKCanvas canvas, float y)
    {
        using var waterShader = SKShader.CreateLinearGradient(
            new SKPoint(0, y),
            new SKPoint(0, Height),
            new[] { new SKColor(17, 31, 82), new SKColor(8, 17, 48) },
            null,
            SKShaderTileMode.Clamp);
        using var water = new SKPaint { Shader = waterShader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, y, Width, Height), water);
        using var shimmer = new SKPaint { Color = new SKColor(205, 179, 255, 42), StrokeWidth = 2, IsAntialias = true };
        for (var i = 0; i < 16; i++)
        {
            var yy = y + 20 + i * 13;
            canvas.DrawLine(760 - i * 8, yy, 1050 + i * 5, yy, shimmer);
        }
    }

    private static void DrawCityHorizon(SKCanvas canvas, float y)
    {
        using var ground = new SKPaint { Color = new SKColor(12, 17, 48, 235), IsAntialias = true };
        canvas.DrawRect(new SKRect(0, y, Width, Height), ground);
        var random = new Random(4404);
        for (var i = 0; i < 18; i++)
        {
            var w = random.Next(28, 64);
            var h = random.Next(55, 210);
            var x = 560 + i * 42;
            using var building = new SKPaint { Color = new SKColor(16, 20, 62, 245), IsAntialias = true };
            canvas.DrawRect(new SKRect(x, y - h, x + w, y), building);
            using var window = new SKPaint { Color = new SKColor(245, 196, 121, (byte)random.Next(80, 190)), IsAntialias = true };
            for (var yy = y - h + 18; yy < y - 12; yy += 25)
                for (var xx = x + 9; xx < x + w - 5; xx += 18)
                    if (random.NextDouble() > .42)
                        canvas.DrawCircle(xx, yy, 2.3f, window);
        }
    }

    private static void DrawIndoorHall(SKCanvas canvas)
    {
        using var wallShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(Width, Height),
            new[] { new SKColor(19, 18, 34), new SKColor(72, 42, 50), new SKColor(14, 19, 43) },
            null,
            SKShaderTileMode.Clamp);
        using var wall = new SKPaint { Shader = wallShader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, 0, Width, 770), wall);

        using var floorShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 760),
            new SKPoint(0, Height),
            new[] { new SKColor(129, 72, 48), new SKColor(55, 32, 37), new SKColor(18, 19, 38) },
            null,
            SKShaderTileMode.Clamp);
        using var floor = new SKPaint { Shader = floorShader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, 760, Width, Height), floor);

        using var lampGlow = new SKPaint
        {
            Color = new SKColor(255, 180, 100, 70),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 34)
        };
        using var lamp = new SKPaint { Color = new SKColor(255, 196, 123), IsAntialias = true };
        for (var x = 100; x < 850; x += 180)
        {
            canvas.DrawCircle(x, 820, 28, lampGlow);
            canvas.DrawCircle(x, 820, 5, lamp);
        }
        DrawCourtGeometry(canvas, floorTop: 760, accent: new SKColor(244, 206, 178, 165));
        DrawNet(canvas, postX: 850, topY: 520, bottomY: 940, rightEdge: 1254);
        DrawBall(canvas, 1032, 1018, 38);
    }

    private static void DrawOutdoorCourt(SKCanvas canvas, float horizonY, bool city, bool warmLights)
    {
        using var courtShader = SKShader.CreateLinearGradient(
            new SKPoint(0, horizonY),
            new SKPoint(0, Height),
            new[] { new SKColor(39, 32, 79, 235), new SKColor(18, 19, 51, 250) },
            null,
            SKShaderTileMode.Clamp);
        using var court = new SKPaint { Shader = courtShader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, horizonY, Width, Height), court);
        DrawCourtGeometry(canvas, horizonY, city ? new SKColor(226, 205, 255, 170) : new SKColor(224, 213, 255, 155));
        DrawNet(canvas, postX: city ? 910 : 875, topY: city ? 455 : 505, bottomY: 1010, rightEdge: 1254);
        DrawBall(canvas, city ? 1062 : 1034, 1080, 36);

        if (warmLights)
        {
            using var wire = new SKPaint { Color = new SKColor(46, 38, 66, 220), StrokeWidth = 2.5f, IsAntialias = true };
            canvas.DrawLine(890, 575, 1254, 410, wire);
            for (var i = 0; i < 7; i++)
            {
                var x = 915 + i * 54;
                var y = 565 - i * 25;
                using var glow = new SKPaint
                {
                    Color = new SKColor(255, 184, 93, 75),
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 13)
                };
                using var bulb = new SKPaint { Color = new SKColor(255, 210, 132), IsAntialias = true };
                canvas.DrawCircle(x, y, 13, glow);
                canvas.DrawCircle(x, y, 5, bulb);
            }
        }
    }

    private static void DrawCourtGeometry(SKCanvas canvas, float floorTop, SKColor accent)
    {
        using var line = new SKPaint { Color = accent, StrokeWidth = 3.5f, IsAntialias = true };
        canvas.DrawLine(95, Height - 78, Width - 36, Height - 78, line);
        canvas.DrawLine(245, floorTop + 50, 95, Height - 78, line);
        canvas.DrawLine(1005, floorTop + 45, Width - 36, Height - 78, line);
        canvas.DrawLine(610, floorTop + 70, 560, Height - 78, line);
    }

    private static void DrawNet(SKCanvas canvas, float postX, float topY, float bottomY, float rightEdge)
    {
        using var postShadow = new SKPaint { Color = new SKColor(6, 10, 35, 235), StrokeWidth = 24, IsAntialias = true };
        canvas.DrawLine(postX, topY - 18, postX, bottomY, postShadow);
        using var post = new SKPaint { Color = new SKColor(64, 60, 113), StrokeWidth = 12, IsAntialias = true };
        canvas.DrawLine(postX, topY - 18, postX, bottomY, post);

        using var mesh = new SKPaint { Color = new SKColor(207, 202, 238, 145), StrokeWidth = 1.6f, IsAntialias = true };
        using var tape = new SKPaint { Color = new SKColor(239, 232, 255, 220), StrokeWidth = 8, IsAntialias = true };
        canvas.DrawLine(postX, topY, rightEdge, topY - 125, tape);
        canvas.DrawLine(postX, topY + 210, rightEdge, topY + 85, tape);
        for (var i = 0; i <= 12; i++)
        {
            var t = i / 12f;
            var x = postX + (rightEdge - postX) * t;
            var top = topY - 125 * t;
            canvas.DrawLine(x, top, x, top + 210, mesh);
        }
        for (var i = 1; i < 8; i++)
        {
            var y = topY + i * 26;
            canvas.DrawLine(postX, y, rightEdge, y - 125, mesh);
        }
    }

    private static void DrawBall(SKCanvas canvas, float x, float y, float radius)
    {
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 95),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14)
        };
        canvas.DrawOval(new SKRect(x - radius * 1.1f, y + radius * .65f, x + radius * 1.3f, y + radius * 1.15f), shadow);
        using var basePaint = new SKPaint { Color = new SKColor(235, 190, 75), IsAntialias = true };
        canvas.DrawCircle(x, y, radius, basePaint);
        using var stripe = new SKPaint { Color = new SKColor(53, 55, 125), StrokeWidth = radius * .42f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        canvas.DrawArc(new SKRect(x - radius, y - radius, x + radius, y + radius), -62, 122, false, stripe);
        canvas.DrawArc(new SKRect(x - radius * .78f, y - radius * .78f, x + radius * .78f, y + radius * .78f), 95, 130, false, stripe);
    }

    private static void DrawSoftVolleyballConstellations(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = new SKColor(202, 185, 255, 24), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var (x, y, r) in new[] { (122f, 126f, 66f), (170f, 430f, 42f), (780f, 720f, 54f) })
        {
            canvas.DrawCircle(x, y, r, paint);
            canvas.DrawArc(new SKRect(x - r, y - r, x + r, y + r), 15, 110, false, paint);
            canvas.DrawArc(new SKRect(x - r * .8f, y - r * .8f, x + r * .8f, y + r * .8f), 165, 120, false, paint);
        }
    }

    private static void DrawVignette(SKCanvas canvas)
    {
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(Width * .53f, Height * .48f),
            Width * .78f,
            new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 15), new SKColor(0, 0, 0, 105) },
            new[] { 0f, .65f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0, 0, Width, Height), paint);
    }
}