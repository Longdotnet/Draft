using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace VolleyDraft.Api.Services;

public sealed record Npc11CharacterProfile(
    string UserId,
    string DisplayName,
    string Rarity,
    string Archetype,
    string SpecialSkill,
    string PassiveSkill,
    string Quote,
    int Defense,
    int Spirit,
    int Support,
    int Reflex,
    int Charm,
    int Level,
    int Seed,
    string Style);

public static class Npc11CharacterEngine
{
    public const string Season = "volleyverse-s1";

    private static readonly string[] Archetypes =
    [
        "LIBERO TÂM LINH",
        "SETTER TIÊN TRI",
        "ACE HỦY DIỆT",
        "BLOCKER THẦN SẦU",
        "SERVER SẤM SÉT",
        "CỨU BÓNG THẦN THÁNH",
        "BENCH LEGEND",
        "CỔ ĐỘNG VIÊN TỐI THƯỢNG"
    ];

    private static readonly string[] Specials =
    [
        "CẦU XIN CỨU BÓNG",
        "BƯỚC CHÂN ẢO ẢNH",
        "CHUYỀN HAI THIÊN KHẢI",
        "ĐẬP BÓNG ĐỊNH MỆNH",
        "TƯỜNG THÀNH PHẢN XẠ",
        "AURA GÁNH TEAM",
        "LĂN XẢ KHÔNG PHANH",
        "GỌI HỒN TRÁI BÓNG"
    ];

    private static readonly string[] Passives =
    [
        "NƯỚC MẮT ĐỒNG ĐỘI",
        "TINH THẦN BẤT DIỆT",
        "MẮT ƯNG BẮT BÓNG",
        "NHÂN PHẨM CUỐI SET",
        "TIẾNG HÉT TĂNG BUFF",
        "ĐỨNG YÊN CŨNG CÓ AURA",
        "CÀNG CĂNG CÀNG TỈNH",
        "BÓNG TÌM TỚI TẬN NƠI"
    ];

    private static readonly string[] Quotes =
    [
        "Team gục ngã, tui vẫn cứu được.",
        "Bóng chưa chạm sàn thì chưa hết chuyện.",
        "Không cần hoàn hảo, chỉ cần đừng rớt bóng.",
        "Một pha cứu bóng đẹp hơn ngàn lời giải thích.",
        "Căng thì căng, tay vẫn phải mềm.",
        "Đánh có thể lỗi, tinh thần không được lỗi.",
        "Tui không gánh team. Team tự đứng lên thôi.",
        "Đừng nhìn chỉ số. Nhìn pha bóng tiếp theo."
    ];

    public static Npc11CharacterProfile Create(string userId, string displayName, string? requestedStyle = null)
    {
        var stableId = string.IsNullOrWhiteSpace(userId) ? displayName.Trim() : userId.Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Season}|{stableId}"));
        var seed = BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        var random = new Random(seed);
        var rarityRoll = random.Next(1000);
        var rarity = rarityRoll switch
        {
            < 10 => "MYTHIC",
            < 80 => "LEGENDARY",
            < 250 => "EPIC",
            < 550 => "RARE",
            _ => "COMMON"
        };
        var rarityBonus = rarity switch
        {
            "MYTHIC" => 10,
            "LEGENDARY" => 7,
            "EPIC" => 4,
            "RARE" => 2,
            _ => 0
        };

        int Stat() => Math.Clamp(55 + random.Next(0, 41) + rarityBonus, 55, 99);
        var style = NormalizeStyle(requestedStyle);
        return new Npc11CharacterProfile(
            stableId,
            string.IsNullOrWhiteSpace(displayName) ? "VOLLEYVERSE PLAYER" : displayName.Trim(),
            rarity,
            Archetypes[random.Next(Archetypes.Length)],
            Specials[random.Next(Specials.Length)],
            Passives[random.Next(Passives.Length)],
            Quotes[random.Next(Quotes.Length)],
            Stat(),
            Stat(),
            Stat(),
            Stat(),
            Stat(),
            1 + random.Next(1, 99),
            seed,
            style);
    }

    public static string NormalizeStyle(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "cyber" or "cyberpunk" => "cyber",
            "cute" or "kawaii" => "cute",
            "dark" or "darker" => "dark",
            "anime" => "anime",
            "real" or "realistic" or "photo" => "realistic",
            "legend" or "legendary" => "legendary",
            _ => "classic"
        };
    }
}

public static class Npc11CardRenderer
{
    public const int Width = 1080;
    public const int Height = 1600;

