using Xunit;
using FreshnessAction = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalFreshnessRaceFuzzTests.ProposalAction;
using FreshnessActionKind = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalFreshnessRaceFuzzTests.ProposalActionKind;
using FreshnessState = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalFreshnessRaceFuzzTests.ProposalFreshnessRaceState;
using FreshnessTarget = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalFreshnessRaceFuzzTests.ProposalFreshnessRaceTarget;
using StatusAction = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalStatusRaceFuzzTests.ProposalAction;
using StatusActionKind = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalStatusRaceFuzzTests.ProposalActionKind;
using StatusState = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalStatusRaceFuzzTests.ProposalStatusRaceState;
using StatusTarget = VolleyDraft.Api.Tests.Fuzzing.ZaloAutoSessionProposalStatusRaceFuzzTests.ProposalStatusRaceTarget;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionProposalRacePromotionFuzzTests
{
    [Fact]
    public async Task Proposal_freshness_race_findings_are_reproduced_minimized_and_reverified_before_promotion()
    {
        FreshnessAction[] seedActions =
        [
            new(FreshnessActionKind.PersistNewerSnapshot),
            new(FreshnessActionKind.PersistOlderSnapshot)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new FreshnessAction((FreshnessActionKind)random.NextInt(Enum.GetValues<FreshnessActionKind>().Length)),
                operationCount: 8);
            var scenario = new StatefulFuzzCase<FreshnessAction>(
                $"auto-session-proposal-freshness-race-promotion-{seed}",
                seed,
                actions);
            await using var target = new FreshnessTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<FreshnessState, FreshnessAction>(
                static () => new FreshnessTarget());
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

    [Fact]
    public async Task Proposal_terminal_status_race_findings_are_reproduced_minimized_and_reverified_before_promotion()
    {
        StatusAction[] seedActions =
        [
            new(StatusActionKind.WinnerCommitsCreated),
            new(StatusActionKind.LoserPersistsFailure)
        ];

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => new StatusAction((StatusActionKind)random.NextInt(Enum.GetValues<StatusActionKind>().Length)),
                operationCount: 6);
            var scenario = new StatefulFuzzCase<StatusAction>(
                $"auto-session-proposal-status-race-promotion-{seed}",
                seed,
                actions);
            await using var target = new StatusTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<StatusState, StatusAction>(
                static () => new StatusTarget());
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

    private static string Describe<TAction>(StatefulFuzzRunResult<TAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions)}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";
}
