using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Avatars;

namespace VolleyDraft.Api.Tests;

public sealed class CaptainAvatarSuperResolutionTests
{
    [Fact]
    public void ApplyWithEnhancer_only_replaces_captain_avatar_data()
    {
        var captainBytes = new byte[] { 1, 2, 3 };
        var playerBytes = new byte[] { 4, 5, 6 };
        var calls = new List<string>();
        var teams = new List<TeamCardTeam>
        {
            new(
                "Team A",
                "Captain A",
                12.5,
                [
                    new TeamCardSlot(
                        "Captain A",
                        [new TeamCardPlayer("Captain A", "https://example.test/captain.jpg", captainBytes, true)],
                        true),
                    new TeamCardSlot(
                        "Player B",
                        [new TeamCardPlayer("Player B", "https://example.test/player.jpg", playerBytes, false)])
                ])
        };

        var result = CaptainAvatarSuperResolution.ApplyWithEnhancer(
            teams,
            (source, name) =>
            {
                calls.Add(name);
                return [9, 9, 9, 9];
            });

        Assert.Equal(["Captain A"], calls);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, result[0].Slots[0].Players[0].AvatarData);
        Assert.Same(playerBytes, result[0].Slots[1].Players[0].AvatarData);
    }

    [Fact]
    public void ApplyWithEnhancer_keeps_captain_when_enhancer_falls_back()
    {
        var captainBytes = new byte[] { 7, 8, 9 };
        var teams = new List<TeamCardTeam>
        {
            new(
                "Team A",
                "Captain A",
                10,
                [new TeamCardSlot(
                    "Captain A",
                    [new TeamCardPlayer("Captain A", AvatarData: captainBytes, IsCaptain: true)],
                    true)])
        };

        var result = CaptainAvatarSuperResolution.ApplyWithEnhancer(
            teams,
            (source, _) => source);

        Assert.Same(captainBytes, result[0].Slots[0].Players[0].AvatarData);
    }

    [Fact]
    public void ApplyWithEnhancer_does_not_call_provider_for_missing_avatar()
    {
        var called = false;
        var teams = new List<TeamCardTeam>
        {
            new(
                "Team A",
                "Captain A",
                10,
                [new TeamCardSlot(
                    "Captain A",
                    [new TeamCardPlayer("Captain A", AvatarData: null, IsCaptain: true)],
                    true)])
        };

        var result = CaptainAvatarSuperResolution.ApplyWithEnhancer(
            teams,
            (_, _) =>
            {
                called = true;
                return [1];
            });

        Assert.False(called);
        Assert.Null(result[0].Slots[0].Players[0].AvatarData);
    }
}
