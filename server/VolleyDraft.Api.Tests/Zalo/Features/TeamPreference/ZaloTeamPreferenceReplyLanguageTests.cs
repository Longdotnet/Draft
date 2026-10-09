using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamPreferenceReplyLanguageTests
{
    [Theory]
    [InlineData("tui muốn chung team với To An", "Vietnamese")]
    [InlineData("저랑 To An 같은 팀으로 해줘", "Korean")]
    public void Script_detection_only_returns_language_when_unicode_is_unambiguous(string message, string expected)
    {
        Assert.Equal(expected, ZaloReplyLanguageDetector.TryDetectFromScript(message)?.ToString());
    }

    [Theory]
    [InlineData("hello bot")]
    [InlineData("good evening, are you still there?")]
    [InlineData("chao bot")]
    [InlineData("tui muon chung team voi To An")]
    public void Latin_only_text_is_left_for_semantic_language_classification(string message)
    {
        Assert.Null(ZaloReplyLanguageDetector.TryDetectFromScript(message));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("yes")]
    [InlineData("확인")]
    [InlineData("네")]
    public void Confirmation_accepts_english_and_korean(string message)
    {
        Assert.True(ZaloBotIntelligence.IsConfirmation(message));
        Assert.True(ZaloPendingTurnPolicy.IsStrongConfirmation(message));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("취소")]
    [InlineData("취소해")]
    public void Cancellation_accepts_english_and_korean(string message)
    {
        Assert.True(ZaloBotIntelligence.IsCancel(message));
    }

    [Fact]
    public void Same_team_preview_uses_english_commands_for_english_request()
    {
        var text = ZaloTeamPreferenceReplyText.Preview(
            ZaloReplyLanguage.English,
            "Friday 9/10",
            CreatePreview());

        Assert.Contains("same-team group", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@bot confirm", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@bot cancel", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mình", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_team_preview_and_apply_use_korean_for_korean_request()
    {
        var preview = ZaloTeamPreferenceReplyText.Preview(
            ZaloReplyLanguage.Korean,
            "금요일 9/10",
            CreatePreview());
        var applied = ZaloTeamPreferenceReplyText.Applied(
            ZaloReplyLanguage.Korean,
            "금요일 9/10",
            new TeamRelationshipApplyResult(
                ZaloTeamRelationshipKind.Together,
                ZaloTeamRelationshipOperation.Set,
                ["Đặng Thế Nguyên", "To An"],
                true));

        Assert.Contains("같은 팀", preview, StringComparison.Ordinal);
        Assert.Contains("@bot 확인", preview, StringComparison.Ordinal);
        Assert.Contains("같은 팀", applied, StringComparison.Ordinal);
        Assert.DoesNotContain("Đã ghi nhận", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void English_team_preference_error_paths_do_not_fall_back_to_vietnamese()
    {
        var sessionClarification = ZaloTeamPreferenceReplyText.SessionClarification(
            ZaloReplyLanguage.English,
            ["Friday 9/10", "Sunday 11/10"]);
        var previewFailed = ZaloTeamPreferenceReplyText.PreviewFailed(
            ZaloReplyLanguage.English,
            "Lỗi nội bộ tiếng Việt");
        var infeasible = ZaloTeamPreferenceReplyText.Infeasible(
            ZaloReplyLanguage.English,
            "Không thể xếp chung team");
        var applyFailed = ZaloTeamPreferenceReplyText.ApplyFailed(
            ZaloReplyLanguage.English,
            "Trạng thái đã thay đổi");
        var operatorRequired = ZaloTeamPreferenceReplyText.OperatorRequired(ZaloReplyLanguage.English);

        Assert.Contains("which match", sessionClarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Friday 9/10", sessionClarification, StringComparison.Ordinal);
        Assert.Contains("couldn't calculate", previewFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("can't be applied", infeasible, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't save", applyFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only the group leader", operatorRequired, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lỗi nội bộ", previewFailed, StringComparison.Ordinal);
        Assert.DoesNotContain("Không thể", infeasible, StringComparison.Ordinal);
        Assert.DoesNotContain("Trạng thái", applyFailed, StringComparison.Ordinal);
    }

    [Fact]
    public void Korean_team_preference_error_paths_do_not_fall_back_to_vietnamese()
    {
        var sessionClarification = ZaloTeamPreferenceReplyText.SessionClarification(
            ZaloReplyLanguage.Korean,
            ["금요일 9/10", "일요일 11/10"]);
        var previewFailed = ZaloTeamPreferenceReplyText.PreviewFailed(
            ZaloReplyLanguage.Korean,
            "Lỗi nội bộ tiếng Việt");
        var operatorVerification = ZaloTeamPreferenceReplyText.OperatorVerificationFailed(ZaloReplyLanguage.Korean);

        Assert.Contains("어느 경기", sessionClarification, StringComparison.Ordinal);
        Assert.Contains("금요일 9/10", sessionClarification, StringComparison.Ordinal);
        Assert.Contains("계산하지 못했어요", previewFailed, StringComparison.Ordinal);
        Assert.Contains("권한", operatorVerification, StringComparison.Ordinal);
        Assert.DoesNotContain("Lỗi nội bộ", previewFailed, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_pre_ai_fallbacks_are_localized_or_language_neutral()
    {
        Assert.Contains("doesn't have any match", ZaloCommonReplyText.NoActiveSessions(ZaloReplyLanguage.English), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("활성화된 경기", ZaloCommonReplyText.NoActiveSessions(ZaloReplyLanguage.Korean), StringComparison.Ordinal);
        Assert.Contains("chưa có trận", ZaloCommonReplyText.NoActiveSessions(ZaloReplyLanguage.Vietnamese), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wait a moment", ZaloCommonReplyText.RateLimited(ZaloReplyLanguage.English), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("잠시 후", ZaloCommonReplyText.RateLimited(ZaloReplyLanguage.Korean), StringComparison.Ordinal);
        Assert.Equal("⏳", ZaloCommonReplyText.RateLimited(null));
    }

    private static TeamPreferencePreview CreatePreview() => new(
        "session-1",
        ["player-1", "player-2"],
        ["Đặng Thế Nguyên", "To An"],
        [],
        2,
        6,
        4,
        2,
        6,
        6,
        0,
        false,
        true,
        true,
        [],
        null,
        "state-token",
        ZaloTeamRelationshipKind.Together,
        ZaloTeamRelationshipOperation.Set,
        [],
        false);
}
