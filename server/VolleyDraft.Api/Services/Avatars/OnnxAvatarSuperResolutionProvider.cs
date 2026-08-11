using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Avatars;

public static class CaptainAvatarSuperResolution
{
    // Apache-2.0 ONNX Model Zoo sub-pixel CNN. It produces an x3 luminance image.
    // The model is only ~240 KB and runs locally on CPU after the first lazy download.
    private const string DefaultModelUrl =
        "https://huggingface.co/onnxmodelzoo/super-resolution-10/resolve/main/super-resolution-10.onnx?download=true";
    private const string DefaultModelSha256 =
        "85f36ff88cc504a24af5e0602148bc56a8aa09a58eca8c0da2756f3e8186035e";
    private const int ModelScale = 3;
    private const int MinimumPreparedSide = 171; // 171 * 3 = 513, Poster 01 hero height.
    private const int MaximumInputSide = 256;
    private const int DefaultEnhanceBelowPixels = 300;
    private const int DefaultTimeoutMs = 2_500;

    private static readonly HttpClient ModelClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private static readonly object SessionLock = new();
    private static readonly ConcurrentDictionary<string, byte[]> EnhancedCache = new(StringComparer.Ordinal);
    private static InferenceSession? session;
    private static DateTimeOffset retryModelAfter = DateTimeOffset.MinValue;

    public static IReadOnlyList<TeamCardTeam> Apply(IReadOnlyList<TeamCardTeam> teams) =>
        ApplyCore(teams, EnhanceSafe);

    // Public test hook keeps the production traversal testable without downloading the model.
    public static IReadOnlyList<TeamCardTeam> ApplyWithEnhancer(
        IReadOnlyList<TeamCardTeam> teams,
        Func<byte[], string, byte[]?> enhancer) =>
        ApplyCore(teams, enhancer);

