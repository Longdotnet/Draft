using Xunit;
using Action = VolleyDraft.Api.Tests.Fuzzing.PassSlotDraftGateMultiInstanceFuzzTests.MultiInstanceAction;
using ActionKind = VolleyDraft.Api.Tests.Fuzzing.PassSlotDraftGateMultiInstanceFuzzTests.MultiInstanceActionKind;
using State = VolleyDraft.Api.Tests.Fuzzing.PassSlotDraftGateMultiInstanceFuzzTests.MultiInstanceState;
using Target = VolleyDraft.Api.Tests.Fuzzing.PassSlotDraftGateMultiInstanceFuzzTests.MultiInstanceDraftGateTarget;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class PassSlotDraftGateMultiInstancePromotionFuzzTests
{
    [Fact]
    public async Task Multi_instance_interleaving_findings_are_reproduced_minimized_and_reverified_before_promotion()
    {
        Action[] seedActions =
        [
            new(ActionKind.OpenOwned),
            new(ActionKind.SwitchInstance),
            new(ActionKind.ClaimOwned),
            new(ActionKind.RestartActiveInstance),
            new(ActionKind.BeginApplyOwned),
            new(ActionKind.SwitchInstance),
            new(ActionKind.CompleteOwned),
            new(ActionKind.OpenForeignConnection),
            new(ActionKind.OpenForeignSession),
            new(ActionKind.AttemptDraft)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new Action((ActionKind)random.NextInt(10)),
                operationCount: 16);
            await using var target = new Target();
            var scenario = new StatefulFuzzCase<Action>(
                $"pass-slot-draft-gate-multi-instance-promotion-{seed}",
                seed,
                actions);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<State, Action>(
                static () => new Target());
            var promotion = await StatefulFuzzPromotion.PrepareAsync(
                scenario,
                isolatedTarget,
                confirmationRuns: 3);

            Assert.True(
                promotion.IsPromotable,
                $"{Describe(result)}; candidate failure was not stable enough for corpus promotion");

            Assert.False(
                result.Failed,
                $"{Describe(result)}; minimizedReproducer={promotion.SerializePermanentReproducer()}");
        }
    }

    private static string Describe(StatefulFuzzRunResult<Action> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";
}
