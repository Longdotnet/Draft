using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPreparationReminderObservationTests
{
    [Fact]
    public void SameObservation_DoesNotReopenSameBucket()
    {
        var readiness = Snapshot(15, 15, 0, "fp-15");
        var previous = Previous(readiness, 0);

        Assert.False(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, readiness, 0));
    }

    [Fact]
    public void SameBucketRefresh_KeepsNormalObservationThrottle()
    {
        var readiness = Snapshot(15, 15, 0, "fp-15");
        var now = DateTimeOffset.UtcNow;
        var recent = Previous(readiness, 0) with { UpdatedAt = now.AddMinutes(-4) };
        var due = Previous(readiness, 0) with { UpdatedAt = now.AddMinutes(-5) };

        Assert.False(ZaloDraftPreparationReminderObservation.ShouldRefreshSameBucket(recent, now));
        Assert.True(ZaloDraftPreparationReminderObservation.ShouldRefreshSameBucket(due, now));
    }

    [Fact]
    public void PassSlotBlockedObservation_RefreshesOnNextHeavyCycle()
    {
        var readiness = Snapshot(18, 18, 0, "fp-18", ZaloDraftReadinessState.UnresolvedPassSlots);
        var now = DateTimeOffset.UtcNow;
        var beforeNextHeavyCycle = Previous(readiness, 1) with { UpdatedAt = now.AddSeconds(-119) };
        var nextHeavyCycle = Previous(readiness, 1) with { UpdatedAt = now.AddMinutes(-2) };

        Assert.False(ZaloDraftPreparationReminderObservation.ShouldRefreshSameBucket(beforeNextHeavyCycle, now));
        Assert.True(ZaloDraftPreparationReminderObservation.ShouldRefreshSameBucket(nextHeavyCycle, now));
    }

    [Fact]
    public void SlotDelta_ReopensSameBucket()
    {
        var before = Snapshot(15, 15, 0, "fp-15");
        var after = Snapshot(16, 16, 0, "fp-16");
        var previous = Previous(before, 0);

        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, after, 0));
    }

    [Fact]
    public void PassSlotRiskAppearingOrClearing_ReopensSameBucket()
    {
        var readiness = Snapshot(18, 18, 0, "fp-18", ZaloDraftReadinessState.Ready, canEscalate: true);
        var clean = Previous(readiness, 0);
        var risky = Previous(readiness, 1);

        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(clean, readiness, 1));
        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(risky, readiness, 0));
    }

    [Fact]
    public void ProfileCompletion_ReopensSameBucketEvenWhenRosterIsIdentical()
    {
        var missing = Snapshot(18, 18, 1, "fp-18", ZaloDraftReadinessState.MissingProfiles);
        var complete = Snapshot(18, 18, 0, "fp-18", ZaloDraftReadinessState.Ready, canEscalate: true);
        var previous = Previous(missing, 0);

        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, complete, 0));
    }

    [Fact]
    public void MissingProfileIdentityReplacement_ReopensSameBucketWhenCountIsUnchanged()
    {
        var before = Snapshot(
            18,
            18,
            1,
            "fp-18",
            ZaloDraftReadinessState.MissingProfiles,
            missingProfileNames: ["An"]);
        var after = Snapshot(
            18,
            18,
            1,
            "fp-18",
            ZaloDraftReadinessState.MissingProfiles,
            missingProfileNames: ["Bình"]);
        var previous = Previous(before, 0);

        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, after, 0));
        Assert.NotEqual(
            ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(before, 0),
            ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(after, 0));
    }

    [Fact]
    public void MissingProfileOrderingAndCase_DoNotManufactureAChange()
    {
        var before = Snapshot(
            18,
            18,
            2,
            "fp-18",
            ZaloDraftReadinessState.MissingProfiles,
            missingProfileNames: ["An", "Bình"]);
        var after = Snapshot(
            18,
            18,
            2,
            "fp-18",
            ZaloDraftReadinessState.MissingProfiles,
            missingProfileNames: [" bình ", "AN"]);
        var previous = Previous(before, 0);

        Assert.False(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, after, 0));
    }

    [Fact]
    public void SameCountRosterReplacement_ReopensSameBucket()
    {
        var before = Snapshot(15, 15, 0, "fp-a");
        var after = Snapshot(15, 15, 0, "fp-b");
        var previous = Previous(before, 0);

        Assert.True(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, after, 0));
    }

    [Fact]
    public void LegacyRosterFingerprint_IsUpgradedWithoutManufacturingAChange()
    {
        var readiness = Snapshot(15, 15, 0, "fp-15");
        var previous = new ZaloDraftPreparationReminderState(
            "session-1", "20260907-1200", 15, 0, "fp-15",
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.False(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, readiness, 0));
        Assert.True(ZaloDraftPreparationReminderObservation.NeedsSilentUpgrade(previous));
    }

    [Fact]
    public void PreviousObservationVersion_IsUpgradedWithoutDeploymentDuplicate()
    {
        var readiness = Snapshot(
            18,
            18,
            1,
            "fp-18",
            ZaloDraftReadinessState.MissingProfiles,
            missingProfileNames: ["An"]);
        var previous = new ZaloDraftPreparationReminderState(
            "session-1",
            "20260907-1200",
            readiness.EffectiveSlotCount,
            0,
            ZaloDraftPreparationReminderObservation.BuildPreviousVersionFingerprintForEval(readiness, 0),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.False(ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, readiness, 0));
        Assert.True(ZaloDraftPreparationReminderObservation.NeedsSilentUpgrade(previous));
    }

    [Fact]
    public void MaterialStateChangesUseDifferentOutboundIdempotencySuffixes()
    {
        var missing = Snapshot(18, 18, 1, "fp-18", ZaloDraftReadinessState.MissingProfiles);
        var ready = Snapshot(18, 18, 0, "fp-18", ZaloDraftReadinessState.Ready, canEscalate: true);

        Assert.NotEqual(
            ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(missing, 0),
            ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(ready, 0));
        Assert.Equal(24, ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(ready, 0).Length);
    }

    private static ZaloDraftPreparationReminderState Previous(
        ZaloDraftReadinessSnapshot readiness,
        int risks) =>
        new(
            "session-1",
            "20260907-1200",
            readiness.EffectiveSlotCount,
            risks,
            ZaloDraftPreparationReminderObservation.BuildFingerprint(readiness, risks),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-5));

    private static ZaloDraftReadinessSnapshot Snapshot(
        int present,
        int effective,
        int missingProfiles,
        string fingerprint,
        ZaloDraftReadinessState state = ZaloDraftReadinessState.RosterNotFull,
        bool canEscalate = false,
        IReadOnlyList<string>? missingProfileNames = null) =>
        new(
            SessionId: "session-1",
            SessionName: "CN 13/09 18:00",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.FromHours(7)),
            PresentPlayerCount: present,
            EffectiveSlotCount: effective,
            Capacity: 18,
            MissingProfileCount: missingProfiles,
            MissingProfileNames: missingProfiles == 0
                ? []
                : missingProfileNames ?? ["A"],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: fingerprint,
            State: state,
            ReasonCode: state == ZaloDraftReadinessState.Ready ? "draft_ready" : "draft_blocked",
            IsRosterReady: state == ZaloDraftReadinessState.Ready,
            CanEscalate: canEscalate);
}
