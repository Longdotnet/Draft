using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMatchBriefFormatterTests
{
    [Fact]
    public void Bot_owned_state_says_what_happens_next_without_web_status_copy()
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
        Assert.DoesNotContain("CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KHÔNG CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chưa cần mở", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Standalone_bot_owned_state_is_action_first()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.Recruiting,
            "Đang tiếp tục gom người",
            MatchLifecycleOwner.ZaloBot,
            needsWebsite: false,
            effectiveSlots: 17,
            capacity: 18);

        var text = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate: true);

        Assert.Contains("Bot tiếp tục xử lý", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa có gì ông cần làm", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("website", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leader_owned_state_keeps_action_in_zalo_and_surfaces_exact_command()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ReadyForDraft,
            "Sẵn sàng chốt draft",
            MatchLifecycleOwner.Leader,
            needsWebsite: false,
            effectiveSlots: 18,
            capacity: 18,
            suggestedCommand: "draft đi");

        var text = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate: true);

        Assert.Contains("`draft đi`", text, StringComparison.Ordinal);
        Assert.Contains("ngay trong Zalo", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("headline", text, StringComparison.Ordinal);
        Assert.DoesNotContain("website", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_member_sees_leader_ownership_but_not_admin_command()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ReadyForDraft,
            "Sẵn sàng chốt draft",
            MatchLifecycleOwner.Leader,
            needsWebsite: false,
            effectiveSlots: 18,
            capacity: 18,
            suggestedCommand: "draft đi");

        var text = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate: false);

        Assert.Contains("trưởng/phó", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa có quyền", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`draft đi`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("website", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authorized_exception_describes_exact_action_and_includes_direct_link()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ResolvingOverbook,
            "Cần xác nhận dư slot",
            MatchLifecycleOwner.AdminWebsite,
            needsWebsite: true,
            effectiveSlots: 19,
            capacity: 18,
            webTarget: "bot-overbook-control");

        const string link = "https://web.example/app?focus=bot-overbook-control&sessionId=s1#bot-overbook-control";
        var text = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate: true, link);

        Assert.Contains("Xác nhận đúng người đang dư slot", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Xử lý ngay", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(link, text, StringComparison.Ordinal);
        Assert.DoesNotContain("CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KHÔNG CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_member_never_receives_admin_exception_link_or_web_framing()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ResolvingOverbook,
            "Cần xác nhận dư slot",
            MatchLifecycleOwner.AdminWebsite,
            needsWebsite: true,
            effectiveSlots: 19,
            capacity: 18,
            webTarget: "bot-overbook-control");

        const string link = "https://web.example/app?focus=bot-overbook-control&sessionId=s1#bot-overbook-control";
        var text = ZaloMatchBriefFormatter.Standalone(lifecycle, canOperate: false, link);

        Assert.Contains("cần trưởng/phó", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, text, StringComparison.Ordinal);
        Assert.DoesNotContain("website", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proactive_exception_only_adds_resolution_link_not_web_status_label()
    {
        var lifecycle = Snapshot(
            MatchLifecycleStage.ResolvingOverbook,
            "Cần xác nhận dư slot",
            MatchLifecycleOwner.AdminWebsite,
            needsWebsite: true,
            effectiveSlots: 19,
            capacity: 18,
            webTarget: "bot-overbook-control");

        const string link = "https://web.example/app?focus=bot-overbook-control&sessionId=s1#bot-overbook-control";
        var text = ZaloMatchBriefFormatter.Append("Tui vừa sync: đang dư 1 slot.", lifecycle, link);

        Assert.Contains("Xử lý bước đang vướng", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(link, text, StringComparison.Ordinal);
        Assert.DoesNotContain("CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KHÔNG CẦN WEBSITE", text, StringComparison.OrdinalIgnoreCase);
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
        int missingProfiles = 0,
        string? suggestedCommand = null) => new(
            SessionId: "s1",
            SessionName: "T6",
            Stage: stage,
            StageLabel: stageLabel,
            Headline: "headline",
            NextStep: "next",
            Owner: owner,
            NeedsWebsite: needsWebsite,
            WebTarget: webTarget,
            SuggestedZaloCommand: suggestedCommand,
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
