using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Draft;

public sealed class ZaloDraftPreparationClientCopyTests
{
    [Fact]
    public void Keep_recruiting_ack_explains_state_without_internal_jargon()
    {
        var text = ZaloDraftPreparationClientCopy.KeepRecruiting(
            string.Empty,
            "CN 13/9",
            15,
            18);

        Assert.Contains("15/18 chỗ", text);
        Assert.Contains("số người/chỗ thay đổi", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Full_roster_keep_recruiting_ack_explains_pause_and_resume()
    {
        var text = ZaloDraftPreparationClientCopy.KeepRecruitingAlreadyFull("CN 13/9", 18, 18);

        Assert.Contains("18/18 chỗ", text);
        Assert.Contains("tạm ngưng gọi thêm", text);
        Assert.Contains("hụt chỗ", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Pass_risk_ack_teaches_real_deterministic_recovery_commands()
    {
        var text = ZaloDraftPreparationClientCopy.PassRisk("CN 13/9", 17, 18, 1);

        Assert.Contains("1 chỗ đang nhường/chờ nhận", text);
        Assert.Contains("`huỷ pass`", text);
        Assert.Contains("`xong`", text);
        Assert.Contains("`huỷ nhận`", text);
        Assert.Contains("`chốt 17`", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Locked_partial_roster_ack_points_directly_to_next_action()
    {
        var text = ZaloDraftPreparationClientCopy.Locked(
            string.Empty,
            "15 chỗ",
            3,
            5);

        Assert.Contains("3 đội x5", text);
        Assert.Contains("`draft đi`", text);
        Assert.Contains("đọc lại vote", text);
        Assert.Contains("quyền lần cuối", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Shared_player_count_is_explained_in_client_language()
    {
        var text = ZaloDraftPreparationClientCopy.PlayerCountLabel(16, 15);

        Assert.Equal("16 người, tính thành 15 chỗ để chia đội", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Non_even_ack_explains_what_human_can_fix_without_engine_terms()
    {
        var text = ZaloDraftPreparationClientCopy.NotEven(
            string.Empty,
            "16 chỗ",
            16,
            3);

        Assert.Contains("16 chỗ chưa chia đều được 3 đội", text);
        Assert.Contains("chỗ chơi chung/luân phiên", text);
        Assert.Contains("không tự bỏ hay thêm người", text);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Poll_refresh_failure_never_exposes_raw_provider_detail()
    {
        var text = ZaloDraftPreparationClientCopy.DraftVoteRefreshFailed("CN 13/9");

        Assert.Contains("chưa đọc lại được đúng vote", text);
        Assert.Contains("Tui chưa đổi gì", text);
        Assert.DoesNotContain("HTTP", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", text, StringComparison.OrdinalIgnoreCase);
        AssertBeginnerSafe(text);
    }

    [Fact]
    public void Decision_change_prefix_uses_human_language()
    {
        var previous = new ZaloDraftPreparationDecisionSnapshot(
            "session-1",
            ZaloDraftPreparationDecisionKind.PlayCurrentRoster,
            "fingerprint",
            15,
            "leader-1",
            "Long",
            "message-1",
            DateTimeOffset.UtcNow);

        var text = ZaloDraftPreparationClientCopy.BuildDecisionChangePrefix(
            previous,
            ZaloDraftPreparationDecisionKind.KeepRecruiting,
            null,
            "Long");

        Assert.Contains("Cập nhật theo Long", text);
        Assert.Contains("chơi với 15 chỗ hiện tại", text);
        Assert.Contains("tiếp tục kiếm thêm", text);
        AssertBeginnerSafe(text);
    }

    private static void AssertBeginnerSafe(string text)
    {
        foreach (var jargon in new[]
                 {
                     "effective slot",
                     "fingerprint",
                     "roster",
                     "sync",
                     "delta",
                     "auto-draft",
                     "over-slot",
                     "shared/rotation",
                     "backend",
                     "handler deterministic"
                 })
        {
            Assert.DoesNotContain(jargon, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
