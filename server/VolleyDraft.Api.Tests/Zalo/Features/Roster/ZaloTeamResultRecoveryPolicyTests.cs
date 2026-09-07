using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamResultRecoveryPolicyTests
{
    [Fact]
    public void Empty_precreated_teams_are_not_authoritative_results()
    {
        var teams = new[]
        {
            new TeamPreviewResponse("a", "A", null, []),
            new TeamPreviewResponse("b", "B", null, []),
            new TeamPreviewResponse("c", "C", null, [])
        };

        Assert.False(ZaloTeamResultRecoveryPolicy.HasAuthoritativeTeamResult(teams));
    }

    [Fact]
    public void Any_assigned_slot_is_an_authoritative_result_for_card_delivery()
    {
        var teams = new[]
        {
            new TeamPreviewResponse("a", "A", null,
                [new TeamSlotPreviewResponse("slot", "Long", Models.DraftSlotType.Single, Models.PlayerGender.Unknown, false, 0)])
        };

        Assert.True(ZaloTeamResultRecoveryPolicy.HasAuthoritativeTeamResult(teams));
    }

    [Fact]
    public void No_roster_explains_the_actual_first_blocker()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", Snapshot(
            ZaloDraftReadinessState.NoRoster,
            present: 0,
            effective: 0,
            capacity: 16));

        Assert.Contains("chưa có người chơi", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("thiếu hồ sơ", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shared_roster_shortage_explains_people_vs_effective_slots_without_calling_it_an_error()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("T4", Snapshot(
            ZaloDraftReadinessState.RosterNotFull,
            present: 15,
            effective: 14,
            capacity: 16));

        Assert.Contains("14/16", message, StringComparison.Ordinal);
        Assert.Contains("thiếu 2 suất", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15 người", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dùng chung suất", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("share slot lỗi", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_profiles_names_the_grounded_people_and_teaches_supported_update_syntax()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("T6", Snapshot(
            ZaloDraftReadinessState.MissingProfiles,
            present: 16,
            effective: 16,
            capacity: 16,
            missingNames: ["Nick Tran", "An"]));

        Assert.Contains("2 hồ sơ", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nick Tran", message, StringComparison.Ordinal);
        Assert.Contains("An", message, StringComparison.Ordinal);
        Assert.Contains("@Npc cập nhật Nick Tran: nam", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 9 T6", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_state_tells_operator_the_next_two_exact_steps()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", Snapshot(
            ZaloDraftReadinessState.Ready,
            present: 16,
            effective: 16,
            capacity: 16));

        Assert.Contains("đã đủ 16/16", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`@Npc 9 CN 13/9`", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10 CN 13/9`", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Draft_in_progress_never_tells_user_to_start_a_second_draft()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("T4", Snapshot(
            ZaloDraftReadinessState.InvalidStatus,
            present: 16,
            effective: 16,
            capacity: 16,
            reason: "draft_blocked_draft_in_progress"));

        Assert.Contains("đang trong lúc draft", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 10 T4", message, StringComparison.Ordinal);
        Assert.DoesNotContain("`@Npc 9 T4`", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finished_without_preview_fails_closed_instead_of_claiming_a_card_exists()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("T4", Snapshot(
            ZaloDraftReadinessState.AlreadyDrafted,
            present: 16,
            effective: 16,
            capacity: 16));

        Assert.Contains("đã draft", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không gửi card rỗng", message, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloDraftReadinessSnapshot Snapshot(
        ZaloDraftReadinessState state,
        int present,
        int effective,
        int capacity,
        IReadOnlyList<string>? missingNames = null,
        string? reason = null) =>
        new(
            SessionId: "session-1",
            SessionName: "T4",
            AdminUserId: "admin",
            ZaloConnectionId: "connection",
            GroupId: "group",
            StartTime: DateTimeOffset.UtcNow.AddHours(6),
            PresentPlayerCount: present,
            EffectiveSlotCount: effective,
            Capacity: capacity,
            MissingProfileCount: missingNames?.Count ?? 0,
            MissingProfileNames: missingNames ?? [],
            HasTeams: false,
            HasLinkedPoll: true,
            Fingerprint: "fingerprint",
            State: state,
            ReasonCode: reason ?? "test",
            IsRosterReady: effective == capacity && (missingNames?.Count ?? 0) == 0,
            CanEscalate: state == ZaloDraftReadinessState.Ready);
}
