using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record Npc11CardResult(string Text, string ImageUrl, bool AiArtUsed, bool CacheHit);

public sealed class Npc11CardService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector protector,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<Npc11CardService> logger)
{
    private const int MaxReferenceBytes = 3 * 1024 * 1024;
    private const int MaxArtBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Styles = new(StringComparer.OrdinalIgnoreCase)
    {
        "classic", "cyber", "cyberpunk", "cute", "kawaii", "dark", "anime",
        "real", "realistic", "photo", "legend", "legendary"
    };

    public async Task<Npc11CardResult> GenerateAsync(
        string activeConnectionId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken = default)
    {
        var connection = await db.ZaloConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == activeConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("Không tìm thấy kết nối Zalo để tạo VolleyVerse card.");

        var senderId = NormalizeId(incoming.SenderId);
        var targetId = incoming.Mentions
            .Select(mention => NormalizeId(mention.Uid))
            .FirstOrDefault(id => id.Length > 0 && id != NormalizeId(incoming.BotId))
            ?? senderId;
        var fallbackName = targetId == senderId ? incoming.SenderName : "VolleyVerse Player";

        var style = ParseStyle(question);
        var member = await ResolveMemberAsync(connection, targetId, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(member?.DisplayName) ? fallbackName : member!.DisplayName.Trim();
        var avatarBytes = await LoadReferenceImageAsync(member?.AvatarUrl, cancellationToken);
        var profile = Npc11CharacterEngine.Create(targetId, displayName, style);

        var provider = (configuration["Npc11:ArtProvider"] ?? "cloudflare-flux2-klein-4b").Trim().ToLowerInvariant();
        var avatarHash = avatarBytes is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(avatarBytes)).ToLowerInvariant()
            : "no-avatar";
        var cacheMaterial = $"npc11-v1|{Npc11CharacterEngine.Season}|{profile.UserId}|{profile.Style}|{provider}|{avatarHash}";
        var cacheHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheMaterial))).ToLowerInvariant();
        var aiFileName = $"npc11-ai-{cacheHash[..24]}.png";
        var fallbackFileName = $"npc11-fallback-{cacheHash[..24]}.png";
        var aiRequested = ShouldAttemptAi(avatarBytes);

        var preferredName = aiRequested ? aiFileName : fallbackFileName;
        var preferredId = await FindCachedAssetIdAsync(connection.AdminUserId, preferredName, cancellationToken);
        if (preferredId is not null)
        {
            return new Npc11CardResult(
                BuildMessage(profile, aiRequested, true),
                BuildPublicImageUrl(preferredId),
                aiRequested,
                true);
        }

        byte[]? aiArt = null;
        if (aiRequested)
        {
            aiArt = await TryGenerateAiArtAsync(profile, avatarBytes, provider, cancellationToken);
            if (aiArt is null)
            {
                // The AI cache stays empty so future calls retry the worker. Reuse one
                // persistent fallback image while the worker is offline to avoid DB growth.
                var fallbackId = await FindCachedAssetIdAsync(connection.AdminUserId, fallbackFileName, cancellationToken);
                if (fallbackId is not null)
                {
                    return new Npc11CardResult(
                        BuildMessage(profile, false, true),
                        BuildPublicImageUrl(fallbackId),
                        false,
                        true);
                }
            }
        }

        var usedAi = aiArt is { Length: > 0 };
        var hero = aiArt ?? avatarBytes;
        var png = Npc11CardRenderer.Render(profile, hero, usedAi);
        if (png.Length == 0 || png.Length > MaxArtBytes)
            throw new InvalidOperationException("VolleyVerse card render produced an invalid image size.");

        var asset = new ZaloBotImageAsset
        {
            AdminUserId = connection.AdminUserId,
            FileName = usedAi ? aiFileName : fallbackFileName,
            ContentType = "image/png",
            Size = png.Length,
            Data = png,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotImageAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "NPC11 VolleyVerse rendered Target={TargetId} Style={Style} Provider={Provider} AiArt={AiArt} AvatarBytes={AvatarBytes} OutputBytes={OutputBytes}",
            targetId,
            profile.Style,
            provider,
            usedAi,
            avatarBytes?.Length ?? 0,
            png.Length);
        return new Npc11CardResult(
            BuildMessage(profile, usedAi, false),
            BuildPublicImageUrl(asset.Id),
            usedAi,
            false);
    }

    private bool ShouldAttemptAi(byte[]? referenceBytes)
    {
        if (!configuration.GetValue("Npc11:AiEnabled", true) || referenceBytes is not { Length: > 0 }) return false;
        var provider = (configuration["Npc11:ArtProvider"] ?? "cloudflare-flux2-klein-4b").Trim().ToLowerInvariant();
        if (provider.StartsWith("cloudflare-", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(configuration["Npc11:Cloudflare:AccountId"]) &&
                   !string.IsNullOrWhiteSpace(configuration["Npc11:Cloudflare:ApiToken"]);
        }

        var baseUrl = configuration["Npc11:ArtWorkerBaseUrl"]?.TrimEnd('/');
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    private async Task<string?> FindCachedAssetIdAsync(
        string adminUserId,
        string fileName,
        CancellationToken cancellationToken) =>
        await db.ZaloBotImageAssets.AsNoTracking()
            .Where(asset => asset.AdminUserId == adminUserId && asset.FileName == fileName)
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(asset => asset.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<BridgeMember?> ResolveMemberAsync(
        ZaloConnection connection,
        string targetId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var credentialsDocument = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
            var credentials = credentialsDocument.RootElement.Clone();
            var members = await bridge.GetMembersAsync(credentials, [targetId]);
            return members.FirstOrDefault(member => NormalizeId(member.ZaloUserId) == targetId)
                   ?? members.FirstOrDefault();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or CryptographicException or InvalidOperationException)
        {
            logger.LogWarning(exception, "NPC11 could not resolve Zalo profile Target={TargetId}; using sender fallback", targetId);
            return null;
        }
    }

    private async Task<byte[]?> LoadReferenceImageAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !await IsPublicHostAsync(uri, cancellationToken))
            return null;

        try
        {
            var client = httpClientFactory.CreateClient("TeamCardAvatars");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaxReferenceBytes ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                return null;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaxReferenceBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var bytes = output.ToArray();
            using var decoded = SKBitmap.Decode(bytes);
            return decoded is not null && decoded.Width > 0 && decoded.Height > 0 ? bytes : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogDebug(exception, "NPC11 could not download avatar Host={Host}", uri.Host);
            return null;
        }
    }

    private async Task<byte[]?> TryGenerateAiArtAsync(
        Npc11CharacterProfile profile,
        byte[]? referenceBytes,
        string provider,
        CancellationToken cancellationToken)
    {
        if (referenceBytes is not { Length: > 0 }) return null;
        if (provider.StartsWith("cloudflare-", StringComparison.OrdinalIgnoreCase))
            return await TryGenerateCloudflareArtAsync(profile, referenceBytes, cancellationToken);

        return await TryGenerateLocalWorkerArtAsync(profile, referenceBytes, provider, cancellationToken);
    }

    private async Task<byte[]?> TryGenerateLocalWorkerArtAsync(
        Npc11CharacterProfile profile,
        byte[] referenceBytes,
        string provider,
        CancellationToken cancellationToken)
    {
        var baseUrl = configuration["Npc11:ArtWorkerBaseUrl"]?.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var workerUri) || workerUri.Scheme is not ("http" or "https"))
            return null;

        var request = new Npc11ArtWorkerRequest(
            provider,
            profile.Seed,
            profile.Style,
            BuildArtPrompt(profile),
            [new Npc11ArtReference("subject", DetectMime(referenceBytes), Convert.ToBase64String(referenceBytes))],
            new Npc11ArtOutput(1024, 1365, "png"));

        var timeoutSeconds = Math.Clamp(configuration.GetValue("Npc11:ArtTimeoutSeconds", 45), 15, 180);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var client = httpClientFactory.CreateClient("Npc11ArtWorker");
            var endpoint = new Uri(workerUri, "/v1/volleyverse/art");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request)
            };
            var key = configuration["Npc11:ArtWorkerKey"];
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.TryAddWithoutValidation("x-npc11-key", key);

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("NPC11 art worker returned HTTP {StatusCode}; using fallback", (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<Npc11ArtWorkerResponse>(cancellationToken: timeout.Token);
            if (payload is null || !payload.Success || string.IsNullOrWhiteSpace(payload.ImageBase64)) return null;
            byte[] imageBytes;
            try { imageBytes = Convert.FromBase64String(payload.ImageBase64); }
            catch (FormatException) { return null; }
            if (imageBytes.Length is <= 0 or > MaxArtBytes) return null;

            using var decoded = SKBitmap.Decode(imageBytes);
            if (decoded is null || decoded.Width < 512 || decoded.Height < 512) return null;
            logger.LogInformation(
                "NPC11 AI art ready Provider={Provider} WorkerStrategy={Strategy} Size={Width}x{Height} Bytes={Bytes}",
                provider,
                payload.Strategy ?? "unknown",
                decoded.Width,
                decoded.Height,
                imageBytes.Length);
            return imageBytes;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            logger.LogWarning(exception, "NPC11 art worker unavailable; using deterministic fallback card");
            return null;
        }
    }

    private async Task<byte[]?> TryGenerateCloudflareArtAsync(
        Npc11CharacterProfile profile,
        byte[] referenceBytes,
        CancellationToken cancellationToken)
    {
        var accountId = configuration["Npc11:Cloudflare:AccountId"]?.Trim();
        var apiToken = configuration["Npc11:Cloudflare:ApiToken"]?.Trim();
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(apiToken)) return null;

        var reference = PrepareCloudflareReference(referenceBytes);
        if (reference is null) return null;

        var model = configuration["Npc11:Cloudflare:Model"]?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "@cf/black-forest-labs/flux-2-klein-4b";
        if (!model.StartsWith("@cf/", StringComparison.Ordinal))
        {
            logger.LogWarning("NPC11 Cloudflare model must use @cf/ prefix; using safe default instead");
            model = "@cf/black-forest-labs/flux-2-klein-4b";
        }

        var timeoutSeconds = Math.Clamp(configuration.GetValue("Npc11:Cloudflare:TimeoutSeconds", 45), 10, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var client = httpClientFactory.CreateClient("Npc11CloudflareAi");
            var endpoint = $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(accountId)}/ai/run/{model}";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(BuildArtPrompt(profile), Encoding.UTF8), "prompt");
            form.Add(new StringContent("1024", Encoding.ASCII), "width");
            form.Add(new StringContent("1365", Encoding.ASCII), "height");
            form.Add(new StringContent(Math.Abs((long)profile.Seed).ToString(CultureInfo.InvariantCulture), Encoding.ASCII), "seed");
            form.Add(new StringContent("3.5", Encoding.ASCII), "guidance");
            var imageContent = new ByteArrayContent(reference.Value.Bytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(reference.Value.MimeType);
            form.Add(imageContent, "input_image_0", reference.Value.FileName);

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                logger.LogWarning(
                    "NPC11 Cloudflare AI returned HTTP {StatusCode}; using fallback. Body={Body}",
                    (int)response.StatusCode,
                    error.Length > 500 ? error[..500] : error);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            if (!TryReadCloudflareImage(document.RootElement, out var base64)) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { return null; }
            if (bytes.Length is <= 0 or > MaxArtBytes) return null;

            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null || decoded.Width < 512 || decoded.Height < 512) return null;
            logger.LogInformation(
                "NPC11 Cloudflare AI art ready Model={Model} Size={Width}x{Height} Bytes={Bytes} Reference={ReferenceWidth}x{ReferenceHeight}",
                model, decoded.Width, decoded.Height, bytes.Length, reference.Value.Width, reference.Value.Height);
            return bytes;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            logger.LogWarning(exception, "NPC11 Cloudflare AI unavailable; using deterministic fallback card");
            return null;
        }
    }

    internal static CloudflareReferenceImage? PrepareCloudflareReference(byte[] bytes)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source is null || source.Width <= 0 || source.Height <= 0) return null;
        const int maximumEdge = 511;
        // Cloudflare requires every reference dimension to be below 512px. Normalize even tiny Zalo
        // thumbnails to a 511px longest edge so the edit model receives a consistent visual canvas.
        var scale = maximumEdge / (float)Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));

        using var resized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(resized))
        using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, new SKRect(0, 0, source.Width, source.Height), new SKRect(0, 0, width, height), paint);
        }
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return new CloudflareReferenceImage(encoded.ToArray(), "image/jpeg", "reference.jpg", width, height);
    }

    internal static bool TryReadCloudflareImage(JsonElement root, out string base64)
    {
        base64 = string.Empty;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return false;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return false;
        if (!result.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String) return false;
        base64 = image.GetString() ?? string.Empty;
        return base64.Length > 0;
    }

    internal static string BuildArtPrompt(Npc11CharacterProfile profile) =>
        $"""
        Create premium collectible volleyball character artwork using the supplied reference image as the primary subject.
        Preserve the subject's identity, species/object identity, silhouette, key colors, distinctive facial features and recognizable pose/gesture when appropriate.
        The reference may be a human, animal, mascot, toy, logo-like object or other non-human subject: do not force a human face or human anatomy onto a non-human object.
        Re-stage the same subject as a VolleyVerse hero in an indoor professional volleyball arena, dark emerald jersey number 11, cinematic stadium lights, energetic crowd bokeh, premium game-card key art.
        Archetype mood: {profile.Archetype}. Visual style: {profile.Style}. Rarity mood: {profile.Rarity}.
        Compose the subject center-right, upper body dominant, with some negative space on the left and lower edge for deterministic card UI.
        Keep volleyballs, hands, limbs and held objects anatomically/structurally coherent. Preserve object count unless the prompt explicitly requests otherwise.
        NO text, NO letters, NO numbers except the jersey number 11, NO logos, NO badges, NO card border, NO UI, NO watermark.
        Output only character artwork; the application renders all Vietnamese text and game UI separately.
        """;

    private string BuildPublicImageUrl(string assetId)
    {
        var configured = configuration["Public:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configuration["Zalo:WebhookUrl"], UriKind.Absolute, out var webhook))
            configured = webhook.GetLeftPart(UriPartial.Authority);
        configured ??= "http://localhost:5030";
        return $"{configured}/api/public/bot-images/{Uri.EscapeDataString(assetId)}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string BuildMessage(Npc11CharacterProfile profile, bool aiArtUsed, bool cached) =>
        $"🃏 VolleyVerse • {profile.DisplayName} — {profile.Rarity}\n" +
        $"Class: {profile.Archetype}\n" +
        $"Skill: {profile.SpecialSkill}\n" +
        $"Art: {(aiArtUsed ? "AI cloud" : "avatar fallback")}{(cached ? " • cache" : string.Empty)}";

    private static string ParseStyle(string question)
    {
        var tokens = question.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var matched = tokens
            .Select(token => token.Trim('"', '\'', ',', '.', '!', '?', ':', ';').ToLowerInvariant())
            .FirstOrDefault(Styles.Contains);
        return Npc11CharacterEngine.NormalizeStyle(matched);
    }

    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length > 12 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
        if (bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF") return "image/webp";
        return "image/jpeg";
    }

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();

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
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6)
        {
            var ipv4 = address.MapToIPv4().GetAddressBytes();
            return ipv4[0] != 10 && ipv4[0] != 127 &&
                   !(ipv4[0] == 169 && ipv4[1] == 254) &&
                   !(ipv4[0] == 172 && ipv4[1] is >= 16 and <= 31) &&
                   !(ipv4[0] == 192 && ipv4[1] == 168);
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length < 2 ||
               !(bytes[0] == 0xfc || bytes[0] == 0xfd || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
    }
}

public readonly record struct CloudflareReferenceImage(byte[] Bytes, string MimeType, string FileName, int Width, int Height);
public sealed record Npc11ArtReference(string Role, string MimeType, string ImageBase64);
public sealed record Npc11ArtOutput(int Width, int Height, string Format);
public sealed record Npc11ArtWorkerRequest(
    string Provider,
    int Seed,
    string Style,
    string Prompt,
    IReadOnlyList<Npc11ArtReference> References,
    Npc11ArtOutput Output);
public sealed record Npc11ArtWorkerResponse(bool Success, string? ImageBase64, string? Strategy, string? Error);
