using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionDateTimeMergeFuzzTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, VietnamOffset);
    private static readonly AutoSessionAction[] SeedActions =
    [
        new(AutoSessionActionKind.OrganizerTimeOverride),
        new(AutoSessionActionKind.MovePollDate),
        new(AutoSessionActionKind.Revalidate)
    ];

    [Fact]
    public async Task Poll_owned_date_survives_organizer_time_override_after_identity_change()
    {
        var scenario = new StatefulFuzzCase<AutoSessionAction>(
            "auto-session-date-time-merge-production-shape",
            20260909,
            SeedActions);
        var target = new DateTimeMergeTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        var revalidation = Assert.IsType<ZaloAutoSessionPollRevalidationV4>(target.LastState!.LastRevalidation);
        Assert.True(revalidation.RequiresOrganizerConfirmation);
        var item = Assert.Single(revalidation.Reconciliation.Draft.Items);
        Assert.Equal(new DateTime(2026, 9, 13), item.StartTime.ToOffset(VietnamOffset).Date);
        Assert.Equal(new TimeSpan(18, 0, 0), item.StartTime.ToOffset(VietnamOffset).TimeOfDay);
    }

    [Fact]
    public async Task Stateful_sequence_mutations_never_mix_stale_calendar_date_with_current_poll_identity()
    {
        for (var seed = 1; seed <= 192; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedActions,
                seed,
                CreateAction,
                operationCount: 8);
            var scenario = new StatefulFuzzCase<AutoSessionAction>(
                $"auto-session-date-time-merge-{seed}",
                seed,
                actions);
            var target = new DateTimeMergeTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
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
        OrganizerTimeOverride,
        MovePollDate,
        RestorePollDate,
        Revalidate
    }

    internal sealed record AutoSessionAction(AutoSessionActionKind Kind);

    internal sealed class DateTimeMergeState
    {
        public ZaloAutoSessionConversationDraft Source { get; } = Draft(
            Item("o1", "T6 11/9", "T6", 11, 17, 30, 8));
        public ZaloAutoSessionConversationDraft Durable { get; set; }
        public string OptionText { get; set; } = "T6 11/9";
        public ZaloAutoSessionPollRevalidationV4? LastRevalidation { get; set; }

        public DateTimeMergeState()
        {
            Durable = Source;
        }
    }

    internal sealed class DateTimeMergeTarget : IStatefulFuzzTarget<DateTimeMergeState, AutoSessionAction>
    {
        public string Name => "auto-session-date-time-merge";

        public DateTimeMergeState? LastState { get; private set; }

        public DateTimeMergeState CreateState(StatefulFuzzCase<AutoSessionAction> scenario)
        {
            LastState = new DateTimeMergeState();
            return LastState;
        }

        public ValueTask ApplyAsync(
            DateTimeMergeState state,
            AutoSessionAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action.Kind)
            {
                case AutoSessionActionKind.OrganizerTimeOverride:
                {
                    var item = Assert.Single(state.Durable.Items);
                    state.Durable = state.Durable with
                    {
                        Items = [item with { StartTime = At(11, 18, 0) }]
                    };
                    break;
                }
                case AutoSessionActionKind.MovePollDate:
                    state.OptionText = "CN 13/9";
                    break;
                case AutoSessionActionKind.RestorePollDate:
                    state.OptionText = "T6 11/9";
                    break;
                case AutoSessionActionKind.Revalidate:
                    state.LastRevalidation = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
                        Poll(state.OptionText),
                        Tracked(),
                        state.Source,
                        state.Durable,
                        Now);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            return ValueTask.CompletedTask;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(DateTimeMergeState state)
        {
            if (state.LastRevalidation is null || state.LastRevalidation.Issues.Count > 0)
                yield break;

            var current = state.LastRevalidation.CurrentSourceDraft.Items.SingleOrDefault(item => item.OptionId == "o1");
            var reconciled = state.LastRevalidation.Reconciliation.Draft.Items.SingleOrDefault(item => item.OptionId == "o1");
            if (current is null || reconciled is null)
                yield break;

            var currentLocal = current.StartTime.ToOffset(VietnamOffset);
            var reconciledLocal = reconciled.StartTime.ToOffset(VietnamOffset);
            if (currentLocal.Date != reconciledLocal.Date)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "poll-owned-date-must-survive-organizer-time-override",
                    $"poll date={currentLocal:yyyy-MM-dd}; reconciled date={reconciledLocal:yyyy-MM-dd}; option={current.OptionContent}");
            }
        }
    }

    private static ZaloTrackedGroupData Tracked() => new()
    {
        DefaultStartMinutes = 17 * 60 + 30,
        DefaultTeamSize = 6,
        DefaultLocation = "UTE",
        AssumePmForHourUnder12 = true
    };

    private static ZaloAutoSessionConversationDraft Draft(params ZaloAutoSessionConversationDraftItem[] items) =>
        new(items, "UTE", 6);

    private static ZaloAutoSessionConversationDraftItem Item(
        string id,
        string content,
        string dayKey,
        int day,
        int hour,
        int minute,
        int votes) =>
        new(id, content, dayKey, At(day, hour, minute), votes, true);

    private static BridgePoll Poll(string optionText) => new(
        "poll-date-time-merge",
        "Vote sân UTE",
        "organizer-1",
        [new BridgePollOption("o1", optionText, 9, [])],
        true,
        false,
        false,
        false,
        9,
        Now.AddDays(-2).ToUnixTimeMilliseconds(),
        Now.ToUnixTimeMilliseconds(),
        Now.AddDays(7).ToUnixTimeMilliseconds());

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, VietnamOffset);
}
