using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMatchBriefFormatterTests
{
    [Fact]
    public void Bot_owned_state_explicitly_says_no_website_and_no_human_action()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.Recruiting,
            "Đang tiếp tục gom người",
            MatchLifecycleOwner.ZaloBot,
            needsWebsite: false,
            effectiveSlots: 17,
            capacity: 18);

        var text = ZaloMatchBriefFormatter.Append("Tui canh poll tiếp nha.", lifecycle);

        Assert.Contains("17/18 slot", text, StringComparison.Ordinal);
        Assert.Contains("CHƯA CẦN MỞ WEBSITE", text, StringComparison.Ordinal);
        Assert.Contains("ông chưa cần làm gì trên web", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leader_owned_state_keeps_action_in_zalo_instead_of_sending_user_to_web()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ReadyForDraft,
            "Sẵn sàng chốt draft",
            MatchLifecycleOwner.Leader,
            needsWebsite: false,
            effectiveSlots: 18,
            capacity: 18);

        var text = ZaloMatchBriefFormatter.Append("Đủ người rồi nha.", lifecycle);

        Assert.Contains("KHÔNG CẦN WEBSITE", text, StringComparison.Ordinal);
        Assert.Contains("trả lời ngay trong Zalo", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_overbook_exception_points_to_exact_web_area()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ResolvingOverbook,
            "Cần xác nhận dư slot",
            MatchLifecycleOwner.AdminWebsite,
            needsWebsite: true,
            effectiveSlots: 19,
            capacity: 18,
            webTarget: "bot-overbook-control");

        var text = ZaloMatchBriefFormatter.Append("Đang dư một slot.", lifecycle);

        Assert.Contains("CẦN WEBSITE", text, StringComparison.Ordinal);
        Assert.Contains("Overbook", text, StringComparison.Ordinal);
        Assert.Contains("Bot dừng", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Brief_surfaces_pass_and_profile_counts_without_listing_private_detail()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ResolvingPassSlots,
            "Đang xử lý pass slot",
            MatchLifecycleOwner.ZaloBot,
            needsWebsite: false,
            effectiveSlots: 18,
            capacity: 18,
            activeSlotRisks: 2,
            missingProfiles: 3);

        var text = ZaloMatchBriefFormatter.Append("Roster chưa sạch.", lifecycle);

        Assert.Contains("Pass đang mở: 2", text, StringComparison.Ordinal);
        Assert.Contains("Hồ sơ thiếu: 3", text, StringComparison.Ordinal);
    }

    private static MatchLifecycleResponse Snapshot(
        MatchLifecycleStage stage,
        string stageLabel,
        MatchLifecycleOwner owner,
        bool needsWebsite,
        int effectiveSlots,
        int capacity,
        string? webTarget = null,
        int activeSlotRisks = 0,
        int missingProfiles = 0) => new(
            SessionId: "s1",
            SessionName: "T6",
            Stage: stage,
            StageLabel: stageLabel,
            Headline: "headline",
            NextStep: "next",
            Owner: owner,
            NeedsWebsite: needsWebsite,
            WebTarget: webTarget,
            SuggestedZaloCommand: null,
            StartTime: DateTimeOffset.UtcNow.AddHours(2),
            PresentPlayerCount: effectiveSlots,
            EffectiveSlotCount: effectiveSlots,
            Capacity: capacity,
            MissingProfileCount: missingProfiles,
            MissingProfileNames: [],
            ActiveSlotRiskCount: activeSlotRisks,
            LeaderDecision: null,
            ReasonCode: "test",
            EvaluatedAt: DateTimeOffset.UtcNow);
}