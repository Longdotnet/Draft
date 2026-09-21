using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamCardAvailabilityTests
{
    [Theory]
    [InlineData(SessionStatus.Setup, false, false)]
    [InlineData(SessionStatus.CaptainSelection, false, false)]
    [InlineData(SessionStatus.Drafting, false, false)]
    [InlineData(SessionStatus.Cancelled, false, false)]
    [InlineData(SessionStatus.Setup, true, true)]
    [InlineData(SessionStatus.CaptainSelection, true, true)]
    [InlineData(SessionStatus.Drafting, true, false)]
    [InlineData(SessionStatus.Cancelled, true, false)]
    [InlineData(SessionStatus.Finished, false, false)]
    [InlineData(SessionStatus.Finished, true, true)]
    public void Card_is_only_exposed_when_authoritative_team_result_exists(
        SessionStatus status,
        bool hasNonCaptainAssignment,
        bool expected)
    {
        Assert.Equal(
            expected,
            ZaloTeamCardService.HasRenderableTeamResult(status, hasNonCaptainAssignment));
    }

    [Fact]
    public void Fixed_court_index_render_failure_is_not_replaced_with_legacy_card()
    {
        var fallbackCalled = false;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ZaloTeamCardService.RenderPosterOrFallback(
                (int)TeamPosterTemplate.NeonArena,
                () => throw new InvalidOperationException("court index failed"),
                _ =>
                {
                    fallbackCalled = true;
                    return [1, 2, 3];
                }));

        Assert.Equal("court index failed", exception.Message);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void Rotating_poster_render_failure_keeps_existing_legacy_fallback()
    {
        var expected = new byte[] { 1, 2, 3 };

        var result = ZaloTeamCardService.RenderPosterOrFallback(
            null,
            () => throw new InvalidOperationException("rotating poster failed"),
            exception =>
            {
                Assert.Equal("rotating poster failed", exception.Message);
                return expected;
            });

        Assert.Same(expected, result);
    }
}
