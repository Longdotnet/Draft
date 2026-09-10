using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamResultRecoveryPolicyTests
{
    [Fact]
    public void Unresolved_pass_slot_reports_exact_grounded_count_and_deterministic_next_actions()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.UnresolvedPassSlots,
            "draft_blocked_pass_slot_unresolved",
            effectiveSlots: 17,
            capacity: 18) with
        {
            ActivePassSlotRiskCount = 2
        };

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("còn 2 suất đang nhường/chờ nhận chưa hoàn tất", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ pass", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`xong`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 10 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("`xác nhận draft`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần @Npc lại", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReasonCode", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("draft_blocked", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_players_reports_authoritative_effective_count_and_shortfall()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.RosterNotFull,
            "draft_blocked_roster_not_full",
            effectiveSlots: 16,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("16/18", message, StringComparison.Ordinal);
        Assert.Contains("thiếu 2 người/chỗ", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 4 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("trả lời chính tin đó", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Over_capacity_reports_authoritative_excess_instead_of_calling_the_roster_ready()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.RosterOverCapacity,
            "draft_blocked_roster_over_capacity",
            effectiveSlots: 19,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("19/18", message, StringComparison.Ordinal);
        Assert.Contains("dư 1 người/chỗ", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("đã đủ người và hồ sơ để chia đội", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_profiles_teaches_self_reply_or_exact_mention_instead_of_bare_display_name_mutation()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.MissingProfiles,
            "draft_blocked_missing_profile",
            effectiveSlots: 18,
            capacity: 18,
            missingNames: ["To An", "Nick Tran"]);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("To An", message, StringComparison.Ordinal);
        Assert.Contains("Nick Tran", message, StringComparison.Ordinal);
        Assert.Contains("trả lời chính tin NPC hỏi", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`nam`", message, StringComparison.Ordinal);
        Assert.Contains("không cần @Npc", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`@Npc cập nhật @Tên: nam`", message, StringComparison.Ordinal);
        Assert.Contains("phải tag đúng người", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không dùng display name trần", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc cập nhật To An: nam", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc cập nhật Nick Tran: nam", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("xác nhận draft", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_missing_display_names_never_become_an_authoritative_update_target()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.MissingProfiles,
            "draft_blocked_missing_profile",
            effectiveSlots: 18,
            capacity: 18,
            missingNames: ["Minh", "Minh"]);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("Minh", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc cập nhật @Tên: nam`", message, StringComparison.Ordinal);
        Assert.Contains("tag đúng người", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc cập nhật Minh: nam", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_state_tells_authorized_user_the_next_action_without_forcing_a_diagnostic_round_trip()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.Ready,
            "draft_ready",
            effectiveSlots: 18,
            capacity: 18,
            canEscalate: true);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("đã đủ người và hồ sơ để chia đội", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 10 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("trả lời chính tin đó bằng `xác nhận draft`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cảnh báo trước khi chia đội", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`huỷ`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần @Npc lại", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NPC sẽ kiểm dữ liệu backend thật và nói đúng blocker hiện tại", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_readiness_keeps_complete_no_ai_escape_hatch_and_identity_safe_profile_guidance()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9");

        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("trả lời chính tin đó bằng `xác nhận draft`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cảnh báo trước khi chia đội", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`huỷ`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần @Npc lại", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 4 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("người được NPC hỏi", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`nam`", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc cập nhật @Tên: nam`", message, StringComparison.Ordinal);
        Assert.Contains("phải tag đúng người", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc cập nhật Nick Tran: nam", message, StringComparison.Ordinal);
        Assert.Contains("huỷ pass", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 10 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("AI có tắt", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handler deterministic", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_roster_state_uses_plain_client_language_instead_of_internal_product_terms()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.NoRoster,
            "draft_blocked_roster_empty",
            effectiveSlots: 0,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("Danh sách hiện chưa có người chơi nào", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cập nhật lại vote đúng trận", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 4 CN 13/9", message, StringComparison.Ordinal);
        Assert.Contains("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("roster", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective slot", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsafe_session_name_falls_back_to_bare_commands_and_never_splices_address_tokens()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.Ready,
            "draft_ready",
            effectiveSlots: 18,
            capacity: 18,
            canEscalate: true);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN @Npc 13/9", readiness);

        Assert.Contains("`@Npc 9`", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10`", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 9 CN @Npc", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 10 CN @Npc", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Draft_in_progress_never_exposes_partial_team_preview_or_tells_user_to_redraft()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.InvalidStatus,
            "draft_blocked_draft_in_progress",
            effectiveSlots: 18,
            capacity: 18);
        var partialTeams = new[]
        {
            new TeamPreviewResponse(
                "team-a",
                "Team A",
                "Captain A",
                [new TeamSlotPreviewResponse(
                    "slot-1",
                    "Player A",
                    DraftSlotType.Single,
                    PlayerGender.Male,
                    false,
                    1)])
        };

        var result = ZaloTeamLineupFormatter.Format("CN 13/9", partialTeams, readiness: readiness);

        Assert.Contains("đang chia đội", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Đội hình CN 13/9:", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Player A", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 9 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.Empty(result.Mentions);
    }

    [Fact]
    public void Finished_without_persisted_result_fails_closed_instead_of_inventing_a_team_or_redraft_action()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.InvalidStatus,
            "draft_blocked_finished_without_team_result",
            effectiveSlots: 18,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("đánh dấu đã kết thúc", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có kết quả đội đầy đủ", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@Npc 9 CN 13/9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("draft lại", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Persisted_result_with_empty_projection_does_not_tell_users_to_run_npc9_again()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.AlreadyDrafted,
            "draft_already_exists",
            effectiveSlots: 18,
            capacity: 18,
            hasTeams: true);

        var result = ZaloTeamLineupFormatter.Format("CN 13/9", [], readiness: readiness);

        Assert.Contains("đã có kết quả chia team trong hệ thống", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 10 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@Npc 9 CN 13/9", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("chưa có kết quả chia team chính thức", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloDraftReadinessSnapshot Snapshot(
        ZaloDraftReadinessState state,
        string reasonCode,
        int effectiveSlots,
        int capacity,
        IReadOnlyList<string>? missingNames = null,
        bool hasTeams = false,
        bool canEscalate = false)
    {
        var names = missingNames ?? [];
        return new ZaloDraftReadinessSnapshot(
            "session-1",
            "CN 13/9",
            "admin-1",
            "connection-1",
            "group-1",
            DateTimeOffset.UtcNow.AddDays(1),
            effectiveSlots,
            effectiveSlots,
            capacity,
            names.Count,
            names,
            hasTeams,
            true,
            "fingerprint",
            state,
            reasonCode,
            effectiveSlots == capacity && names.Count == 0,
            canEscalate);
    }
}
