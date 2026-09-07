using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftReadinessTeamResultAuthorityTests
{
    [Theory]
    [InlineData(SessionStatus.Setup, false, false)]
    [InlineData(SessionStatus.CaptainSelection, false, false)]
    [InlineData(SessionStatus.Drafting, false, false)]
    [InlineData(SessionStatus.Cancelled, false, false)]
    [InlineData(SessionStatus.Finished, false, false)]
    [InlineData(SessionStatus.Setup, true, true)]
    [InlineData(SessionStatus.CaptainSelection, true, true)]
    [InlineData(SessionStatus.Drafting, true, false)]
    [InlineData(SessionStatus.Cancelled, true, false)]
    [InlineData(SessionStatus.Finished, true, true)]
    public void Readiness_only_reports_team_result_when_allocation_is_authoritative(
        SessionStatus status,
        bool hasNonCaptainAssignment,
        bool expected)
    {
        Assert.Equal(
            expected,
            ZaloDraftReadinessService.HasAuthoritativeTeamResult(status, hasNonCaptainAssignment));
    }
}
