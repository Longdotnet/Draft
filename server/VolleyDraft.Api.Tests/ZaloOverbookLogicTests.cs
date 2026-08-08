using VolleyDraft.Api.Services;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOverbookLogicTests
{
    [Fact]
    public void FifteenBaselineThenFiveAppended_TrustsOrderAndTargetsLastTwo()
    {
        var previous = Enumerable.Range(1, 15).Select(index => $"u{index}").ToList();
        var current = previous.Concat(["u16", "u17", "u18", "u19", "u20"]).ToList();

        Assert.True(ZaloOverbookLogic.IsTrustedOrderTransition(previous, current, "u20"));
        var result = ZaloOverbookLogic.EvaluateCapacity(current, 18, 0, new Dictionary<string, string>());

        Assert.Equal(20, result.EffectiveSlotCount);
        Assert.Equal(2, result.ExcessSlotCount);
        Assert.Equal(["u19", "u20"], result.SuggestedTargetIds);
    }

    [Fact]
    public void DebouncedMultipleNewVotesAtEnd_AreStillTrusted()
    {
        var previous = Enumerable.Range(1, 18).Select(index => $"u{index}").ToList();
        var current = previous.Concat(["u19", "u20"]).ToList();

        Assert.True(ZaloOverbookLogic.IsTrustedOrderTransition(previous, current, "u20"));
    }

    [Fact]
    public void ArbitraryReorderWithoutReliableActor_IsRejected()
    {
        var previous = new[] { "a", "b", "c", "d" };
        var current = new[] { "a", "c", "b", "d", "e" };

        Assert.False(ZaloOverbookLogic.IsTrustedOrderTransition(previous, current, null));
    }

    [Fact]
    public void RevoteMovingActorToEnd_IsTrusted()
    {
        var previous = new[] { "a", "b", "c", "d" };
        var current = new[] { "a", "c", "d", "b" };

        Assert.True(ZaloOverbookLogic.IsTrustedOrderTransition(previous, current, "b"));
    }

    [Fact]
    public void SharedPairConsumesOneEffectiveSlot()
    {
        var voters = Enumerable.Range(1, 19).Select(index => $"u{index}").ToList();
        var shared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["u4"] = "slot-a",
            ["u5"] = "slot-a"
        };

        var result = ZaloOverbookLogic.EvaluateCapacity(voters, 18, 0, shared);

        Assert.Equal(18, result.EffectiveSlotCount);
        Assert.Equal(0, result.ExcessSlotCount);
        Assert.Empty(result.SuggestedTargetIds);
    }

    [Fact]
    public void ReservedManualSlotsReduceAvailablePollCapacity()
    {
        var voters = Enumerable.Range(1, 18).Select(index => $"u{index}").ToList();

        var result = ZaloOverbookLogic.EvaluateCapacity(voters, 18, 1, new Dictionary<string, string>());

        Assert.Equal(19, result.EffectiveSlotCount);
        Assert.Equal(1, result.ExcessSlotCount);
        Assert.Equal(["u18"], result.SuggestedTargetIds);
    }
}
