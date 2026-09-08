using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloRosterChangeCoordinatorPolicyTests
{
    [Fact]
    public void FirstObservation_IsBaseline_NotDrop()
    {
        var now = DateTimeOffset.UtcNow;
        var result = ZaloRosterChangeCoordinatorPolicy.Observe(
            null, "s1", 18, 18, "fp18", now, TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.Baseline, result.Kind);
        Assert.Equal(18, result.State.StableEffectiveSlotCount);
        Assert.False(result.State.HasUnnotifiedDrop);
    }

    [Fact]
    public void EighteenToSeventeen_IsConfirmedOnlyAfterDebounce()
    {
        var start = DateTimeOffset.UtcNow;
        var baseline = Baseline(18, start);

        var pending = ZaloRosterChangeCoordinatorPolicy.Observe(
            baseline, "s1", 17, 17, "fp17", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var confirmed = ZaloRosterChangeCoordinatorPolicy.Observe(
            pending.State, "s1", 17, 17, "fp17", start.AddMinutes(3.1), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.DropPending, pending.Kind);
        Assert.Equal(ZaloRosterObservationTransitionKind.DropConfirmed, confirmed.Kind);
        Assert.Equal(18, confirmed.DropFrom);
        Assert.Equal(17, confirmed.DropTo);
        Assert.True(confirmed.State.HasUnnotifiedDrop);
        Assert.True(ZaloRosterChangeCoordinatorPolicy.IsFullRosterBreak(18, 17, 18));
    }

    [Fact]
    public void AccidentalDropThatRecovers_IsBouncedWithoutNotification()
    {
        var start = DateTimeOffset.UtcNow;
        var pending = ZaloRosterChangeCoordinatorPolicy.Observe(
            Baseline(15, start), "s1", 14, 14, "fp14", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var bounced = ZaloRosterChangeCoordinatorPolicy.Observe(
            pending.State, "s1", 15, 15, "fp15b", start.AddMinutes(1.5), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.DropBounced, bounced.Kind);
        Assert.Null(bounced.State.PendingDropStartedAt);
        Assert.False(bounced.State.HasUnnotifiedDrop);
    }

    [Fact]
    public void MultipleDropsInsideDebounce_AreCoalescedFromOriginalStableCount()
    {
        var start = DateTimeOffset.UtcNow;
        var first = ZaloRosterChangeCoordinatorPolicy.Observe(
            Baseline(15, start), "s1", 14, 14, "fp14", start.AddMinutes(.5), TimeSpan.FromMinutes(2));
        var second = ZaloRosterChangeCoordinatorPolicy.Observe(
            first.State, "s1", 13, 13, "fp13", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var confirmed = ZaloRosterChangeCoordinatorPolicy.Observe(
            second.State, "s1", 13, 13, "fp13", start.AddMinutes(3), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.DropConfirmed, confirmed.Kind);
        Assert.Equal(15, confirmed.DropFrom);
        Assert.Equal(13, confirmed.DropTo);
    }

    [Fact]
    public void SameCountFingerprintReplacement_IsNotRosterDrop()
    {
        var start = DateTimeOffset.UtcNow;
        var result = ZaloRosterChangeCoordinatorPolicy.Observe(
            Baseline(15, start), "s1", 15, 15, "different-fingerprint", start.AddMinutes(2), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.Unchanged, result.Kind);
        Assert.Equal("different-fingerprint", result.State.StableFingerprint);
        Assert.False(result.State.HasUnnotifiedDrop);
    }

    [Fact]
    public void IncreaseClearsAnUnsentStaleDrop_WhenOriginalCountIsRestored()
    {
        var start = DateTimeOffset.UtcNow;
        var pending = ZaloRosterChangeCoordinatorPolicy.Observe(
            Baseline(18, start), "s1", 17, 17, "fp17", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var confirmed = ZaloRosterChangeCoordinatorPolicy.Observe(
            pending.State, "s1", 17, 17, "fp17", start.AddMinutes(3), TimeSpan.FromMinutes(2));
        Assert.True(confirmed.State.HasUnnotifiedDrop);

        var recovered = ZaloRosterChangeCoordinatorPolicy.Observe(
            confirmed.State, "s1", 18, 18, "fp18b", start.AddMinutes(4), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.Recovered, recovered.Kind);
        Assert.False(recovered.State.HasUnnotifiedDrop);
    }

    [Fact]
    public void PartialRecovery_PreservesConfirmedIncidentUntilOriginalCountReturns()
    {
        var start = DateTimeOffset.UtcNow;
        var first = ZaloRosterChangeCoordinatorPolicy.Observe(
            Baseline(18, start), "s1", 16, 16, "fp16", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var confirmed = ZaloRosterChangeCoordinatorPolicy.Observe(
            first.State, "s1", 16, 16, "fp16", start.AddMinutes(3), TimeSpan.FromMinutes(2));
        var notified = confirmed.State with { LastDropNotifiedAt = start.AddMinutes(3.1) };

        var partial = ZaloRosterChangeCoordinatorPolicy.Observe(
            notified, "s1", 17, 17, "fp17", start.AddMinutes(4), TimeSpan.FromMinutes(2));
        var recovered = ZaloRosterChangeCoordinatorPolicy.Observe(
            partial.State, "s1", 18, 18, "fp18", start.AddMinutes(5), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.Increased, partial.Kind);
        Assert.Equal(18, partial.State.LastDropFromCount);
        Assert.Equal(16, partial.State.LastDropToCount);
        Assert.NotNull(partial.State.LastDropNotifiedAt);
        Assert.Equal(ZaloRosterObservationTransitionKind.Recovered, recovered.Kind);
        Assert.Equal(17, recovered.DropFrom);
        Assert.Equal(18, recovered.DropTo);
        Assert.Null(recovered.State.LastDropAt);
        Assert.Null(recovered.State.LastDropNotifiedAt);
    }

    [Fact]
    public void RecoveredReady_IsAnnouncedOnlyAfterAUserVisibleConfirmedIncident()
    {
        var start = DateTimeOffset.UtcNow;
        var previous = Baseline(17, start) with
        {
            LastDropAt = start.AddMinutes(-2),
            LastDropNotifiedAt = start.AddMinutes(-1),
            LastDropFromCount = 18,
            LastDropToCount = 17
        };
        var transition = ZaloRosterChangeCoordinatorPolicy.Observe(
            previous, "s1", 18, 18, "fp18", start, TimeSpan.FromMinutes(2));
        var ready = ReadySnapshot() with { ActivePassSlotRiskCount = 0 };

        Assert.True(ZaloRosterChangeCoordinatorPolicy.ShouldAnnounceRecoveredReady(
            previous,
            transition,
            ready));
        Assert.False(ZaloRosterChangeCoordinatorPolicy.ShouldAnnounceRecoveredReady(
            previous with { LastDropNotifiedAt = null },
            transition,
            ready));
        Assert.False(ZaloRosterChangeCoordinatorPolicy.ShouldAnnounceRecoveredReady(
            previous,
            transition,
            ready with
            {
                State = ZaloDraftReadinessState.UnresolvedPassSlots,
                ActivePassSlotRiskCount = 1,
                IsRosterReady = false,
                CanEscalate = false
            }));
    }

    [Fact]
    public void RecoveredReadyMessage_ClosesRecruitmentAndTeachesTheNextDeterministicAction()
    {
        var message = ZaloRosterChangeCoordinatorPolicy.BuildRecoveredReadyUpdate(
            ReadySnapshot(),
            17,
            18);

        Assert.Contains("đủ lại 18/18", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Không còn chỗ đang nhường/chờ nhận", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ngưng gọi thêm", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draft đi", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đọc lại vote lần cuối", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObservationStore_PersistsAndCanBeExplicitlyReset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloRecruitmentRosterObservationStore(db);
        var state = Baseline(18, DateTimeOffset.UtcNow) with
        {
            PendingDropFromCount = 18,
            PendingDropToCount = 17,
            PendingDropStartedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(state);
        var loaded = await store.GetAsync("s1");

        Assert.NotNull(loaded);
        Assert.Equal(18, loaded!.PendingDropFromCount);
        Assert.Equal(17, loaded.PendingDropToCount);
        Assert.Equal(1, await store.DeleteAsync("s1"));
        Assert.Null(await store.GetAsync("s1"));
    }

    private static ZaloRecruitmentRosterObservation Baseline(int count, DateTimeOffset now) => new(
        "s1",
        count,
        count,
        $"fp{count}",
        null,
        null,
        null,
        now,
        null,
        null,
        null,
        null,
        now);

    private static ZaloDraftReadinessSnapshot ReadySnapshot() => new(
        SessionId: "s1",
        SessionName: "CN 13/9",
        AdminUserId: "admin",
        ZaloConnectionId: "conn-1",
        GroupId: "g1",
        StartTime: DateTimeOffset.UtcNow.AddHours(4),
        PresentPlayerCount: 18,
        EffectiveSlotCount: 18,
        Capacity: 18,
        MissingProfileCount: 0,
        MissingProfileNames: [],
        HasTeams: false,
        HasLinkedPoll: true,
        Fingerprint: "fp18",
        State: ZaloDraftReadinessState.Ready,
        ReasonCode: "draft_ready",
        IsRosterReady: true,
        CanEscalate: true);
}
