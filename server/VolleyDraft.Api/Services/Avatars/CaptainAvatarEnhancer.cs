using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Avatars;

public sealed class CaptainAvatarEnhancer(
    IAvatarSuperResolutionProvider provider,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<CaptainAvatarEnhancer> logger)
{
    private const int DefaultEnhanceBelowPixels = 300;
    private const int DefaultTimeoutMs = 2_500;
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromDays(30);
    private static readonly TimeSpan FallbackCacheDuration = TimeSpan.FromMinutes(5);

    public async Task<CaptainAvatarEnhancementResult> EnhanceAsync(
        byte[] originalBytes,
        string captainName,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var decoded = SKBitmap.Decode(originalBytes);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                originalBytes,
                0,
                0,
                0,
                0,
                false,
                false,
                "invalid-source"), captainName, stopwatch);
        }

        var originalWidth = decoded.Width;
        var originalHeight = decoded.Height;
        var threshold = Math.Clamp(
            configuration.GetValue("AvatarEnhancement:EnhanceBelowPixels", DefaultEnhanceBelowPixels),
            64,
            700);
        if (Math.Min(originalWidth, originalHeight) >= threshold)
        {
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                originalBytes,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                false,
                false,
                "source-sufficient"), captainName, stopwatch);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        var cacheKey = $"captain-avatar-enhanced:v1:{hash}";
        if (cache.TryGetValue<CachedEnhancement>(cacheKey, out var cached) && cached is not null)
        {
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                cached.Data,
                originalWidth,
                originalHeight,
                cached.Width,
                cached.Height,
                true,
                cached.Enhanced,
                cached.Strategy), captainName, stopwatch);
        }

        var timeoutMs = Math.Clamp(
            configuration.GetValue("AvatarEnhancement:TimeoutMs", DefaultTimeoutMs),
            250,
            10_000);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            var enhanced = await provider.UpscaleAsync(originalBytes, timeout.Token);
            if (enhanced is not null &&
                enhanced.Data.Length > 0 &&
                enhanced.Width > originalWidth &&
                enhanced.Height > originalHeight &&
                IsDecodable(enhanced.Data, enhanced.Width, enhanced.Height))
            {
                cache.Set(
                    cacheKey,
                    new CachedEnhancement(enhanced.Data, enhanced.Width, enhanced.Height, true, enhanced.Strategy),
                    SuccessCacheDuration);
                return LogAndReturn(new CaptainAvatarEnhancementResult(
                    enhanced.Data,
                    originalWidth,
                    originalHeight,
                    enhanced.Width,
                    enhanced.Height,
                    false,
                    true,
                    enhanced.Strategy), captainName, stopwatch);
            }

            cache.Set(
                cacheKey,
                new CachedEnhancement(originalBytes, originalWidth, originalHeight, false, "fallback-original"),
                FallbackCacheDuration);
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                originalBytes,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                false,
                false,
                "fallback-original"), captainName, stopwatch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Captain avatar enhancement timed out Captain={Captain} TimeoutMs={TimeoutMs}",
                captainName,
                timeoutMs);
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                originalBytes,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                false,
                false,
                "fallback-timeout"), captainName, stopwatch);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Captain avatar enhancement failed Captain={Captain}; using original image",
                captainName);
            return LogAndReturn(new CaptainAvatarEnhancementResult(
                originalBytes,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                false,
                false,
                "fallback-error"), captainName, stopwatch);
        }
    }

    private CaptainAvatarEnhancementResult LogAndReturn(
        CaptainAvatarEnhancementResult result,
        string captainName,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        logger.LogInformation(
            "[CaptainAvatarEnhancement] Captain=\"{Captain}\" Original={OriginalWidth}x{OriginalHeight} Enhanced={EnhancedWidth}x{EnhancedHeight} ElapsedMs={ElapsedMs} CacheHit={CacheHit} EnhancedApplied={EnhancedApplied} Strategy={Strategy}",
            captainName,
            result.OriginalWidth,
            result.OriginalHeight,
            result.EnhancedWidth,
            result.EnhancedHeight,
            stopwatch.ElapsedMilliseconds,
            result.CacheHit,
            result.Enhanced,
            result.Strategy);
        return result;
    }

    private static bool IsDecodable(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        using var decoded = SKBitmap.Decode(bytes);
        return decoded is not null && decoded.Width == expectedWidth && decoded.Height == expectedHeight;
    }

    private sealed record CachedEnhancement(
        byte[] Data,
        int Width,
        int Height,
        bool Enhanced,
        string Strategy);
}

public sealed record CaptainAvatarEnhancementResult(
    byte[] Data,
    int OriginalWidth,
    int OriginalHeight,
    int EnhancedWidth,
    int EnhancedHeight,
    bool CacheHit,
    bool Enhanced,
    string Strategy);
