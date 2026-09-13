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
        var scenario = new StatefulFuzzCase<AutoSessionAction>("auto-session-stale-preview-production-shape", 20260909, SeedActions);
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
            var actions = StatefulSequenceMutator.Mutate(SeedActions, seed, CreateAction, operationCount: 8);
            var scenario = new StatefulFuzzCase<AutoSessionAction>($"auto-session-stale-preview-{seed}", seed, actions);
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
            [new(AutoSessionActionKind.Preview), new(AutoSessionActionKind.EditPoll), new(AutoSessionActionKind.Confirm), new(AutoSessionActionKind.RestorePoll), new(AutoSessionActionKind.Confirm)]);
        var target = new AutoSessionStalePreviewTarget();
        var result = await StatefulFuzzRunner.RunAsync(scenario, target);
        Assert.False(result.Failed, Describe(result));
        Assert.Equal(1, target.LastState!.RejectedConfirmations);
        Assert.Equal(1, target.LastState.SuccessfulConfirmations);
    }

    [Fact]
    public async Task Delayed_confirmation_after_match_start_must_fail_closed_even_when_poll_is_unchanged()
    {
        var scenario = new StatefulFuzzCase<AutoSessionAction>(
            "auto-session-preview-midnight-delay-confirm",
            20260913,
            [new(AutoSessionActionKind.Preview), new(AutoSessionActionKind.AdvancePastStart), new(AutoSessionActionKind.Confirm)]);
        var target = new AutoSessionStalePreviewTarget();
        var result = await StatefulFuzzRunner.RunAsync(scenario, target);
        Assert.False(result.Failed, Describe(result));
        Assert.Equal(1, target.LastState!.RejectedConfirmations);
        Assert.Equal(0, target.LastState.SuccessfulConfirmations);
    }

    [Fact]
    public async Task Temporal_mutations_never_allow_a_preview_to_create_after_its_selected_start_time()
    {
        var temporalSeed = new AutoSessionAction[]
        {
            new(AutoSessionActionKind.Preview),
            new(AutoSessionActionKind.AdvancePastStart),
            new(AutoSessionActionKind.Confirm)
        };
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(temporalSeed, seed, CreateTemporalAction, operationCount: 5);
            var scenario = new StatefulFuzzCase<AutoSessionAction>($"auto-session-delayed-confirm-{seed}", seed, actions);
            var target = new AutoSessionStalePreviewTarget();
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            Assert.False(result.Failed, Describe(result));
        }
    }

    [Fact]
    public void Execution_window_boundary_accepts_future_and_rejects_equal_or_past_start()
    {
        var candidate = ZaloPollScheduleParser.ExtractSchedule(BuildPoll(OriginalOption), new ZaloTrackedGroupData(), PreviewNow).Candidates.Single();
        ZaloAutoSessionActionExecutor.EnsureCandidatesStillUpcoming([candidate], candidate.StartTime.AddTicks(-1));
        Assert.Throws<InvalidOperationException>(() => ZaloAutoSessionActionExecutor.EnsureCandidatesStillUpcoming([candidate], candidate.StartTime));
        Assert.Throws<InvalidOperationException>(() => ZaloAutoSessionActionExecutor.EnsureCandidatesStillUpcoming([candidate], candidate.StartTime.AddMinutes(1)));
    }

    private static AutoSessionAction CreateAction(StableFuzzRandom random) => new((AutoSessionActionKind)random.NextInt(Enum.GetValues<AutoSessionActionKind>().Length));
    private static AutoSessionAction CreateTemporalAction(StableFuzzRandom random) => new(random.NextBool() ? AutoSessionActionKind.AdvancePastStart : AutoSessionActionKind.Confirm);

    private static string Describe(StatefulFuzzRunResult<AutoSessionAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum AutoSessionActionKind { Preview, EditPoll, RestorePoll, AdvancePastStart, Confirm }
    internal sealed record AutoSessionAction(AutoSessionActionKind Kind);

    internal sealed class AutoSessionFuzzState
    {
        public string OptionText { get; set; } = OriginalOption;
        public DateTimeOffset ExecutionNow { get; set; } = PreviewNow;
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

        public ValueTask ApplyAsync(AutoSessionFuzzState state, AutoSessionAction action, int actionIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (action.Kind)
            {
                case AutoSessionActionKind.Preview:
                    var extraction = ZaloPollScheduleParser.ExtractSchedule(BuildPoll(state.OptionText), new ZaloTrackedGroupData(), state.ExecutionNow);
                    state.PreviewCandidate = extraction.Candidates.SingleOrDefault();
                    state.LastConfirmationAccepted = null;
                    state.LastConfirmationExpectedFresh = null;
                    break;
                case AutoSessionActionKind.EditPoll:
                    state.OptionText = EditedOption;
                    break;
                case AutoSessionActionKind.RestorePoll:
                    state.OptionText = OriginalOption;
                    break;
                case AutoSessionActionKind.AdvancePastStart:
                    state.ExecutionNow = MatchStart.AddMinutes(1);
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
            if (state.LastConfirmationAccepted is not null && state.LastConfirmationExpectedFresh is not null && state.LastConfirmationAccepted != state.LastConfirmationExpectedFresh)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    state.PreviewCandidate is not null && state.ExecutionNow >= state.PreviewCandidate.StartTime ? "expired-preview-must-not-execute" : "preview-provenance-must-match-current-poll",
                    $"confirmation accepted={state.LastConfirmationAccepted} while expectedFresh={state.LastConfirmationExpectedFresh}; now={state.ExecutionNow:O}; start={state.PreviewCandidate?.StartTime:O}");
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
            var sourceFresh = ZaloPollScheduleParser.ValidateCandidateConsistency(poll, state.PreviewCandidate, out _);
            state.LastConfirmationExpectedFresh = sourceFresh && state.PreviewCandidate.StartTime > state.ExecutionNow;
            try
            {
                ZaloAutoSessionActionExecutor.EnsureCandidatesMatchPollSource(poll, [state.PreviewCandidate]);
                ZaloAutoSessionActionExecutor.EnsureCandidatesStillUpcoming([state.PreviewCandidate], state.ExecutionNow);
                state.LastConfirmationAccepted = true;
                state.SuccessfulConfirmations += 1;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("auto_session_candidate_source_mismatch:", StringComparison.Ordinal) ||
                exception.Message.StartsWith("auto_session_candidate_start_elapsed:", StringComparison.Ordinal))
            {
                state.LastConfirmationAccepted = false;
                state.RejectedConfirmations += 1;
            }
        }
    }

    private static readonly DateTimeOffset Created = new(2026, 9, 13, 22, 30, 0, VietnamOffset);
    private static readonly DateTimeOffset PreviewNow = new(2026, 9, 13, 23, 50, 0, VietnamOffset);
    private static readonly DateTimeOffset MatchStart = new(2026, 9, 14, 0, 5, 0, VietnamOffset);
    private const string OriginalOption = "T2 14/9 00:05";
    private const string EditedOption = "T2 21/9 00:05";

    private static BridgePoll BuildPoll(string optionText) => new(
        "poll-fuzz-auto-session-stale-preview", "Vote sân UTE. Max 18 slots/sân. 00:05-02:00", "leader-1",
        [new BridgePollOption("o1", optionText, 2, [])], true, false, false, false, 2,
        Created.ToUnixTimeMilliseconds(), Created.ToUnixTimeMilliseconds(), 0);
}