    private static IReadOnlyList<TeamCardTeam> ApplyCore(
        IReadOnlyList<TeamCardTeam> teams,
        Func<byte[], string, byte[]?> enhancer)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("AVATAR_ENHANCEMENT_ENABLED"),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            return teams;
        }

        return teams.Select(team => team with
        {
            Slots = team.Slots.Select(slot => slot with
            {
                Players = slot.Players.Select(player =>
                {
                    if (!player.IsCaptain || player.AvatarData is null || player.AvatarData.Length == 0)
                        return player;

                    var enhanced = enhancer(player.AvatarData, player.Name);
                    return enhanced is { Length: > 0 } && !ReferenceEquals(enhanced, player.AvatarData)
                        ? player with { AvatarData = enhanced }
                        : player;
                }).ToList()
            }).ToList()
        }).ToList();
    }

    private static byte[]? EnhanceSafe(byte[] sourceBytes, string captainName)
    {
        using var decoded = SKBitmap.Decode(sourceBytes);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            WriteDiagnostic(captainName, 0, 0, 0, 0, 0, false, false, "invalid-source");
            return sourceBytes;
        }

        var originalWidth = decoded.Width;
        var originalHeight = decoded.Height;
        var threshold = ReadIntEnvironment("AVATAR_ENHANCEMENT_BELOW_PIXELS", DefaultEnhanceBelowPixels, 64, 700);
        if (Math.Min(originalWidth, originalHeight) >= threshold ||
            Math.Max(originalWidth, originalHeight) > MaximumInputSide)
        {
            WriteDiagnostic(
                captainName,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                0,
                false,
                false,
                "source-sufficient");
            return sourceBytes;
        }

        var hash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        if (EnhancedCache.TryGetValue(hash, out var cached))
        {
            using var cachedBitmap = SKBitmap.Decode(cached);
            WriteDiagnostic(
                captainName,
                originalWidth,
                originalHeight,
                cachedBitmap?.Width ?? originalWidth,
                cachedBitmap?.Height ?? originalHeight,
                0,
                true,
                true,
                $"onnx-subpixel-x{ModelScale}");
            return cached;
        }

        var timeoutMs = ReadIntEnvironment("AVATAR_ENHANCEMENT_TIMEOUT_MS", DefaultTimeoutMs, 250, 10_000);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource();
        try
        {
            var work = Task.Run(() => UpscaleCore(sourceBytes, timeout.Token), CancellationToken.None);
            var enhanced = work.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
            stopwatch.Stop();

            if (enhanced is null || enhanced.Length == 0)
            {
                WriteDiagnostic(
                    captainName,
                    originalWidth,
                    originalHeight,
                    originalWidth,
                    originalHeight,
                    stopwatch.ElapsedMilliseconds,
                    false,
                    false,
                    "fallback-original");
                return sourceBytes;
            }

            using var enhancedBitmap = SKBitmap.Decode(enhanced);
            if (enhancedBitmap is null ||
                enhancedBitmap.Width <= originalWidth ||
                enhancedBitmap.Height <= originalHeight)
            {
                WriteDiagnostic(
                    captainName,
                    originalWidth,
                    originalHeight,
                    originalWidth,
                    originalHeight,
                    stopwatch.ElapsedMilliseconds,
                    false,
                    false,
                    "fallback-invalid-output");
                return sourceBytes;
            }

            EnhancedCache.TryAdd(hash, enhanced);
            WriteDiagnostic(
                captainName,
                originalWidth,
                originalHeight,
                enhancedBitmap.Width,
                enhancedBitmap.Height,
                stopwatch.ElapsedMilliseconds,
                false,
                true,
                $"onnx-subpixel-x{ModelScale}");
            return enhanced;
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            stopwatch.Stop();
            WriteDiagnostic(
                captainName,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                stopwatch.ElapsedMilliseconds,
                false,
                false,
                "fallback-timeout");
            return sourceBytes;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timeout.Cancel();
            stopwatch.Stop();
            Console.WriteLine($"[CaptainAvatarEnhancement] error={exception.GetType().Name} message=\"{Sanitize(exception.Message)}\"");
            WriteDiagnostic(
                captainName,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                stopwatch.ElapsedMilliseconds,
                false,
                false,
                "fallback-error");
            return sourceBytes;
        }
    }

    private static byte[]? UpscaleCore(byte[] sourceBytes, CancellationToken cancellationToken)
    {
        var inferenceSession = GetSession(cancellationToken);
        if (inferenceSession is null) return null;

        using var decoded = SKBitmap.Decode(sourceBytes);
        if (decoded is null) return null;
        using var prepared = PrepareInput(decoded);

        var width = prepared.Width;
        var height = prepared.Height;
        var input = new DenseTensor<float>(new[] { 1, 1, height, width });
        var inputSpan = input.Buffer.Span;
        for (var y = 0; y < height; y += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x += 1)
            {
                var pixel = prepared.GetPixel(x, y);
                inputSpan[y * width + x] = ToLuma(pixel.Red, pixel.Green, pixel.Blue) / 255f;
            }
        }

        var inputName = inferenceSession.InputMetadata.Keys.Single();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        };
        using var results = inferenceSession.Run(inputs);
        var output = results.First().AsTensor<float>();
        if (output.Dimensions.Length < 4) return null;

        var outputHeight = output.Dimensions[2];
        var outputWidth = output.Dimensions[3];
        if (outputHeight <= height || outputWidth <= width) return null;
        var outputValues = output.ToArray();

        using var chromaBase = ResizeBitmap(prepared, outputWidth, outputHeight);
        using var enhanced = new SKBitmap(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var y = 0; y < outputHeight; y += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < outputWidth; x += 1)
            {
                var basePixel = chromaBase.GetPixel(x, y);
                var cb = 128f - 0.168736f * basePixel.Red - 0.331264f * basePixel.Green + 0.5f * basePixel.Blue;
                var cr = 128f + 0.5f * basePixel.Red - 0.418688f * basePixel.Green - 0.081312f * basePixel.Blue;
                var modelY = Math.Clamp(outputValues[y * outputWidth + x], 0f, 1f) * 255f;

                enhanced.SetPixel(x, y, new SKColor(
                    ClampByte(modelY + 1.402f * (cr - 128f)),
                    ClampByte(modelY - 0.344136f * (cb - 128f) - 0.714136f * (cr - 128f)),
                    ClampByte(modelY + 1.772f * (cb - 128f)),
                    basePixel.Alpha));
            }
        }

        using var image = SKImage.FromBitmap(enhanced);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    private static InferenceSession? GetSession(CancellationToken cancellationToken)
    {
        if (session is not null) return session;
        if (DateTimeOffset.UtcNow < retryModelAfter) return null;

        lock (SessionLock)
        {
            if (session is not null) return session;
            if (DateTimeOffset.UtcNow < retryModelAfter) return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modelUrl = Environment.GetEnvironmentVariable("AVATAR_ENHANCEMENT_MODEL_URL")?.Trim();
                modelUrl = string.IsNullOrWhiteSpace(modelUrl) ? DefaultModelUrl : modelUrl;
                if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new InvalidDataException("Avatar enhancement model URL is invalid");

                using var modelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                modelTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var modelBytes = ModelClient.GetByteArrayAsync(uri, modelTimeout.Token).GetAwaiter().GetResult();
                if (modelBytes.Length is < 100_000 or > 2_000_000)
                    throw new InvalidDataException($"Unexpected model size: {modelBytes.Length} bytes");

                var expectedSha = Environment.GetEnvironmentVariable("AVATAR_ENHANCEMENT_MODEL_SHA256")?.Trim();
                if (string.IsNullOrWhiteSpace(expectedSha) && string.Equals(modelUrl, DefaultModelUrl, StringComparison.Ordinal))
                    expectedSha = DefaultModelSha256;
                if (!string.IsNullOrWhiteSpace(expectedSha))
                {
                    var actualSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
                    if (!string.Equals(actualSha, expectedSha.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                        throw new InvalidDataException("Avatar enhancement model SHA256 mismatch");
                }

                session = new InferenceSession(modelBytes);
                Console.WriteLine($"[CaptainAvatarEnhancement] model-ready bytes={modelBytes.Length} host={uri.Host}");
                return session;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or OnnxRuntimeException)
            {
                retryModelAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                Console.WriteLine($"[CaptainAvatarEnhancement] model-unavailable error={exception.GetType().Name} message=\"{Sanitize(exception.Message)}\"");
                return null;
            }
        }
    }

    private static SKBitmap PrepareInput(SKBitmap source)
    {
        var minSide = Math.Min(source.Width, source.Height);
        if (minSide >= MinimumPreparedSide)
            return ResizeBitmap(source, source.Width, source.Height);

        var scale = MinimumPreparedSide / (float)Math.Max(1, minSide);
        var width = Math.Clamp((int)MathF.Round(source.Width * scale), 1, MaximumInputSide);
        var height = Math.Clamp((int)MathF.Round(source.Height * scale), 1, MaximumInputSide);
        return ResizeBitmap(source, width, height);
    }

    private static SKBitmap ResizeBitmap(SKBitmap source, int width, int height)
    {
        var resized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(resized);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium
        };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(
            source,
            new SKRect(0, 0, source.Width, source.Height),
            new SKRect(0, 0, width, height),
            paint);
        return resized;
    }

    private static void WriteDiagnostic(
        string captainName,
        int originalWidth,
        int originalHeight,
        int enhancedWidth,
        int enhancedHeight,
        long elapsedMs,
        bool cacheHit,
        bool enhanced,
        string strategy)
    {
        Console.WriteLine(
            $"[CaptainAvatarEnhancement] Captain=\"{Sanitize(captainName)}\" Original={originalWidth}x{originalHeight} Enhanced={enhancedWidth}x{enhancedHeight} ElapsedMs={elapsedMs} CacheHit={cacheHit.ToString().ToLowerInvariant()} EnhancedApplied={enhanced.ToString().ToLowerInvariant()} Strategy={strategy}");
    }

    private static int ReadIntEnvironment(string name, int fallback, int min, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static string Sanitize(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);

    private static float ToLuma(byte red, byte green, byte blue) =>
        0.299f * red + 0.587f * green + 0.114f * blue;

    private static byte ClampByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
