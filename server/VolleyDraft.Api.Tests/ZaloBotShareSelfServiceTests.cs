using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotShareSelfServiceTests
{
    [Fact]
    public void Current_vote_in_selected_option_is_accepted()
    {
        Assert.True(ZaloBotService.HasCurrentPollVote(
            "poll-1", "[\"option-cn\"]", "poll-1", "[\"option-cn\"]"));
    }

    [Fact]
    public void Vote_from_different_poll_is_rejected()
    {
        Assert.False(ZaloBotService.HasCurrentPollVote(
            "poll-old", "[\"option-cn\"]", "poll-current", "[\"option-cn\"]"));
    }

    [Fact]
    public void Vote_from_unlinked_option_is_rejected()
    {
        Assert.False(ZaloBotService.HasCurrentPollVote(
            "poll-1", "[\"option-t6\"]", "poll-1", "[\"option-cn\"]"));
    }

    [Fact]
    public void Multiple_selected_options_accept_any_intersection()
    {
        Assert.True(ZaloBotService.HasCurrentPollVote(
            "poll-1", "[\"option-cn\"]", "poll-1", "[\"option-t6\",\"option-cn\"]"));
    }

    [Fact]
    public void Malformed_option_json_is_rejected_safely()
    {
        Assert.False(ZaloBotService.HasCurrentPollVote(
            "poll-1", "not-json", "poll-1", "[\"option-cn\"]"));
    }
}
