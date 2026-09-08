using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloProfileUpdateReadinessCopyTests
{
    [Fact]
    public void Ready_invites_authorized_draft_after_grounded_recheck()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(Snapshot(ZaloDraftReadinessState.Ready));

        Assert.Contains("draft đi", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kiểm lại dữ liệu thật", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("đủ điều kiện để draft", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unresolved_pass_teaches_deterministic_recovery_before_draft()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(
            Snapshot(ZaloDraftReadinessState.UnresolvedPassSlots) with { ActivePassSlotRiskCount = 2 });

        Assert.Contains("2 chỗ", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ pass", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xong", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_people_teaches_leader_decision_before_draft()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(
            Snapshot(ZaloDraftReadinessState.RosterNotFull, effectiveSlots: 16, capacity: 18));

        Assert.Contains("16/18", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("còn thiếu 2", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 4", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vẫn đánh", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chốt 16", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kiếm thêm", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ sau", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draft đi", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Over_capacity_does_not_invite_draft_before_roster_is_fixed()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(
            Snapshot(ZaloDraftReadinessState.RosterOverCapacity, effectiveSlots: 20, capacity: 18));

        Assert.Contains("20/18", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dư 2", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc 4", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đừng chạy `draft đi`", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_roster_teaches_recruitment_instead_of_dead_end()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(
            Snapshot(ZaloDraftReadinessState.NoRoster, effectiveSlots: 0, capacity: 18));

        Assert.Contains("@Npc 4", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kiếm thêm", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nói `draft đi`", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remaining_profiles_keep_recovery_on_profile_workflow()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(
            Snapshot(
                ZaloDraftReadinessState.MissingProfiles,
                missingNames: ["An", "Bình"]));

        Assert.Contains("An", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bình", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần @Npc", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc cập nhật @Tên", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("draft đi", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Already_drafted_never_suggests_redraft_after_profile_edit()
    {
        var message = ZaloProfileUpdateReadinessCopy.Build(Snapshot(ZaloDraftReadinessState.AlreadyDrafted));

        Assert.Contains("@Npc 10", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không chạy lại", message, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloDraftReadinessSnapshot Snapshot(
        ZaloDraftReadinessState state,
        int effectiveSlots = 18,
        int capacity = 18,
        IReadOnlyList<string>? missingNames = null)
    {
        missingNames ??= [];
        return new ZaloDraftReadinessSnapshot(
            "session-1",
            "CN 13/9",
            "admin-1",
            "connection-1",
            "group-1",
            DateTimeOffset.UtcNow.AddHours(6),
            effectiveSlots,
            effectiveSlots,
            capacity,
            missingNames.Count,
            missingNames,
            state == ZaloDraftReadinessState.AlreadyDrafted,
            true,
            "fingerprint",
            state,
            state switch
            {
                ZaloDraftReadinessState.InvalidStatus => "draft_blocked_invalid_status",
                _ => "test"
            },
            state == ZaloDraftReadinessState.Ready,
            state == ZaloDraftReadinessState.Ready);
    }
}
