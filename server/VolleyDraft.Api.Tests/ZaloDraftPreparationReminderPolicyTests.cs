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
    public void MissingOneAfterRosterDrop_UsesUrgentDropLanguage()
    {
        var readiness = Snapshot(
            effectiveSlots: 17,
            capacity: 18,
            state: ZaloDraftReadinessState.RosterNotFull,
            canEscalate: false);

        var message = ZaloDraftPreparationReminderPolicy.BuildMessage(
            readiness,
            previousSlotCount: 18,
            openOfferCount: 0,
            urgent: true);

        Assert.NotNull(message);
        Assert.Contains("tụt từ 18/18 xuống 17/18", message!);
        Assert.Contains("Còn đúng 1 slot", message);
    }

    [Fact]
    public void MissingThreeOrMore_SuggestsEarlyCourtDecision()
    {
        var readiness = Snapshot(
            effectiveSlots: 15,
            capacity: 18,
            state: ZaloDraftReadinessState.RosterNotFull,
            canEscalate: false);

        var message = ZaloDraftPreparationReminderPolicy.BuildMessage(
            readiness,
            previousSlotCount: 15,
            openOfferCount: 0,
            urgent: false);

        Assert.NotNull(message);
        Assert.Contains("huỷ sân sớm", message!);
        Assert.Contains("Thiếu 3", message);
    }

    [Fact]
    public void OpenPassSlot_BlocksDraftInvitationEvenWhenRosterStillFull()
    {
        var readiness = Snapshot(
            effectiveSlots: 18,
            capacity: 18,
            state: ZaloDraftReadinessState.Ready,
            canEscalate: true);

        var message = ZaloDraftPreparationReminderPolicy.BuildMessage(
            readiness,
            previousSlotCount: 18,
            openOfferCount: 1,
            urgent: false);

        Assert.NotNull(message);
        Assert.Contains("pass/huỷ", message!);
        Assert.Contains("Chưa draft vội", message);
        Assert.DoesNotContain("`draft đi`", message);
    }

    [Fact]
    public void ReadyRoster_InvitesExplicitDraftConfirmation()
    {
        var readiness = Snapshot(
            effectiveSlots: 18,
            capacity: 18,
            state: ZaloDraftReadinessState.Ready,
            canEscalate: true);

        var message = ZaloDraftPreparationReminderPolicy.BuildMessage(
            readiness,
            previousSlotCount: 18,
            openOfferCount: 0,
            urgent: false);

        Assert.NotNull(message);
        Assert.Contains("`draft đi`", message!);
        Assert.Contains("18/18", message);
    }

    private static DateTimeOffset Local(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, VietnamOffset);

    private static ZaloDraftReadinessSnapshot Snapshot(
        int effectiveSlots,
        int capacity,
        ZaloDraftReadinessState state,
        bool canEscalate) =>
        new(
            SessionId: "session-1",
            SessionName: "T4 26/08 18:00",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: Local(2026, 8, 26, 18, 0),
            PresentPlayerCount: effectiveSlots,
            EffectiveSlotCount: effectiveSlots,
            Capacity: capacity,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: "fingerprint",
            State: state,
            ReasonCode: state == ZaloDraftReadinessState.Ready ? "draft_ready" : "draft_blocked_roster_not_full",
            IsRosterReady: state == ZaloDraftReadinessState.Ready,
            CanEscalate: canEscalate);
}
