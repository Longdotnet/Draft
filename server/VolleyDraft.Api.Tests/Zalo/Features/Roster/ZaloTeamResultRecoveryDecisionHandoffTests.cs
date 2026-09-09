using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamResultRecoveryDecisionHandoffTests
{
    [Fact]
    public void Partial_roster_teaches_the_same_grounded_play_or_recruit_decision_as_draft_preparation()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.RosterNotFull,
            "draft_blocked_roster_not_full",
            effectiveSlots: 16,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("16/18", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc 4 CN 13/9`", message, StringComparison.Ordinal);
        Assert.Contains("`vẫn đánh`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`chốt 16`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`kiếm thêm`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đọc lại vote", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dữ liệu mới", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`@Npc 9 CN 13/9`", message, StringComparison.Ordinal);
        Assert.Contains("`@Npc 10 CN 13/9`", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_roster_has_an_executable_recruitment_path_instead_of_telling_a_beginner_to_draft_again()
    {
        var readiness = Snapshot(
            ZaloDraftReadinessState.NoRoster,
            "draft_blocked_roster_empty",
            effectiveSlots: 0,
            capacity: 18);

        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9", readiness);

        Assert.Contains("`@Npc 4 CN 13/9`", message, StringComparison.Ordinal);
        Assert.Contains("`kiếm thêm`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không tự đoán rằng trận bị huỷ", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("khi đã có danh sách thật", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`@Npc 9 CN 13/9`", message, StringComparison.Ordinal);
        Assert.Contains("chỉ sau khi chia xong", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`@Npc 10 CN 13/9`", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_readiness_still_teaches_partial_roster_decision_without_inventing_a_count()
    {
        var message = ZaloTeamResultRecoveryPolicy.BuildNoResultMessage("CN 13/9");

        Assert.Contains("`vẫn đánh`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`chốt N`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ví dụ `chốt 16`", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`kiếm thêm`", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chốt 18", message, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloDraftReadinessSnapshot Snapshot(
        ZaloDraftReadinessState state,
        string reasonCode,
        int effectiveSlots,
        int capacity)
    {
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
            0,
            [],
            false,
            true,
            "fingerprint",
            state,
            reasonCode,
            false,
            false);
    }
}
