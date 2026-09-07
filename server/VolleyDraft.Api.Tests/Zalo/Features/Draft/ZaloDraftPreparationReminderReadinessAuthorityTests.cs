using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationReminderReadinessAuthorityTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public void ProductionPolicy_UsesPassRiskFromReadinessSnapshot()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.Ready, activePassRisks: 1, canEscalate: false);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("1 suất đang nhường/chờ nhận", message!);
        Assert.DoesNotContain("`draft đi`", message);
    }

    [Fact]
    public void PassRiskReminder_TeachesDeterministicRecoveryWithoutAi()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.UnresolvedPassSlots, activePassRisks: 2, canEscalate: false);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("2 suất đang nhường/chờ nhận", message!);
        Assert.Contains("`huỷ pass`", message);
        Assert.Contains("`xong`", message);
        Assert.Contains("`huỷ nhận`", message);
        Assert.Contains("trạng thái thật", message);
    }

    [Fact]
    public void ReadyReminder_StillOffersDraftWhenSnapshotHasNoPassRisk()
    {
        var readiness = Snapshot(ZaloDraftReadinessState.Ready, activePassRisks: 0, canEscalate: true);

        var message = BuildProduction(readiness);

        Assert.NotNull(message);
        Assert.Contains("18/18", message!);
        Assert.Contains("`draft đi`", message);
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
            ReasonCode: activePassRisks > 0 ? "draft_blocked_pass_slot_unresolved" : "draft_ready",
            IsRosterReady: activePassRisks == 0,
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
