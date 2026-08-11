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
