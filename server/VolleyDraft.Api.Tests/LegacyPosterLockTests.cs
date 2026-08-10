using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class LegacyPosterLockTests
{
    [Fact]
    public void Sessions_created_before_collection_rollout_stay_on_legacy_neon_arena()
    {
        Assert.False(ZaloTeamCardService.ShouldJoinPosterRotation(
            new DateTimeOffset(2026, 8, 10, 4, 59, 13, TimeSpan.Zero)));
    }

    [Fact]
    public void Sessions_created_from_collection_rollout_join_the_ten_poster_deck()
    {
        Assert.True(ZaloTeamCardService.ShouldJoinPosterRotation(
            new DateTimeOffset(2026, 8, 10, 4, 59, 14, TimeSpan.Zero)));
        Assert.True(ZaloTeamCardService.ShouldJoinPosterRotation(
            new DateTimeOffset(2026, 8, 10, 5, 30, 0, TimeSpan.Zero)));
    }
}
