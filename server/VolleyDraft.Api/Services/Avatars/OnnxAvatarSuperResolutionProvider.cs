using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace VolleyDraft.Api.Services.Avatars;

public sealed class OnnxAvatarSuperResolutionProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OnnxAvatarSuperResolutionProvider> logger) : IAvatarSuperResolutionProvider, IDisposable
{
    // Apache-2.0 ONNX Model Zoo sub-pixel CNN, fixed x3 output scale.
    // https://huggingface.co/onnxmodelzoo/super-resolution-10
    private const string DefaultModelUrl =
        "https://huggingface.co/onnxmodelzoo/super-resolution-10/resolve/main/super-resolution-10.onnx?download=true";
    private const string DefaultModelSha256 =
        "85f36ff88cc504a24af5e0602148bc56a8aa09a58eca8c0da2756f3e8186035e";
    private const int ModelScale = 3;
    private const int MinimumPreparedSide = 171; // 171 * 3 = 513, enough for Poster 01 hero height.
    private const int MaximumInputSide = 256;

    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private InferenceSession? session;
    private DateTimeOffset retryModelAfter = DateTimeOffset.MinValue;

    public async Task<SuperResolutionImage?> UpscaleAsync(
        byte[] sourceBytes,
        CancellationToken cancellationToken = default)
    {
        if (sourceBytes.Length == 0) return null;

        using var decoded = SKBitmap.Decode(sourceBytes);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;

        var minSide = Math.Min(decoded.Width, decoded.Height);
        var maxSide = Math.Max(decoded.Width, decoded.Height);
        if (minSide >= 300 || maxSide > MaximumInputSide) return null;

        var inferenceSession = await GetSessionAsync(cancellationToken);
        if (inferenceSession is null) return null;

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
        using var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        };
        using var results = inferenceSession.Run(inputs);
        var output = results.First().AsTensor<float>();
        if (output.Dimensions.Length < 4) return null;

        var outputHeight = output.Dimensions[2];
        var outputWidth = output.Dimensions[3];
        if (outputHeight <= height || outputWidth <= width) return null;

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
                var modelY = Math.Clamp(output[0, 0, y, x], 0f, 1f) * 255f;

                var red = ClampByte(modelY + 1.402f * (cr - 128f));
                var green = ClampByte(modelY - 0.344136f * (cb - 128f) - 0.714136f * (cr - 128f));
                var blue = ClampByte(modelY + 1.772f * (cb - 128f));
                enhanced.SetPixel(x, y, new SKColor(red, green, blue, basePixel.Alpha));
            }
        }

        using var image = SKImage.FromBitmap(enhanced);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded?.ToArray();
        return bytes is { Length: > 0 }
            ? new SuperResolutionImage(bytes, outputWidth, outputHeight, $"onnx-subpixel-x{ModelScale}")
            : null;
    }

    private async Task<InferenceSession?> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (session is not null) return session;
        if (DateTimeOffset.UtcNow < retryModelAfter) return null;

        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (session is not null) return session;
            if (DateTimeOffset.UtcNow < retryModelAfter) return null;

            var modelUrl = configuration["AvatarEnhancement:ModelUrl"]?.Trim();
            modelUrl = string.IsNullOrWhiteSpace(modelUrl) ? DefaultModelUrl : modelUrl;
            if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                logger.LogWarning("Avatar super-resolution model URL is invalid; enhancement disabled");
                retryModelAfter = DateTimeOffset.UtcNow.AddMinutes(15);
                return null;
            }

            try
            {
                var client = httpClientFactory.CreateClient("AvatarSuperResolutionModel");
                var modelBytes = await client.GetByteArrayAsync(uri, cancellationToken);
                if (modelBytes.Length is < 100_000 or > 2_000_000)
                    throw new InvalidDataException($"Unexpected super-resolution model size: {modelBytes.Length} bytes");

                var configuredSha = configuration["AvatarEnhancement:ModelSha256"]?.Trim().ToLowerInvariant();
                var expectedSha = string.IsNullOrWhiteSpace(configuredSha) && string.Equals(modelUrl, DefaultModelUrl, StringComparison.Ordinal)
                    ? DefaultModelSha256
                    : configuredSha;
                if (!string.IsNullOrWhiteSpace(expectedSha))
                {
                    var actualSha = Convert.ToHexStringLower(SHA256.HashData(modelBytes));
                    if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Avatar super-resolution model SHA256 did not match the configured value");
                }

                session = new InferenceSession(modelBytes);
                logger.LogInformation(
                    "Avatar super-resolution model ready Bytes={Bytes} Source={Source}",
                    modelBytes.Length,
                    uri.Host);
                return session;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or OnnxRuntimeException)
            {
                retryModelAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                logger.LogWarning(exception,
                    "Could not initialize avatar super-resolution model; captain avatars will use original source until retry");
                return null;
            }
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private static SKBitmap PrepareInput(SKBitmap source)
    {
        var minSide = Math.Min(source.Width, source.Height);
        if (minSide >= MinimumPreparedSide) return source.Copy();

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

    private static float ToLuma(byte red, byte green, byte blue) =>
        0.299f * red + 0.587f * green + 0.114f * blue;

    private static byte ClampByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    public void Dispose()
    {
        session?.Dispose();
        sessionGate.Dispose();
    }
}
