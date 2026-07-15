using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloTeamCardService(
    VolleyDraftDbContext db,
    ZaloIntegrationService zaloIntegration,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<ZaloTeamCardService> logger)
{
    private const int MaxAvatarBytes = 2 * 1024 * 1024;

    public async Task<GeneratedTeamCard?> GenerateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return null;

        var hydrated = await zaloIntegration.HydrateMissingMemberAvatarsAsync(session.AdminUserId, session.Id);
        if (!hydrated.IsSuccess)
        {
            logger.LogWarning(
                "Could not hydrate missing team-card avatars for Session={SessionId}: {Error}",
                session.Id,
                hydrated.Error);
        }

        var trackedTeams = await db.Teams.AsNoTracking()
            .Include(team => team.CaptainSessionPlayer)
            .Include(team => team.AssignedSlots)
                .ThenInclude(slot => slot.Players)
                .ThenInclude(link => link.SessionPlayer)
                .ThenInclude(player => player.PlayerProfile)
            .Where(team => team.SessionId == sessionId)
            .OrderBy(team => team.Name)
            .ToListAsync(cancellationToken);

        var playerNames = trackedTeams
            .SelectMany(team => team.AssignedSlots)
            .SelectMany(slot => slot.Players)
            .Select(link => link.SessionPlayer.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fallbackAvatarByName = (await db.PlayerProfiles.AsNoTracking()
                .Where(profile => profile.AvatarUrl != null && playerNames.Contains(profile.DisplayName))
                .Select(profile => new { profile.DisplayName, profile.AvatarUrl })
                .ToListAsync(cancellationToken))
            .GroupBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().AvatarUrl, StringComparer.OrdinalIgnoreCase);

        var avatarUrls = trackedTeams
            .SelectMany(team => team.AssignedSlots)
            .SelectMany(slot => slot.Players)
            .Select(link => GetAvatarUrl(link.SessionPlayer, fallbackAvatarByName))
            .Where(url => IsHttpUrl(url))
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();
        var avatarTasks = avatarUrls.ToDictionary(
            url => url,
            url => LoadAvatarAsync(url, cancellationToken),
            StringComparer.Ordinal);
        await Task.WhenAll(avatarTasks.Values);
        var avatars = avatarTasks.ToDictionary(
            item => item.Key,
            item => item.Value.Result,
            StringComparer.Ordinal);

        var teams = trackedTeams.Select(team =>
        {
            var slots = team.AssignedSlots
                .OrderByDescending(slot => slot.IsCaptainSlot)
                .ThenBy(slot => slot.DisplayName)
                .Select(slot =>
                {
                    var players = slot.Players
                        .OrderBy(link => link.RotationOrder)
                        .Select(link =>
                        {
                            var player = link.SessionPlayer;
                            var avatarUrl = GetAvatarUrl(player, fallbackAvatarByName);
                            avatars.TryGetValue(avatarUrl ?? string.Empty, out var avatarData);
                            return new TeamCardPlayer(
                                player.DisplayName,
                                avatarUrl,
                                avatarData,
                                player.Id == team.CaptainSessionPlayerId);
                        })
                        .ToList();
                    if (players.Count == 0)
                    {
                        players.Add(new TeamCardPlayer(
                            slot.DisplayName,
                            null,
                            null,
                            slot.IsCaptainSlot));
                    }
                    return new TeamCardSlot(slot.DisplayName, players, slot.IsCaptainSlot);
                })
                .ToList();
            return new TeamCardTeam(
                team.Name,
                team.CaptainSessionPlayer?.DisplayName,
                team.TotalAverageScore,
                slots);
        }).ToList();

        return new GeneratedTeamCard(
            SimpleTeamCardPng.Render(session.Name, session.StartTime, session.Location, teams),
            "image/png");
    }

    private static string? GetAvatarUrl(
        SessionPlayer player,
        IReadOnlyDictionary<string, string?> fallbackAvatarByName)
    {
        if (!string.IsNullOrWhiteSpace(player.AvatarUrl)) return player.AvatarUrl;
        if (!string.IsNullOrWhiteSpace(player.PlayerProfile?.AvatarUrl)) return player.PlayerProfile.AvatarUrl;
        return fallbackAvatarByName.GetValueOrDefault(player.DisplayName);
    }

    public string GetPublicUrl(string sessionId)
    {
        var configured = configuration["Public:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configuration["Zalo:WebhookUrl"], UriKind.Absolute, out var webhook))
            configured = webhook.GetLeftPart(UriPartial.Authority);
        configured ??= "http://localhost:5030";
        return $"{configured}/api/public/sessions/{Uri.EscapeDataString(sessionId)}/team-card.png?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private async Task<byte[]?> LoadAvatarAsync(string url, CancellationToken cancellationToken)
    {
        var cacheKey = $"team-card-avatar:{url}";
        if (cache.TryGetValue<AvatarCacheEntry>(cacheKey, out var cached)) return cached?.Data;

        byte[]? result = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !await IsPublicHostAsync(uri, cancellationToken))
            {
                return null;
            }

            var client = httpClientFactory.CreateClient("TeamCardAvatars");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaxAvatarBytes ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaxAvatarBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var bytes = output.ToArray();
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is not null && decoded.Width > 0 && decoded.Height > 0) result = bytes;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            logger.LogDebug(exception, "Could not load team-card avatar Host={Host}",
                Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid");
        }
        finally
        {
            cache.Set(
                cacheKey,
                new AvatarCacheEntry(result),
                result is null ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6));
        }
        return result;
    }

    private static async Task<bool> IsPublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.MapToIPv6().GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6)
        {
            var ipv4 = address.MapToIPv4().GetAddressBytes();
            return ipv4[0] != 10 &&
                   ipv4[0] != 127 &&
                   !(ipv4[0] == 169 && ipv4[1] == 254) &&
                   !(ipv4[0] == 172 && ipv4[1] is >= 16 and <= 31) &&
                   !(ipv4[0] == 192 && ipv4[1] == 168);
        }
        return !(bytes[0] == 0xfc || bytes[0] == 0xfd || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private sealed record AvatarCacheEntry(byte[]? Data);
}

