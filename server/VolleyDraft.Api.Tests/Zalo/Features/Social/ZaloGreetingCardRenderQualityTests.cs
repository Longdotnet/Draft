using System.Buffers.Binary;
using SkiaSharp;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingCardRenderQualityTests
{
    [Theory]
    [InlineData("Chào ngày mới ☀️", "Chào ngày mới", 1)]
    [InlineData("Ngủ ngon nhé 🌙", "Ngủ ngon nhé", 2)]
    [InlineData("Nhẹ lòng nha 😌", "Nhẹ lòng nha", 5)]
    [InlineData("Cùng nhau nhé 🤝", "Cùng nhau nhé", 6)]
    public void PrepareText_removes_platform_emoji_and_returns_vector_icon(
        string input,
        string expectedText,
        int expectedIcon)
    {
        var text = ZaloGreetingCardRenderQuality.PrepareText(input, out var icon);

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedText.Length, text.Length);
        Assert.Equal(expectedIcon, (int)icon);
    }

    [Fact]
    public void Morning_renderer_outputs_bandwidth_friendly_jpeg()
    {
        var copy = ZaloSocialCardCopyGenerator.CreateFallback(
            ZaloDailyGreetingKind.Morning,
            ZaloDailyGreetingMood.Warm,
            hasMatchToday: true);

        var bytes = ZaloSocialGreetingCardRenderer.Render(3, "CLB Tân bình-The First Spike", copy);

        Assert.True(IsJpeg(bytes));
        Assert.InRange(bytes.Length, 10_001, 1_500_000);
    }

    [Fact]
    public void Night_renderer_outputs_bandwidth_friendly_jpeg()
    {
        var copy = ZaloNightGreetingCardCopyGenerator.CreateFallback(ZaloDailyGreetingMood.TenderRomantic);

        var bytes = ZaloNightGreetingCardRenderer.Render(1, "CLB Tân bình-The First Spike", copy);

        Assert.True(IsJpeg(bytes));
        Assert.InRange(bytes.Length, 10_001, 1_500_000);
    }

    [Fact]
    public void Uploaded_image_optimizer_caps_dimensions_and_outputs_jpeg()
    {
        using var sourceBitmap = new SKBitmap(2400, 1200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(sourceBitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.White, TextSize = 120, IsAntialias = true };
            canvas.DrawText("Volley Draft", 140, 600, paint);
        }
        using var sourceImage = SKImage.FromBitmap(sourceBitmap);
        using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Png, 100);

        var optimized = ZaloBotImageService.OptimizeForDelivery(sourceData.ToArray());

        Assert.True(IsJpeg(optimized));
        using var decoded = SKBitmap.Decode(optimized);
        Assert.NotNull(decoded);
        Assert.Equal(1600, decoded.Width);
        Assert.Equal(800, decoded.Height);
    }

    [Fact]
    public void Uploaded_image_optimizer_preserves_exif_display_orientation()
    {
        using var sourceBitmap = new SKBitmap(120, 60, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(sourceBitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(new SKRect(60, 0, 120, 60), paint);
        }
        using var sourceImage = SKImage.FromBitmap(sourceBitmap);
        using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Jpeg, 95);
        var orientedJpeg = AddExifOrientation(sourceData.ToArray(), orientation: 6);

        using var codecData = SKData.CreateCopy(orientedJpeg);
        using var codec = SKCodec.Create(codecData);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedOrigin.RightTop, codec.EncodedOrigin);

        var optimized = ZaloBotImageService.OptimizeForDelivery(orientedJpeg);

        using var decoded = SKBitmap.Decode(optimized);
        Assert.NotNull(decoded);
        Assert.Equal(60, decoded.Width);
        Assert.Equal(120, decoded.Height);

        var top = decoded.GetPixel(decoded.Width / 2, decoded.Height / 4);
        var bottom = decoded.GetPixel(decoded.Width / 2, decoded.Height * 3 / 4);
        Assert.True(top.Red > top.Blue, $"Expected red-dominant top half but got {top}.");
        Assert.True(bottom.Blue > bottom.Red, $"Expected blue-dominant bottom half but got {bottom}.");
    }

    [Fact]
    public void Uploaded_image_optimizer_rejects_extreme_declared_dimensions_before_full_decode()
    {
        using var sourceBitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        sourceBitmap.Erase(SKColors.CornflowerBlue);
        using var sourceImage = SKImage.FromBitmap(sourceBitmap);
        using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var oversizedHeader = RewritePngDimensions(sourceData.ToArray(), width: 40_000, height: 40_000);

        var bounds = SKBitmap.DecodeBounds(oversizedHeader);
        Assert.Equal(40_000, bounds.Width);
        Assert.Equal(40_000, bounds.Height);

        var error = Assert.Throws<InvalidOperationException>(
            () => ZaloBotImageService.OptimizeForDelivery(oversizedHeader));
        Assert.Contains("safe decode budget", error.Message);
    }

    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
        Assert.True(IsJpeg(jpeg));
        Assert.InRange(orientation, (ushort)1, (ushort)8);

        // APP1 payload: Exif header + little-endian TIFF header + one Orientation tag.
        var payload = new byte[]
        {
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };
        var segmentLength = payload.Length + 2;
        var result = new byte[jpeg.Length + payload.Length + 4];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)segmentLength);
        payload.CopyTo(result.AsSpan(6));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + payload.Length));
        return result;
    }

    private static byte[] RewritePngDimensions(byte[] png, int width, int height)
    {
        var copy = png.ToArray();
        Assert.True(copy.Length >= 33);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(copy, 12, 4));

        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(29, 4), Crc32(copy.AsSpan(12, 17)));
        return copy;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0xFF &&
        bytes[1] == 0xD8 &&
        bytes[^2] == 0xFF &&
        bytes[^1] == 0xD9;
}
