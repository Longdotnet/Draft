using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftReadinessStateContractTests
{
    [Theory]
    [InlineData(ZaloDraftReadinessState.Ready, true)]
    [InlineData(ZaloDraftReadinessState.AlreadyDrafted, false)]
    [InlineData(ZaloDraftReadinessState.UnresolvedPassSlots, false)]
    [InlineData(ZaloDraftReadinessState.RosterNotFull, false)]
    [InlineData(ZaloDraftReadinessState.RosterOverCapacity, false)]
    [InlineData(ZaloDraftReadinessState.MissingProfiles, false)]
    [InlineData(ZaloDraftReadinessState.SessionStarted, false)]
    [InlineData(ZaloDraftReadinessState.MissingStartTime, false)]
    [InlineData(ZaloDraftReadinessState.InvalidStatus, false)]
    [InlineData(ZaloDraftReadinessState.NoRoster, false)]
    public void Compatibility_roster_ready_flag_never_disagrees_with_canonical_state(
        ZaloDraftReadinessState state,
        bool expectedReady)
    {
        Assert.Equal(expectedReady, ZaloDraftReadinessService.IsRosterReadyState(state));
    }
}