    private static readonly SKTypeface Regular = FindTypeface(SKFontStyle.Normal);
    private static readonly SKTypeface Bold = FindTypeface(SKFontStyle.Bold);

    public static byte[] Render(Npc11CharacterProfile profile, byte[]? heroArt)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(3, 18, 14));

        using (var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Width, Height),
                [new SKColor(7, 40, 31), new SKColor(3, 14, 18), new SKColor(18, 29, 13)],
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(new SKRect(0, 0, Width, Height), background);
        }

        DrawHero(canvas, heroArt);
        DrawHeroShade(canvas);
        DrawFrame(canvas, profile.Rarity);
        DrawHeader(canvas, profile);
        DrawStats(canvas, profile);
        DrawSkillPanels(canvas, profile);
        DrawFooter(canvas, profile);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void DrawHero(SKCanvas canvas, byte[]? art)
    {
        var rect = new SKRect(28, 28, Width - 28, 1040);
        if (art is { Length: > 0 })
        {
            using var hero = SKBitmap.Decode(art);
            if (hero is not null && hero.Width > 0 && hero.Height > 0)
            {
                var src = Cover(hero.Width, hero.Height, rect.Width, rect.Height);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(hero, src, rect, paint);
                return;
            }
        }

        using var placeholder = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Bottom),
                [new SKColor(21, 117, 74), new SKColor(5, 45, 37)],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, placeholder);
    }

    private static void DrawHeroShade(SKCanvas canvas)
    {
        using var shade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(20, 0),
                new SKPoint(700, 0),
                [new SKColor(0, 0, 0, 225), new SKColor(0, 0, 0, 105), new SKColor(0, 0, 0, 0)],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(28, 28, Width - 28, 1040), shade);

        using var bottom = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 760),
                new SKPoint(0, 1045),
                [new SKColor(0, 0, 0, 0), new SKColor(2, 14, 10, 230)],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(28, 700, Width - 28, 1045), bottom);
    }

    private static void DrawFrame(SKCanvas canvas, string rarity)
    {
        var accent = RarityColor(rarity);
        using var outer = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = new SKColor(213, 177, 76), IsAntialias = true };
        using var inner = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = accent, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(14, 14, Width - 14, Height - 14), 32, 32, outer);
        canvas.DrawRoundRect(new SKRect(28, 28, Width - 28, Height - 28), 24, 24, inner);
        canvas.DrawLine(40, 1050, Width - 40, 1050, outer);
    }

    private static void DrawHeader(SKCanvas canvas, Npc11CharacterProfile profile)
    {
        var accent = RarityColor(profile.Rarity);
        DrawText(canvas, "VOLLEYVERSE", 820, 58, 24, new SKColor(232, 238, 226), false);
        DrawBadge(canvas, profile.Rarity, 55, 62, 255, 112, accent);
        DrawText(canvas, "#11", 905, 130, 56, SKColors.White, true);

        var display = profile.DisplayName.ToUpper(new CultureInfo("vi-VN"));
        DrawFittedText(canvas, display, 60, 310, 470, 78, 42, SKColors.White, true);
        DrawBadge(canvas, profile.Archetype, 60, 402, 430, 62, new SKColor(122, 201, 56));
        DrawWrappedText(canvas, $"“{profile.Quote}”", new SKRect(62, 492, 445, 610), 31, new SKColor(238, 242, 231), false, 3);
    }

    private static void DrawStats(SKCanvas canvas, Npc11CharacterProfile profile)
    {
        var labels = new[] { "PHÒNG THỦ", "TINH THẦN", "CỔ VŨ", "PHẢN XẠ", "ĐỘ ĐÁNG THƯƠNG" };
        var values = new[] { profile.Defense, profile.Spirit, profile.Support, profile.Reflex, profile.Charm };
        var y = 665f;
        for (var i = 0; i < labels.Length; i += 1)
        {
            DrawText(canvas, labels[i], 66, y, 24, SKColors.White, true);
            DrawText(canvas, values[i].ToString(CultureInfo.InvariantCulture), 372, y, 35, new SKColor(153, 221, 68), true, SKTextAlign.Right);
            using var track = new SKPaint { Color = new SKColor(25, 62, 47, 220), IsAntialias = true };
            using var fill = new SKPaint { Color = new SKColor(151, 220, 61), IsAntialias = true };
            var trackRect = new SKRect(66, y + 14, 354, y + 28);
            canvas.DrawRoundRect(trackRect, 7, 7, track);
            canvas.DrawRoundRect(new SKRect(trackRect.Left, trackRect.Top, trackRect.Left + trackRect.Width * values[i] / 100f, trackRect.Bottom), 7, 7, fill);
            y += 72;
        }
    }

    private static void DrawSkillPanels(SKCanvas canvas, Npc11CharacterProfile profile)
    {
        var left = new SKRect(45, 1080, 535, 1328);
        var right = new SKRect(548, 1080, 1035, 1328);
        DrawPanel(canvas, left);
        DrawPanel(canvas, right);
        DrawText(canvas, "KỸ NĂNG ĐẶC BIỆT", left.Left + 22, left.Top + 42, 22, new SKColor(165, 223, 72), true);
        DrawFittedText(canvas, profile.SpecialSkill, left.Left + 22, left.Top + 98, left.Width - 44, 37, 25, SKColors.White, true);
        DrawWrappedText(canvas, SkillDescription(profile.SpecialSkill), new SKRect(left.Left + 22, left.Top + 125, left.Right - 22, left.Bottom - 18), 21, new SKColor(224, 231, 218), false, 4);

        DrawText(canvas, "PASSIVE SKILL", right.Left + 22, right.Top + 42, 22, new SKColor(165, 223, 72), true);
        DrawFittedText(canvas, profile.PassiveSkill, right.Left + 22, right.Top + 98, right.Width - 44, 34, 24, SKColors.White, true);
        DrawWrappedText(canvas, PassiveDescription(profile.PassiveSkill), new SKRect(right.Left + 22, right.Top + 125, right.Right - 22, right.Bottom - 18), 21, new SKColor(224, 231, 218), false, 4);
    }

    private static void DrawFooter(SKCanvas canvas, Npc11CharacterProfile profile)
    {
        DrawPanel(canvas, new SKRect(45, 1345, 330, 1540));
        DrawText(canvas, "LV.", 72, 1395, 28, new SKColor(226, 230, 211), true);
        DrawText(canvas, profile.Level.ToString(CultureInfo.InvariantCulture), 72, 1492, 92, new SKColor(178, 229, 76), true);
        DrawText(canvas, Npc11CharacterEngine.Season.ToUpperInvariant(), 72, 1522, 16, new SKColor(146, 164, 148), false);

        var radarRect = new SKRect(355, 1350, 700, 1535);
        DrawRadar(canvas, radarRect, [profile.Defense, profile.Spirit, profile.Support, profile.Reflex, profile.Charm]);

        DrawPanel(canvas, new SKRect(720, 1345, 1035, 1540));
        DrawText(canvas, "CARD ID", 746, 1390, 18, new SKColor(159, 177, 160), true);
        DrawText(canvas, StableCardId(profile), 746, 1432, 22, SKColors.White, true);
        DrawWrappedText(canvas, "AI ART READY • FALLBACK SAFE • OBJECT REFERENCE", new SKRect(746, 1460, 1005, 1525), 16, new SKColor(177, 211, 166), false, 3);
    }

    private static void DrawPanel(SKCanvas canvas, SKRect rect)
    {
        using var fill = new SKPaint { Color = new SKColor(3, 28, 22, 225), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(175, 145, 58), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRoundRect(rect, 14, 14, fill);
        canvas.DrawRoundRect(rect, 14, 14, stroke);
    }

    private static void DrawBadge(SKCanvas canvas, string text, float x, float y, float width, float height, SKColor color)
    {
        using var fill = new SKPaint { Color = new SKColor(color.Red, color.Green, color.Blue, 215), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(225, 194, 94), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        var rect = new SKRect(x, y, x + width, y + height);
        canvas.DrawRoundRect(rect, 13, 13, fill);
        canvas.DrawRoundRect(rect, 13, 13, stroke);
        DrawFittedText(canvas, text, x + 18, y + height * .68f, width - 36, height * .47f, 17, SKColors.White, true);
    }

    private static void DrawRadar(SKCanvas canvas, SKRect rect, IReadOnlyList<int> values)
    {
        var center = new SKPoint(rect.MidX, rect.MidY + 5);
        var radius = Math.Min(rect.Width, rect.Height) * .42f;
        var count = 5;
        using var grid = new SKPaint { Color = new SKColor(107, 160, 89, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        for (var ring = 1; ring <= 4; ring += 1)
        {
            using var path = new SKPath();
            for (var i = 0; i < count; i += 1)
            {
                var point = Polar(center, radius * ring / 4f, i, count);
                if (i == 0) path.MoveTo(point); else path.LineTo(point);
            }
            path.Close();
            canvas.DrawPath(path, grid);
        }
        using var data = new SKPath();
        for (var i = 0; i < count; i += 1)
        {
            var point = Polar(center, radius * values[i] / 100f, i, count);
            if (i == 0) data.MoveTo(point); else data.LineTo(point);
        }
        data.Close();
        using var dataFill = new SKPaint { Color = new SKColor(152, 222, 65, 95), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var dataLine = new SKPaint { Color = new SKColor(184, 236, 91), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        canvas.DrawPath(data, dataFill);
        canvas.DrawPath(data, dataLine);
    }

    private static SKPoint Polar(SKPoint center, float radius, int index, int count)
    {
        var angle = -MathF.PI / 2f + index * MathF.PI * 2f / count;
        return new SKPoint(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
    }

    private static string StableCardId(Npc11CharacterProfile profile)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Npc11CharacterEngine.Season}|{profile.UserId}")));
        return $"VV-{hash[..8]}";
    }

    private static string SkillDescription(string skill) => skill switch
    {
        "CẦU XIN CỨU BÓNG" => "Khi tưởng như hết cơ hội, nhân vật kích hoạt pha cứu bóng không tưởng và buff tinh thần đồng đội.",
        "AURA GÁNH TEAM" => "Đồng đội đứng gần được tăng độ tự tin. Hiệu ứng mạnh nhất ở những điểm số căng thẳng.",
        _ => "Kỹ năng bùng nổ tạo lợi thế trong pha bóng quyết định và tăng nhịp chiến đấu toàn đội."
    };

    private static string PassiveDescription(string passive) => passive switch
    {
        "NƯỚC MẮT ĐỒNG ĐỘI" => "Mỗi lần team gặp khó, tinh thần toàn đội được cộng thêm một lớp lì lợm trong 10 giây.",
        _ => "Nội tại luôn bật, giúp nhân vật giữ phong độ ổn định và tạo hiệu ứng tinh thần cho cả đội."
    };

    private static SKRect Cover(int sourceWidth, int sourceHeight, float targetWidth, float targetHeight)
    {
        var sourceAspect = sourceWidth / (float)sourceHeight;
        var targetAspect = targetWidth / targetHeight;
        if (sourceAspect > targetAspect)
        {
            var width = sourceHeight * targetAspect;
            var left = (sourceWidth - width) / 2f;
            return new SKRect(left, 0, left + width, sourceHeight);
        }
        var height = sourceWidth / targetAspect;
        var top = (sourceHeight - height) / 2f;
        return new SKRect(0, top, sourceWidth, top + height);
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool bold, SKTextAlign align = SKTextAlign.Left)
    {
        using var paint = new SKPaint { Typeface = bold ? Bold : Regular, TextSize = size, Color = color, IsAntialias = true, TextAlign = align };
        canvas.DrawText(text, x, y, paint);
    }

    private static void DrawFittedText(SKCanvas canvas, string text, float x, float y, float maxWidth, float preferred, float minimum, SKColor color, bool bold)
    {
        var size = preferred;
        using var paint = new SKPaint { Typeface = bold ? Bold : Regular, TextSize = size, Color = color, IsAntialias = true };
        while (size > minimum && paint.MeasureText(text) > maxWidth)
        {
            size -= 1;
            paint.TextSize = size;
        }
        canvas.DrawText(text, x, y, paint);
    }

    private static void DrawWrappedText(SKCanvas canvas, string text, SKRect rect, float size, SKColor color, bool bold, int maxLines)
    {
        using var paint = new SKPaint { Typeface = bold ? Bold : Regular, TextSize = size, Color = color, IsAntialias = true };
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (paint.MeasureText(candidate) <= rect.Width) current = candidate;
            else
            {
                if (current.Length > 0) lines.Add(current);
                current = word;
                if (lines.Count >= maxLines - 1) break;
            }
        }
        if (current.Length > 0 && lines.Count < maxLines) lines.Add(current);
        var lineHeight = size * 1.3f;
        for (var i = 0; i < lines.Count; i += 1) canvas.DrawText(lines[i], rect.Left, rect.Top + size + i * lineHeight, paint);
    }

    private static SKColor RarityColor(string rarity) => rarity switch
    {
        "MYTHIC" => new SKColor(239, 91, 255),
        "LEGENDARY" => new SKColor(246, 171, 49),
        "EPIC" => new SKColor(129, 73, 213),
        "RARE" => new SKColor(47, 135, 225),
        _ => new SKColor(49, 150, 92)
    };

    private static SKTypeface FindTypeface(SKFontStyle style) =>
        SKTypeface.FromFamilyName("DejaVu Sans", style)
        ?? SKTypeface.FromFamilyName("Arial", style)
        ?? SKTypeface.Default;
}
