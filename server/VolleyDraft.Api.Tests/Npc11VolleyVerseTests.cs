using SkiaSharp;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class Npc11VolleyVerseTests
{
    [Fact]
    public void Character_profile_is_stable_for_same_zalo_user_and_season()
    {
        var first = Npc11CharacterEngine.Create("zalo-123", "Ếch Cầu Cứu", "cyber");
        var second = Npc11CharacterEngine.Create("zalo-123", "Ếch Cầu Cứu", "cyber");

        Assert.Equal(first, second);
        Assert.InRange(first.Defense, 55, 99);
        Assert.InRange(first.Spirit, 55, 99);
        Assert.InRange(first.Support, 55, 99);
        Assert.InRange(first.Reflex, 55, 99);
        Assert.InRange(first.Charm, 55, 99);
    }

    [Fact]
    public void Card_renderer_produces_expected_png_dimensions_with_reference_art()
    {
        using var avatar = new SKBitmap(640, 640, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(avatar))
        {
            canvas.Clear(new SKColor(88, 178, 104));
            using var eye = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(220, 210, 70, eye);
            canvas.DrawCircle(420, 210, 70, eye);
        }
        using var image = SKImage.FromBitmap(avatar);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        var profile = Npc11CharacterEngine.Create("frog-11", "Ếch Cầu Cứu", "classic");
        var png = Npc11CardRenderer.Render(profile, encoded.ToArray());

        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);
        Assert.Equal(Npc11CardRenderer.Width, rendered!.Width);
        Assert.Equal(Npc11CardRenderer.Height, rendered.Height);
        Assert.True(png.Length > 50_000);
    }


    [Fact]
    public void Renderer_exposes_a_real_font_family_for_vietnamese_card_copy()
    {
        Assert.False(string.IsNullOrWhiteSpace(Npc11CardRenderer.SelectedFontFamily));

        var profile = Npc11CharacterEngine.Create("vi-font", "Đặng Thế Nguyễn", "classic");
        var png = Npc11CardRenderer.Render(profile, null);
        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);
        Assert.Equal(1080, rendered!.Width);
        Assert.Equal(1600, rendered.Height);
    }

    [Theory]
    [InlineData("cyberpunk", "cyber")]
    [InlineData("kawaii", "cute")]
    [InlineData("photo", "realistic")]
    [InlineData("anything", "classic")]
    public void Styles_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, Npc11CharacterEngine.NormalizeStyle(input));
    }

    [Fact]
    public void Cloudflare_reference_is_resized_below_512_pixels_and_encoded_as_jpeg()
    {
        using var source = new SKBitmap(900, 600, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(source)) canvas.Clear(new SKColor(80, 180, 110));
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var prepared = Npc11CardService.PrepareCloudflareReference(encoded.ToArray());

        Assert.NotNull(prepared);
        Assert.True(prepared.Value.Width < 512);
        Assert.True(prepared.Value.Height < 512);
        Assert.Equal("image/jpeg", prepared.Value.MimeType);
        using var decoded = SKBitmap.Decode(prepared.Value.Bytes);
        Assert.NotNull(decoded);
        Assert.Equal(prepared.Value.Width, decoded!.Width);
        Assert.Equal(prepared.Value.Height, decoded.Height);
    }

    [Fact]
    public void Cloudflare_response_parser_reads_result_image_only_for_success_envelopes()
    {
        using var ok = System.Text.Json.JsonDocument.Parse("{\"success\":true,\"result\":{\"image\":\"aGVsbG8=\"}}");
        Assert.True(Npc11CardService.TryReadCloudflareImage(ok.RootElement, out var image));
        Assert.Equal("aGVsbG8=", image);

        using var failed = System.Text.Json.JsonDocument.Parse("{\"success\":false,\"result\":{\"image\":\"aGVsbG8=\"}}");
        Assert.False(Npc11CardService.TryReadCloudflareImage(failed.RootElement, out _));
    }

    [Fact]
    public void Art_prompt_tells_worker_to_preserve_non_human_subjects_and_avoid_ui_text()
    {
        var profile = Npc11CharacterEngine.Create("frog-11", "Ếch Cầu Cứu", "classic");
        var prompt = Npc11CardService.BuildArtPrompt(profile);

        Assert.Contains("animal", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("object", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not force a human face", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO text", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
