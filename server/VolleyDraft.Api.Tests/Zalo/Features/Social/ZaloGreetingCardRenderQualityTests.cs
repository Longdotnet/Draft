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
