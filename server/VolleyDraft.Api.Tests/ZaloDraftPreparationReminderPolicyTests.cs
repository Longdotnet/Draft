using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationReminderPolicyTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Theory]
    [InlineData(12, 15, "20260826-1200")]
    [InlineData(14, 15, "20260826-1400")]
    [InlineData(16, 44, "20260826-1630")]
    [InlineData(17, 29, "20260826-1700")]
    public void DueBucket_UsesNoonTwoPmThenThirtyMinuteCadence(
        int hour,
        int minute,
        string expectedKey)
    {
        var start = Local(2026, 8, 26, 18, 0);
        var now = Local(2026, 8, 26, hour, minute);

        var bucket = ZaloDraftPreparationReminderPolicy.GetDueBucket(start, now, 30);

        Assert.NotNull(bucket);
        Assert.Equal(expectedKey, bucket!.Key);
    }

    [Fact]
    public void DueBucket_StopsAfterThirtyMinutesBeforeMatch()
    {
        var start = Local(2026, 8, 26, 18, 0);

        Assert.NotNull(ZaloDraftPreparationReminderPolicy.GetDueBucket(
            start,
            Local(2026, 8, 26, 17, 30),
            30));
        Assert.Null(ZaloDraftPreparationReminderPolicy.GetDueBucket(
            start,
            Local(2026, 8, 26, 17, 31),
            30));
    }

    [Fact]
    public void FifteenOfEighteen_Undecided_AsksLeaderInsteadOfSuggestingCancellation()
    {
        var readiness = Snapshot(15, 15, 18, ZaloDraftReadinessState.RosterNotFull, "fp-15");

        var message = Build(readiness);

        Assert.NotNull(message);
        Assert.Contains("3 team x5", message!);
        Assert.Contains("chốt 15", message);
        Assert.Contains("kiếm thêm", message);
        Assert.DoesNotContain("huỷ sân", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("huỷ kèo", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepRecruiting_DoesNotAskCancelQuestionAgain()
    {
        var readiness = Snapshot(15, 15, 18, ZaloDraftReadinessState.RosterNotFull, "fp-15");
        var decision = Decision(ZaloDraftPreparationDecisionKind.KeepRecruiting, null, null);

        var message = Build(readiness, decision: decision, previous: 14);

        Assert.NotNull(message);
        Assert.Contains("đã chốt tiếp tục kiếm thêm", message!);
        Assert.Contains("14/18 → 15/18", message);
        Assert.Contains("không hỏi huỷ/giữ sân lại", message);
    }

    [Fact]
    public void PlayCurrentFifteen_StopsRecruitPressureAndOffersThreeByFiveDraft()
    {
        var readiness = Snapshot(15, 15, 18, ZaloDraftReadinessState.RosterNotFull, "fp-15");
        var decision = Decision(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, "fp-15", 15);

        var message = Build(readiness, decision: decision);

        Assert.NotNull(message);
        Assert.Contains("3 team x5", message!);
        Assert.Contains("Không dí kiếm thêm nữa", message);
        Assert.Contains("`draft đi`", message);
    }

    [Fact]
    public void PlayCurrentSixteen_IsRememberedButNotAdvertisedAsAutoDraftable()
    {
        var readiness = Snapshot(16, 16, 18, ZaloDraftReadinessState.RosterNotFull, "fp-16");
        var decision = Decision(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, "fp-16", 16);

        var message = Build(readiness, decision: decision);

        Assert.NotNull(message);
        Assert.Contains("vẫn giữ quyết định chơi", message!);
        Assert.Contains("16 effective slot chưa chia đều", message);
        Assert.Contains("shared/rotation", message);
        Assert.DoesNotContain("cứu kèo", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawSixteenWithSharedSlotEffectiveFifteen_ShowsBothFactsAndCanDraftThreeByFive()
    {
        var readiness = Snapshot(16, 15, 18, ZaloDraftReadinessState.RosterNotFull, "fp-shared");
        var decision = Decision(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, "fp-shared", 15);

        var message = Build(readiness, decision: decision);

        Assert.NotNull(message);
        Assert.Contains("16 người / 15 effective slot", message!);
        Assert.Contains("3 team x5", message);
    }

    [Fact]
    public void StalePlayCurrentDecision_IsExplicitlyInvalidated()
    {
        var readiness = Snapshot(16, 16, 18, ZaloDraftReadinessState.RosterNotFull, "fp-16");

        var message = Build(
            readiness,
            decision: null,
            stale: true,
            staleCount: 15,
            previous: 15);

        Assert.NotNull(message);
        Assert.Contains("quyết định roster cũ hết hiệu lực", message!);
        Assert.Contains("15/18 → 16/18", message);
    }

    [Fact]
    public void ActivePassSlot_BlocksDraftInvitationEvenWhenRosterFull()
    {
        var readiness = Snapshot(18, 18, 18, ZaloDraftReadinessState.Ready, "fp-18", canEscalate: true);

        var message = Build(readiness, risks: 1);

        Assert.NotNull(message);
        Assert.Contains("pass/huỷ", message!);
        Assert.Contains("Chưa chốt draft", message);
        Assert.DoesNotContain("`draft đi`", message);
    }

    [Fact]
    public void ReadyFullRoster_StillInvitesDraftConfirmation()
    {
        var readiness = Snapshot(18, 18, 18, ZaloDraftReadinessState.Ready, "fp-18", canEscalate: true);

        var message = Build(readiness);

        Assert.NotNull(message);
        Assert.Contains("18/18", message!);
        Assert.Contains("`draft đi`", message);
    }

    [Theory]
    [InlineData("15 vẫn đánh", ZaloDraftPreparationDecisionKind.PlayCurrentRoster, 15)]
    [InlineData("chốt 15 nha", ZaloDraftPreparationDecisionKind.PlayCurrentRoster, 15)]
    [InlineData("cứ đánh đi", ZaloDraftPreparationDecisionKind.PlayCurrentRoster, null)]
    [InlineData("kiếm thêm đi", ZaloDraftPreparationDecisionKind.KeepRecruiting, null)]
    [InlineData("cứ kiếm thêm", ZaloDraftPreparationDecisionKind.KeepRecruiting, null)]
    [InlineData("huỷ kèo T4", ZaloDraftPreparationDecisionKind.StopMatch, null)]
    [InlineData("huy san di", ZaloDraftPreparationDecisionKind.StopMatch, null)]
    public void DecisionParser_RecognizesStrongLeaderLanguage(
        string text,
        ZaloDraftPreparationDecisionKind expectedKind,
        int? expectedCount)
    {
        var command = ZaloDraftPreparationDecisionPolicy.TryParse(text);

        Assert.NotNull(command);
        Assert.Equal(expectedKind, command!.Kind);
        Assert.Equal(expectedCount, command.RequestedSlotCount);
    }

    [Theory]
    [InlineData("huỷ slot thôi")]
    [InlineData("huy slot T4 nha")]
    [InlineData("15 người đang chơi")]
    [InlineData("tối nay đánh mấy giờ")]
    public void DecisionParser_DoesNotConfuseSlotOrOrdinaryChatWithMatchDecision(string text)
    {
        Assert.Null(ZaloDraftPreparationDecisionPolicy.TryParse(text));
    }

    [Theory]
    [InlineData(15, 3, true)]
    [InlineData(18, 3, true)]
    [InlineData(16, 3, false)]
    [InlineData(17, 3, false)]
    [InlineData(5, 3, false)]
    public void AutoDraftDivisibility_MatchesDraftEngineRule(int slots, int teams, bool expected)
    {
        Assert.Equal(expected, ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(slots, teams));
    }

    private static string? Build(
        ZaloDraftReadinessSnapshot readiness,
        ZaloDraftPreparationDecisionSnapshot? decision = null,
        bool stale = false,
        int? staleCount = null,
        int? previous = null,
        int risks = 0,
        bool urgent = false) =>
        ZaloLeaderAwareDraftReminderPolicy.BuildMessage(
            Session(),
            readiness,
            decision,
            stale,
            staleCount,
            previous,
            risks,
            urgent);

    private static MatchSession Session() => new()
    {
        Id = "session-1",
        Name = "T4 26/08 18:00",
        AdminUserId = "admin",
        TeamCount = 3,
        TeamSize = 6,
        StartTime = Local(2026, 8, 26, 18, 0),
        Status = SessionStatus.Setup
    };

    private static ZaloDraftPreparationDecisionSnapshot Decision(
        ZaloDraftPreparationDecisionKind kind,
        string? fingerprint,
        int? slots) =>
        new(
            "session-1",
            kind,
            fingerprint,
            slots,
            "leader-1",
            "Leader",
            "message-1",
            Local(2026, 8, 26, 12, 0),
            Local(2026, 8, 26, 12, 0));

    private static DateTimeOffset Local(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, VietnamOffset);

    private static ZaloDraftReadinessSnapshot Snapshot(
        int presentPlayers,
        int effectiveSlots,
        int capacity,
        ZaloDraftReadinessState state,
        string fingerprint,
        bool canEscalate = false) =>
        new(
            SessionId: "session-1",
            SessionName: "T4 26/08 18:00",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: Local(2026, 8, 26, 18, 0),
            PresentPlayerCount: presentPlayers,
            EffectiveSlotCount: effectiveSlots,
            Capacity: capacity,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: fingerprint,
            State: state,
            ReasonCode: state == ZaloDraftReadinessState.Ready ? "draft_ready" : "draft_blocked_roster_not_full",
            IsRosterReady: state == ZaloDraftReadinessState.Ready,
            CanEscalate: canEscalate);
}
