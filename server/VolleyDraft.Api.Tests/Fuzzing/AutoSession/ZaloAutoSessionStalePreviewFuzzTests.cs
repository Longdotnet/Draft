using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionStalePreviewFuzzTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly AutoSessionAction[] SeedActions =
    [
        new(AutoSessionActionKind.Preview),
        new(AutoSessionActionKind.EditPoll),
        new(AutoSessionActionKind.Confirm)
    ];

    [Fact]
    public async Task Stale_preview_confirmation_fails_closed_after_authoritative_poll_edit()
    {
        var scenario = new StatefulFuzzCase<AutoSessionAction>(
            "auto-session-stale-preview-production-shape",
            20260909,
            SeedActions);
        var target = new AutoSessionStalePreviewTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(1, target.LastState!.RejectedConfirmations);
        Assert.Equal(0, target.LastState.SuccessfulConfirmations);
    }

    [Fact]
    public async Task Stateful_sequence_mutations_never_allow_confirmation_against_stale_poll_provenance()
    {
        for (var seed = 1; seed <= 256; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedActions,
                seed,
                CreateAction,
                operationCount: 8);
            var scenario = new StatefulFuzzCase<AutoSessionAction>(
                $"auto-session-stale-preview-{seed}",
                seed,
                actions);
            var target = new AutoSessionStalePreviewTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    [Fact]
    public async Task Restore_after_edit_reenables_only_a_matching_preview_confirmation()
    {
        var scenario = new StatefulFuzzCase<AutoSessionAction>(
            "auto-session-preview-edit-restore-confirm",
            991,
            [
                new(AutoSessionActionKind.Preview),
                new(AutoSessionActionKind.EditPoll),
                new(AutoSessionActionKind.Confirm),
                new(AutoSessionActionKind.RestorePoll),
                new(AutoSessionActionKind.Confirm)
            ]);
        var target = new AutoSessionStalePreviewTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(1, target.LastState!.RejectedConfirmations);
        Assert.Equal(1, target.LastState.SuccessfulConfirmations);
    }

    private static AutoSessionAction CreateAction(StableFuzzRandom random) =>
        new((AutoSessionActionKind)random.NextInt(Enum.GetValues<AutoSessionActionKind>().Length));

    private static string Describe(StatefulFuzzRunResult<AutoSessionAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum AutoSessionActionKind
    {
        Preview,
        EditPoll,
        RestorePoll,
        Confirm
    }

    internal sealed record AutoSessionAction(AutoSessionActionKind Kind);

    internal sealed class AutoSessionFuzzState
    {
        public string OptionText { get; set; } = OriginalOption;
        public ZaloAutoSessionCandidate? PreviewCandidate { get; set; }
        public bool? LastConfirmationAccepted { get; set; }
        public bool? LastConfirmationExpectedFresh { get; set; }
        public int SuccessfulConfirmations { get; set; }
        public int RejectedConfirmations { get; set; }
    }

    internal sealed class AutoSessionStalePreviewTarget : IStatefulFuzzTarget<AutoSessionFuzzState, AutoSessionAction>
    {
        public string Name => "auto-session-stale-preview";

        public AutoSessionFuzzState? LastState { get; private set; }

        public AutoSessionFuzzState CreateState(StatefulFuzzCase<AutoSessionAction> scenario)
        {
            LastState = new AutoSessionFuzzState();
            return LastState;
        }

        public ValueTask ApplyAsync(
            AutoSessionFuzzState state,
            AutoSessionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Kind)
            {
                case AutoSessionActionKind.Preview:
                {
                    var extraction = ZaloPollScheduleParser.ExtractSchedule(
                        BuildPoll(state.OptionText),
                        new ZaloTrackedGroupData(),
                        Now);
                    state.PreviewCandidate = extraction.Candidates.SingleOrDefault();
                    state.LastConfirmationAccepted = null;
                    state.LastConfirmationExpectedFresh = null;
                    break;
                }
                case AutoSessionActionKind.EditPoll:
                    state.OptionText = EditedOption;
                    break;
                case AutoSessionActionKind.RestorePoll:
                    state.OptionText = OriginalOption;
                    break;
                case AutoSessionActionKind.Confirm:
                    Confirm(state);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(AutoSessionFuzzState state)
        {
            if (state.LastConfirmationAccepted is not null &&
                state.LastConfirmationExpectedFresh is not null &&
                state.LastConfirmationAccepted != state.LastConfirmationExpectedFresh)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "preview-provenance-must-match-current-poll",
                    $"confirmation accepted={state.LastConfirmationAccepted} while sourceFresh={state.LastConfirmationExpectedFresh}");
            }
        }

        private static void Confirm(AutoSessionFuzzState state)
        {
            if (state.PreviewCandidate is null)
            {
                state.LastConfirmationAccepted = null;
                state.LastConfirmationExpectedFresh = null;
                return;
            }

            var poll = BuildPoll(state.OptionText);
            state.LastConfirmationExpectedFresh = ZaloPollScheduleParser.ValidateCandidateConsistency(
                poll,
                state.PreviewCandidate,
                out _);

            try
            {
                ZaloAutoSessionActionExecutor.EnsureCandidatesMatchPollSource(
                    poll,
                    [state.PreviewCandidate]);
                state.LastConfirmationAccepted = true;
                state.SuccessfulConfirmations += 1;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("auto_session_candidate_source_mismatch:", StringComparison.Ordinal))
            {
                state.LastConfirmationAccepted = false;
                state.RejectedConfirmations += 1;
            }
        }
    }

    private static readonly DateTimeOffset Created =
        new(2026, 9, 5, 20, 0, 0, VietnamOffset);
    private static readonly DateTimeOffset Now =
        new(2026, 9, 5, 21, 0, 0, VietnamOffset);
    private const string OriginalOption = "CN 13/9 17:45";
    private const string EditedOption = "CN 20/9 17:45";

    private static BridgePoll BuildPoll(string optionText) => new(
        "poll-fuzz-auto-session-stale-preview",
        "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00",
        "leader-1",
        [new BridgePollOption("o1", optionText, 2, [])],
        true,
        false,
        false,
        false,
        2,
        Created.ToUnixTimeMilliseconds(),
        Created.ToUnixTimeMilliseconds(),
        0);
}