public sealed record GeneratedTeamCard(byte[] Data, string ContentType);
public sealed record TeamCardPlayer(string Name, string? AvatarUrl = null, byte[]? AvatarData = null, bool IsCaptain = false);
public sealed record TeamCardSlot(string DisplayName, IReadOnlyList<TeamCardPlayer> Players, bool IsCaptainSlot = false);
public sealed record TeamCardTeam(
    string Name,
    string? CaptainName,
    double AverageScore,
    IReadOnlyList<TeamCardSlot> Slots)
{
    public TeamCardTeam(string name, string? captainName, IReadOnlyList<string> players)
        : this(
            name,
            captainName,
            0,
            players.Select((player, index) => new TeamCardSlot(
                player,
                [new TeamCardPlayer(player, IsCaptain: string.Equals(player, captainName, StringComparison.OrdinalIgnoreCase))],
                index == 0 && string.Equals(player, captainName, StringComparison.OrdinalIgnoreCase))).ToList())
    {
    }
}

internal static class SimpleTeamCardPng
{
    private const int Width = 1440;
    private const int Height = 900;
    private static readonly SKColor BackgroundTop = new(9, 18, 38);
    private static readonly SKColor BackgroundBottom = new(24, 39, 66);
    private static readonly SKColor TextPrimary = new(15, 23, 42);
    private static readonly SKColor TextSecondary = new(71, 85, 105);
    private static readonly SKTypeface RegularTypeface = FindTypeface(SKFontStyle.Normal);
    private static readonly SKTypeface BoldTypeface = FindTypeface(SKFontStyle.Bold);
    private static readonly SKColor[] TeamColors =
    [
        new(14, 165, 233),
        new(249, 115, 22),
        new(34, 197, 94),
        new(168, 85, 247)
    ];

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        IReadOnlyList<TeamCardTeam> teams) =>
        Render(sessionName, startTime, null, teams);

    public static byte[] Render(
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create team-card canvas.");
        var canvas = surface.Canvas;
        using (var background = new SKPaint
               {
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0),
                       new SKPoint(Width, Height),
                       [BackgroundTop, BackgroundBottom],
                       null,
                       SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(new SKRect(0, 0, Width, Height), background);
        }

        DrawText(canvas, "VOLLEY DRAFT • ĐỘI HÌNH", 48, 48, 25, new SKColor(125, 211, 252), true);
        DrawText(canvas, sessionName, 48, 95, 42, SKColors.White, true, 930);
        var metadata = BuildMetadata(startTime, location);
        DrawText(canvas, metadata, 48, 139, 22, new SKColor(203, 213, 225), false, 1180);

        var cards = teams.Take(3).ToList();
        if (cards.Count == 0)
        {
            DrawRoundedRect(canvas, new SKRect(220, 280, 1220, 650), 30, new SKColor(255, 255, 255, 235));
            DrawText(canvas, "Chưa có kết quả chia đội", 420, 470, 42, TextSecondary, true, 700);
            return Encode(surface);
        }

        const float margin = 40;
        const float gap = 24;
        const float top = 180;
        const float bottom = 850;
        var cardWidth = (Width - margin * 2 - gap * 2) / 3f;
        for (var index = 0; index < cards.Count; index += 1)
        {
            var team = cards[index];
            var color = TeamColors[index % TeamColors.Length];
            var left = margin + index * (cardWidth + gap);
            DrawTeamCard(canvas, new SKRect(left, top, left + cardWidth, bottom), team, color);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    private static void DrawTeamCard(SKCanvas canvas, SKRect rect, TeamCardTeam team, SKColor color)
    {
        var shadowRect = rect;
        shadowRect.Offset(0, 7);
        DrawRoundedRect(canvas, shadowRect, 25, new SKColor(0, 0, 0, 45));
        DrawRoundedRect(canvas, rect, 25, new SKColor(248, 250, 252));
        var header = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 124);
        DrawTopRoundedHeader(canvas, header, 25, color);

        DrawText(canvas, team.Name, rect.Left + 25, rect.Top + 46, 31, SKColors.White, true, rect.Width - 145);
        var score = team.AverageScore.ToString("0.0", CultureInfo.InvariantCulture);
        DrawPill(canvas, $"ĐIỂM {score}", rect.Right - 112, rect.Top + 22, 88, 30, new SKColor(255, 255, 255, 52), SKColors.White);
        var captain = string.IsNullOrWhiteSpace(team.CaptainName) ? "Chưa chọn đội trưởng" : $"Đội trưởng: {team.CaptainName}";
        DrawText(canvas, captain, rect.Left + 25, rect.Top + 88, 19, new SKColor(240, 249, 255), false, rect.Width - 50);

        var playerCount = team.Slots.Sum(slot => Math.Max(1, slot.Players.Count));
        DrawText(canvas, $"{playerCount} người", rect.Left + 25, rect.Top + 160, 20, TextPrimary, true);
        DrawText(canvas, $"{team.Slots.Count} slot", rect.Left + 145, rect.Top + 160, 20, TextSecondary, false);
        using (var separator = new SKPaint { Color = new SKColor(226, 232, 240), StrokeWidth = 1.5f, IsAntialias = true })
            canvas.DrawLine(rect.Left + 25, rect.Top + 180, rect.Right - 25, rect.Top + 180, separator);

        var rowTop = rect.Top + 195;
        const float rowHeight = 70;
        var slots = team.Slots.Take(6).ToList();
        for (var index = 0; index < slots.Count; index += 1)
        {
            DrawSlotRow(canvas, rect.Left + 18, rowTop + index * rowHeight, rect.Width - 36, rowHeight - 6, index + 1, slots[index], color);
        }
        if (slots.Count == 0)
            DrawText(canvas, "Chưa có thành viên", rect.Left + 80, rowTop + 70, 23, TextSecondary, false, rect.Width - 130);

        DrawText(canvas, "Tạo tự động bởi Volley Draft", rect.Left + 25, rect.Bottom - 22, 14, new SKColor(148, 163, 184), false, rect.Width - 50);
    }

    private static void DrawSlotRow(
        SKCanvas canvas,
        float x,
        float y,
        float width,
        float height,
        int number,
        TeamCardSlot slot,
        SKColor color)
    {
        var background = number % 2 == 0 ? new SKColor(241, 245, 249) : new SKColor(248, 250, 252);
        DrawRoundedRect(canvas, new SKRect(x, y, x + width, y + height), 13, background);
        DrawText(canvas, number.ToString(CultureInfo.InvariantCulture), x + 10, y + 39, 17, new SKColor(148, 163, 184), true);

        var avatarX = x + 50;
        var centerY = y + height / 2;
        var displayedPlayers = slot.Players.Take(2).ToList();
        for (var index = displayedPlayers.Count - 1; index >= 0; index -= 1)
        {
            var player = displayedPlayers[index];
            DrawAvatar(canvas, avatarX + index * 25, centerY, 21, player, color);
        }
        var names = string.Join(" / ", slot.Players.Select(player => player.Name));
        var nameX = avatarX + (displayedPlayers.Count > 1 ? 58 : 34);
        var hasCaptain = slot.IsCaptainSlot || slot.Players.Any(player => player.IsCaptain);
        DrawText(canvas, names, nameX, y + 31, 18, TextPrimary, true, x + width - nameX - 10);
        if (slot.Players.Count > 1)
            DrawText(canvas, "SHARE SLOT", nameX, y + 52, 12, color, true, 100);
        else if (hasCaptain)
            DrawPill(canvas, "ĐỘI TRƯỞNG", nameX, y + 39, 91, 18, new SKColor(254, 243, 199), new SKColor(180, 83, 9), 10);
    }

    private static void DrawAvatar(SKCanvas canvas, float centerX, float centerY, float radius, TeamCardPlayer player, SKColor teamColor)
    {
        using var border = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(centerX, centerY, radius + 2.5f, border);
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

        using var fallback = new SKPaint { Color = AvatarColor(player.Name, teamColor), IsAntialias = true };
        canvas.DrawCircle(centerX, centerY, radius, fallback);
        var initials = Initials(player.Name);
        using var text = TextPaint(13, SKColors.White, true);
        var textWidth = text.MeasureText(initials);
        canvas.DrawText(initials, centerX - textWidth / 2, centerY + 5, text);
    }

    private static SKRect CropSquare(int width, int height)
    {
        var size = Math.Min(width, height);
        return new SKRect((width - size) / 2f, (height - size) / 2f, (width + size) / 2f, (height + size) / 2f);
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
        float fontSize = 12)
    {
        DrawRoundedRect(canvas, new SKRect(x, y, x + width, y + height), height / 2, background);
        using var paint = TextPaint(fontSize, foreground, true);
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
        float maxWidth = float.MaxValue)
    {
        using var paint = TextPaint(fontSize, color, bold);
        var fitted = FitText(text, paint, maxWidth);
        canvas.DrawText(fitted, x, baseline, paint);
    }

    private static SKPaint TextPaint(float fontSize, SKColor color, bool bold) => new()
    {
        Color = color,
        TextSize = fontSize,
        Typeface = bold ? BoldTypeface : RegularTypeface,
        IsAntialias = true,
        SubpixelText = true
    };

    private static string FitText(string value, SKPaint paint, float maxWidth)
    {
        if (maxWidth == float.MaxValue || paint.MeasureText(value) <= maxWidth) return value;
        var text = value.Trim();
        while (text.Length > 1 && paint.MeasureText(text + "…") > maxWidth) text = text[..^1].TrimEnd();
        return text + "…";
    }

    private static void DrawRoundedRect(SKCanvas canvas, SKRect rect, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, paint);
    }

    private static void DrawTopRoundedHeader(SKCanvas canvas, SKRect rect, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(rect.Left, rect.Bottom);
        path.LineTo(rect.Left, rect.Top + radius);
        path.QuadTo(rect.Left, rect.Top, rect.Left + radius, rect.Top);
        path.LineTo(rect.Right - radius, rect.Top);
        path.QuadTo(rect.Right, rect.Top, rect.Right, rect.Top + radius);
        path.LineTo(rect.Right, rect.Bottom);
        path.Close();
        canvas.DrawPath(path, paint);
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
        return parts.Count == 0 ? "Thông tin trận đang được cập nhật" : string.Join("  •  ", parts);
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "?";
        var first = parts[0][0].ToString();
        var last = parts.Length > 1 ? parts[^1][0].ToString() : string.Empty;
        return (first + last).ToUpper(new CultureInfo("vi-VN"));
    }

    private static SKColor AvatarColor(string name, SKColor fallback)
    {
        uint hash = 2166136261;
        foreach (var character in name)
        {
            hash ^= character;
            hash *= 16777619;
        }
        var factor = .72f + (hash % 18) / 100f;
        return new SKColor(
            (byte)Math.Clamp(fallback.Red * factor, 0, 255),
            (byte)Math.Clamp(fallback.Green * factor, 0, 255),
            (byte)Math.Clamp(fallback.Blue * factor, 0, 255));
    }

    private static SKTypeface FindTypeface(SKFontStyle style) =>
        SKTypeface.FromFamilyName("Noto Sans", style) ??
        SKTypeface.FromFamilyName("DejaVu Sans", style) ??
        SKTypeface.FromFamilyName("Segoe UI", style) ??
        SKTypeface.Default;

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }
}
