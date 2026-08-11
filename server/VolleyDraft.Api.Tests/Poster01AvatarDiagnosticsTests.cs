using VolleyDraft.Api.Services.Posters;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class Poster01AvatarDiagnosticsTests
{
    [Fact]
    public void Tiny_avatar_reports_real_scale_and_strips_query_tokens()
    {
        var diagnostic = CourtIndexCrispPortraitPosterRenderer.BuildAvatarDiagnostic(
            "https://cdn.example.com/avatar/user/avatar_240.jpg?token=secret&expires=123",
            12_345,
            240,
            240,
            414,
            513);

        Assert.Equal("https://cdn.example.com/avatar/user/avatar_240.jpg", diagnostic.Source);
        Assert.Equal(12_345, diagnostic.Bytes);
        Assert.Equal(240, diagnostic.SourceWidth);
        Assert.Equal(240, diagnostic.SourceHeight);
        Assert.Equal(414, diagnostic.TargetWidth);
        Assert.Equal(513, diagnostic.TargetHeight);
        Assert.InRange(diagnostic.RequiredScale, 2.13f, 2.14f);
        Assert.Equal("Tiny", diagnostic.QualityBucket);
    }

    [Fact]
    public void Medium_avatar_is_classified_without_being_called_hd()
    {
        var diagnostic = CourtIndexCrispPortraitPosterRenderer.BuildAvatarDiagnostic(
            "https://cdn.example.com/avatar_640.jpg",
            80_000,
            640,
            640,
            414,
            513);

        Assert.InRange(diagnostic.RequiredScale, .80f, .81f);
        Assert.Equal("Medium", diagnostic.QualityBucket);
    }

    [Fact]
    public void Hd_avatar_and_missing_avatar_are_distinguished()
    {
        var hd = CourtIndexCrispPortraitPosterRenderer.BuildAvatarDiagnostic(
            "https://cdn.example.com/avatar_960.jpg",
            220_000,
            960,
            960,
            414,
            513);
        var missing = CourtIndexCrispPortraitPosterRenderer.BuildAvatarDiagnostic(
            null,
            0,
            0,
            0,
            414,
            513);

        Assert.Equal("HD", hd.QualityBucket);
        Assert.Equal("Missing", missing.QualityBucket);
        Assert.Equal("none", missing.Source);
        Assert.Equal(0f, missing.RequiredScale);
    }
}
