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
}
