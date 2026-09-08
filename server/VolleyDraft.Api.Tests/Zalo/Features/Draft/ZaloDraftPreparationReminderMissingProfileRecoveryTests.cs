using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationReminderMissingProfileRecoveryTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public void LockedPartialListWithMissingProfiles_TeachesSelfServiceAndOrganizerRecoveryWithoutInvitingDraft()
    {
        var readiness = Snapshot(
            presentPlayers: 15,
            effectiveSlots: 15,
            capacity: 18,
            state: ZaloDraftReadinessState.MissingProfiles,
            fingerprint: "fp-15",
            missingNames: ["An", "Bình"]);
        var decision = Decision(ZaloDraftPreparationDecisionKind.PlayCurrentRoster, "fp-15", 15);

        var message = Build(readiness, decision);

        Assert.NotNull(message);
        Assert.Contains("An, Bình", message!);
        Assert.Contains("`nam`", message);
        Assert.Contains("`thủ`", message);
        Assert.Contains("`mới chơi`", message);
        Assert.Contains("không cần @Npc", message);
        Assert.Contains("`@Npc cập nhật @Tên: nam, công, trung bình`", message);
        Assert.Contains("phải tag đúng người", message);
        Assert.DoesNotContain("`draft đi`", message);
        AssertBeginnerLanguage(message);
    }

    [Fact]
    public void FullListWithMissingProfiles_TeachesSameDeterministicRecoveryInsteadOfDeadEndCopy()
    {
        var readiness = Snapshot(
            presentPlayers: 18,
            effectiveSlots: 18,
            capacity: 18,
            state: ZaloDraftReadinessState.MissingProfiles,
            fingerprint: "fp-18",
            missingNames: ["Chi"]);

        var message = Build(readiness);

        Assert.NotNull(message);
        Assert.Contains("Chi", message!);
        Assert.Contains("`nam`", message);
        Assert.Contains("`@Npc cập nhật @Tên: nam, công, trung bình`", message);
        Assert.DoesNotContain("Bổ sung nốt", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`draft đi`", message);
        AssertBeginnerLanguage(message);
    }

    private static void AssertBeginnerLanguage(string message)
    {
        var forbidden = new[]
        {
            "effective slot",
            "roster",
            "sync",
            "delta",
            "auto-draft",
            "shared/rotation",
            "over-slot",
            "fingerprint",
            "backend"
        };

        foreach (var term in forbidden)
            Assert.DoesNotContain(term, message, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Build(
        ZaloDraftReadinessSnapshot readiness,
        ZaloDraftPreparationDecisionSnapshot? decision = null) =>
        ZaloLeaderAwareDraftReminderPolicy.BuildMessage(
            Session(),
            readiness,
            decision,
            decisionWasStale: false,
            staleDecisionSlotCount: null,
            previousObservedSlotCount: null,
            activeSlotRiskCount: 0,
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
        IReadOnlyList<string> missingNames) =>
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
            MissingProfileCount: missingNames.Count,
            MissingProfileNames: missingNames,
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: fingerprint,
            State: state,
            ReasonCode: "draft_blocked_missing_profiles",
            IsRosterReady: false,
            CanEscalate: false);
}
