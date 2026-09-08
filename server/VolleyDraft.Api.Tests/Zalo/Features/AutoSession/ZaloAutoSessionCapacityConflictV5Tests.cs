using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionCapacityConflictV5Tests
{
    [Fact]
    public void Resolve_NoExplicitCapacity_LeavesApprovedDefaultEligible()
    {
        var result = ZaloAutoSessionCapacityPolicyV5.Resolve("Vote sân UTE tuần sau. 17:45-22:00");

        Assert.False(result.HasExplicitCapacity);
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("Vote sân UTE. Max 18 slots/sân", 18, 6)]
    [InlineData("Vote sân UTE. tối đa 21 người", 21, 7)]
    [InlineData("Vote sân UTE. capacity 24 slots", 24, 8)]
    public void Resolve_OneExplicitCapacity_RemainsAuthoritative(
        string question,
        int expectedCapacity,
        int expectedTeamSize)
    {
        var result = ZaloAutoSessionCapacityPolicyV5.Resolve(question);

        Assert.True(result.HasExplicitCapacity);
        Assert.True(result.IsValid);
        Assert.Equal(expectedCapacity, result.Capacity);
        Assert.Equal(expectedTeamSize, result.TeamSize);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Resolve_RepeatedSameCapacity_IsStillUnambiguous()
    {
        var result = ZaloAutoSessionCapacityPolicyV5.Resolve(
            "Max 18 slots/sân. Nhắc lại: tối đa 18 người.");

        Assert.True(result.HasExplicitCapacity);
        Assert.True(result.IsValid);
        Assert.Equal(18, result.Capacity);
        Assert.Equal(6, result.TeamSize);
    }

    [Theory]
    [InlineData("Max 18 slots/sân. Tối đa 21 người")]
    [InlineData("capacity 24 slots - max 18 slots/sân")]
    [InlineData("Tối đa 21 người. capacity 24 slots. max 18 slots/sân")]
    public void Resolve_ConflictingExplicitCapacities_FailsClosed(string question)
    {
        var result = ZaloAutoSessionCapacityPolicyV5.Resolve(question);

        Assert.True(result.HasExplicitCapacity);
        Assert.False(result.IsValid);
        Assert.Equal(0, result.Capacity);
        Assert.Equal(0, result.TeamSize);
        Assert.Equal("explicit_capacity_conflict", result.ErrorCode);
        Assert.Contains("nhiều mức tối đa khác nhau", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sửa", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ConflictMessage_IsOrderIndependentAndListsGroundedValues()
    {
        var first = ZaloAutoSessionCapacityPolicyV5.Resolve("max 21 slots. max 18 slots");
        var second = ZaloAutoSessionCapacityPolicyV5.Resolve("max 18 slots. max 21 slots");

        Assert.Equal("explicit_capacity_conflict", first.ErrorCode);
        Assert.Equal(first.ErrorMessage, second.ErrorMessage);
        Assert.Contains("18, 21", first.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Max 20 slots/sân", 20)]
    [InlineData("Max 3 slots/sân", 3)]
    [InlineData("Max 99 slots/sân", 99)]
    public void Resolve_SingleUnsupportedCapacity_StillFailsClosed(
        string question,
        int expectedCapacity)
    {
        var result = ZaloAutoSessionCapacityPolicyV5.Resolve(question);

        Assert.True(result.HasExplicitCapacity);
        Assert.False(result.IsValid);
        Assert.Equal(expectedCapacity, result.Capacity);
        Assert.Equal("explicit_capacity_not_supported", result.ErrorCode);
    }
}
