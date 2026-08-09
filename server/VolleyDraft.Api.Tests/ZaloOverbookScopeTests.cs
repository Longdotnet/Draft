using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOverbookScopeTests
{
    [Fact]
    public void Same_poll_and_same_options_are_accepted_regardless_of_option_order()
    {
        Assert.True(ZaloOverbookService.MatchesExpectedScope(
            "poll-1", ["cn", "t6"], "poll-1", ["t6", "cn"]));
    }

    [Fact]
    public void Different_poll_is_rejected()
    {
        Assert.False(ZaloOverbookService.MatchesExpectedScope(
            "poll-2", ["cn"], "poll-1", ["cn"]));
    }

    [Fact]
    public void Different_selected_options_are_rejected()
    {
        Assert.False(ZaloOverbookService.MatchesExpectedScope(
            "poll-1", ["t6"], "poll-1", ["cn"]));
    }

    [Fact]
    public void Admin_incident_key_is_stable_for_same_poll_snapshot_and_targets()
    {
        var first = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        var second = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Admin_incident_key_changes_when_poll_snapshot_changes()
    {
        var first = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1234, 19, ["u19"]);
        var second = ZaloOverbookService.BuildAdminIncidentKey("poll-1", ["cn"], 1235, 19, ["u19"]);
        Assert.NotEqual(first, second);
    }
}
