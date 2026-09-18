using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationReminderReadinessAuthorityTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public void ProductionPolicy_KeepsPassRiskInformationalAndStillOffersDraft()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.Ready, activePassRisks: 1, canEscalate: true);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("1 pass slot còn mở", message!);
        Assert.Contains("không chặn draft", message);
        Assert.Contains("`draft đi`", message);
        Assert.Contains("danh sách/vote hiện tại", message);
    }

    [Fact]
    public void MultiplePassOffers_DoNotTurnReadySnapshotIntoABlocker()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.Ready, activePassRisks: 2, canEscalate: true);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("2 pass slot còn mở", message!);
        Assert.Contains("không chặn draft", message);
        Assert.Contains("`draft đi`", message);
    }

    [Fact]
    public void ReadyReminder_StillOffersDraftWhenSnapshotHasNoPassRisk()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.Ready, activePassRisks: 0, canEscalate: true);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("18/18 chỗ", message!);
        Assert.Contains("`draft đi`", message);
        Assert.Contains("kiểm tra vote lần cuối", message);
    }

    private static string? BuildProduction(ZaloDraftReadinessSnapshot readiness) =>
        ZaloLeaderAwareDraftReminderPolicy.BuildMessage(
            Session(),
            readiness,
            decision: null,
            decisionWasStale: false,
            staleDecisionSlotCount: null,
            previousObservedSlotCount: null,
            urgent: false);

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

    private static ZaloDraftReadinessSnapshot Snapshot(
        ZaloDraftReadinessState state,
        int activePassRisks,
        bool canEscalate)
    {
        return new ZaloDraftReadinessSnapshot(
            SessionId: "session-1",
            SessionName: "T4 26/08 18:00",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: Local(2026, 8, 26, 18, 0),
            PresentPlayerCount: 18,
            EffectiveSlotCount: 18,
            Capacity: 18,
            MissingProfileCount: 0,
            MissingProfileNames: [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: "fp-18",
            State: state,
            ReasonCode: state == ZaloDraftReadinessState.Ready ? "draft_ready" : "test_state",
            IsRosterReady: state == ZaloDraftReadinessState.Ready,
            CanEscalate: canEscalate)
        {
            ActivePassSlotRiskCount = activePassRisks
        };
    }

    private static DateTimeOffset Local(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, VietnamOffset);
}
