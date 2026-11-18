using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDailyBlameServiceTests
{
    [Fact]
    public void Context_builder_prefers_real_contradiction_over_single_message_roast()
    {
        var target = new ZaloDailyBlameTarget("pin", "Pin", "coach");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence("m1", "pin", "Pin", "Dẹp hết bồ bịch, banh bóng quan trọng hơn", now.AddDays(-4)),
            Evidence("m2", "pin", "Pin", "mai tớ đi chơi vs bồ nhó, ai ế ở nhà đánh banh", now.AddDays(-1)),
            Evidence("m3", "pin", "Pin", "ai pass slot thì nhả slot nha", now.AddHours(-4))
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.Equal(ZaloRoastAngleKind.Contradiction, context.SelectedAngle.Kind);
        Assert.Equal("contradiction:love-vs-volleyball", context.SelectedAngle.Id);
        Assert.True(context.SelectedAngle.Evidence.Select(item => item.MessageId).ToHashSet().SetEquals(["m1", "m2"]));
    }

    [Fact]
    public void Used_contradiction_is_penalized_so_fresh_context_can_win()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence("old1", "u1", "A", "Dẹp hết bồ bịch banh bóng quan trọng hơn", now.AddDays(-3), usedRecently: true),
            Evidence("old2", "u1", "A", "mai đi chơi với bồ", now.AddDays(-2), usedRecently: true),
            Evidence("fresh1", "u1", "A", "ai pass slot thì cho tui xin slot", now.AddHours(-4)),
            Evidence("fresh2", "u1", "A", "còn slot thì tui lấy slot", now.AddHours(-2))
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.NotEqual("contradiction:love-vs-volleyball", context.SelectedAngle.Id);
        Assert.Contains(context.SelectedAngle.Evidence, item => item.MessageId.StartsWith("fresh", StringComparison.Ordinal));
    }

    [Fact]
    public void Counter_reply_beats_plain_self_own_when_reply_context_exists()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence(
                "m1",
                "u1",
                "A",
                "Hôm qua lần đầu vượt KPI 10 block, hãy coi chừng tôi",
                now.AddHours(-2),
                replies: ["REPLY · B: đánh với anh tui dùng tay trái được rồi"])
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.Equal(ZaloRoastAngleKind.CounterReply, context.SelectedAngle.Kind);
        Assert.Equal("counter-reply:m1", context.SelectedAngle.Id);
    }

    [Fact]
    public void Nearby_context_is_weaker_than_exact_reply()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence("exact", "u1", "A", "KPI 10 block", now.AddMinutes(-20), replies: ["REPLY · B: dùng tay trái"]),
            Evidence("near", "u1", "A", "KPI 11 block", now.AddMinutes(-10), replies: ["NEARBY · C: nay sân đông ghê"])
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.Equal("counter-reply:exact", context.SelectedAngle.Id);
    }

    [Fact]
    public void Contradiction_requires_followup_to_happen_after_claim()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence("admin", "u1", "A", "xin slot rồi chia team", now.AddDays(-3)),
            Evidence("study", "u1", "A", "tôi cần chuyên tâm học hành", now.AddDays(-1))
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.DoesNotContain(context.RankedAngles, angle => angle.Id == "contradiction:study-vs-volleyball-admin");
    }

    [Fact]
    public void Short_money_token_does_not_match_inside_block_word()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var now = DateTimeOffset.UtcNow;
        var evidence = new[]
        {
            Evidence("m1", "u1", "A", "KPI block hôm nay", now.AddMinutes(-20)),
            Evidence("m2", "u1", "A", "block tiếp quả nữa", now.AddMinutes(-10))
        };

        var context = ZaloRoastContextBuilder.Build(target, evidence, [], now);

        Assert.DoesNotContain(context.RankedAngles, angle => angle.Id == "pattern:money");
        Assert.Contains(context.RankedAngles, angle => angle.Id == "pattern:block");
    }

    [Fact]
    public void Ai_copywriter_must_return_selected_angle_and_only_allowed_evidence()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var context = ZaloRoastContextBuilder.Build(
            target,
            [Evidence("m1", "u1", "A", "KPI 10 block", DateTimeOffset.UtcNow, replies: ["REPLY · B: dùng tay trái"] )],
            [],
            DateTimeOffset.UtcNow);
        var validText = "[[TARGET]] vừa công bố KPI rất tự tin, nhưng phản hồi ngay sau đó đã kéo báo cáo thành tích trở lại mặt đất. Backend có đủ log nên NPC không cần dựng thêm tình tiết. Một câu gáy cộng một câu phản dame là đủ thành biên bản. Hôm nay phòng truyền thông có lẽ nên đọc log trước khi phát hành thông cáo =))";
        var valid = $$"""
                      {"targetId":"u1","angleId":"{{context.SelectedAngle.Id}}","text":"{{validText}}","evidenceIds":["m1"]}
                      """;
        var foreign = $$"""
                        {"targetId":"u1","angleId":"{{context.SelectedAngle.Id}}","text":"{{validText}}","evidenceIds":["m999"]}
                        """;

        Assert.NotNull(ZaloRoastTextGenerator.Parse(valid, context));
        Assert.Null(ZaloRoastTextGenerator.Parse(foreign, context));
    }

    [Fact]
    public void Validator_rejects_model_created_mentions_and_repeated_copy()
    {
        var target = new ZaloDailyBlameTarget("u1", "A", "audit");
        var previous = "🚨 NPC DAILY BLAME\n\n@A KPI hôm nay vừa công bố xong đã bị phản dame ngay lập tức. Một câu gáy cộng một câu đáp là đủ thành báo cáo. NPC chỉ audit dữ liệu chứ không cần bịa thêm. Phòng truyền thông nên đọc log trước khi lên sóng.\n\n📌 Hồ sơ hôm nay lấy từ log thật của chính nhân vật. NPC không bịa, NPC chỉ audit =))";
        var context = ZaloRoastContextBuilder.Build(
            target,
            [Evidence("m1", "u1", "A", "KPI 10 block", DateTimeOffset.UtcNow, replies: ["REPLY · B: dùng tay trái"] )],
            [previous],
            DateTimeOffset.UtcNow);
        var withMention = new ZaloDailyBlameSection(
            "u1",
            "[[TARGET]] hôm nay audit KPI nhưng lại tự @B kéo người khác vào bài. Câu này cố tình đủ dài để vượt qua kiểm tra hình dạng của validator và chứng minh model không được quyền tự tạo mention trong output dù context có người phản hồi.",
            ["m1"]);
        var repeated = new ZaloDailyBlameSection(
            "u1",
            "[[TARGET]] KPI hôm nay vừa công bố xong đã bị phản dame ngay lập tức. Một câu gáy cộng một câu đáp là đủ thành báo cáo. NPC chỉ audit dữ liệu chứ không cần bịa thêm. Phòng truyền thông nên đọc log trước khi lên sóng.",
            ["m1"]);

        Assert.Equal("model_created_mention", ZaloRoastValidator.Validate(withMention, context).Reason);
        Assert.Equal("too_similar_to_previous", ZaloRoastValidator.Validate(repeated, context).Reason);
    }

    [Fact]
    public void One_daily_blame_message_mentions_only_its_target()
    {
        var target = new ZaloDailyBlameTarget("u1", "Đặng Thế Nguyễn", "audit");
        const string text = "🚨 NPC DAILY BLAME\n\n@Đặng Thế Nguyễn một bài audit riêng.";

        var mentions = ZaloDailyBlameService.BuildMentions(text, [target]);

        Assert.Single(mentions);
        Assert.Equal("u1", mentions[0].Uid);
    }

    [Fact]
    public void Pending_target_moves_to_next_member_after_each_daily_delivery()
    {
        var targets = new[]
        {
            new ZaloDailyBlameTarget("u1", "Đặng Thế Nguyễn", "audit"),
            new ZaloDailyBlameTarget("u2", "Pin", "coach"),
            new ZaloDailyBlameTarget("u3", "Thanh Trúc", "cfo")
        };
        const string day = "2026-11-18";
        var firstHistory = new[]
        {
            History(ZaloProactiveLane.DailyBlame, "evidence:m1", DateTimeOffset.UtcNow, "u1", day)
        };
        var secondHistory = firstHistory.Append(
            History(ZaloProactiveLane.DailyBlame, "evidence:m2", DateTimeOffset.UtcNow, "u2", day));

        Assert.Equal("u2", ZaloDailyBlameService.SelectPendingTarget(targets, firstHistory, day)!.ZaloUserId);
        Assert.Equal("u3", ZaloDailyBlameService.SelectPendingTarget(targets, secondHistory, day)!.ZaloUserId);
        Assert.Null(ZaloDailyBlameService.SelectPendingTarget(
            targets,
            secondHistory.Append(History(ZaloProactiveLane.DailyBlame, "evidence:m3", DateTimeOffset.UtcNow, "u3", day)),
            day));
    }

    [Fact]
    public void Used_evidence_is_loaded_only_from_recent_daily_blame_history()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new[]
        {
            History(ZaloProactiveLane.DailyBlame, "evidence:m1,m2", now.AddDays(-1)),
            History(ZaloProactiveLane.Community, "evidence:m3", now.AddDays(-1)),
            History(ZaloProactiveLane.DailyBlame, "evidence:old", now.AddDays(-30))
        };

        var used = ZaloDailyBlameService.ExtractUsedEvidenceIds(history, now.AddDays(-21));

        Assert.True(used.SetEquals(["m1", "m2"]));
    }

    [Fact]
    public void Never_roasted_extra_member_gets_strong_rotation_priority()
    {
        var now = DateTimeOffset.UtcNow;

        var neverRoasted = ZaloDailyBlameService.ScoreExtraTargetFairness(null, now);
        var roastedElevenDaysAgo = ZaloDailyBlameService.ScoreExtraTargetFairness(now.AddDays(-11), now);

        Assert.Equal(300, neverRoasted);
        Assert.True(neverRoasted > roastedElevenDaysAgo);
    }

    [Fact]
    public void Extra_target_score_rewards_rich_recent_activity()
    {
        var now = DateTimeOffset.UtcNow;
        var rich = ZaloDailyBlameService.ScoreExtraTargetForTest(
            "rich",
            "2026-11-18",
            [
                ("KPI block hôm nay", now.AddHours(-1)),
                ("ai pass slot cho tui xin", now.AddHours(-2)),
                ("tui muốn khác team hôm nay", now.AddHours(-3)),
                ("vote lại nha", now.AddHours(-4))
            ],
            now);
        var quiet = ZaloDailyBlameService.ScoreExtraTargetForTest(
            "quiet",
            "2026-11-18",
            [
                ("dạ", now.AddDays(-8)),
                ("ok ạ", now.AddDays(-9)),
                ("em gửi", now.AddDays(-10))
            ],
            now);

        Assert.True(rich > quiet);
    }

    private static ZaloDailyBlameEvidence Evidence(
        string id,
        string userId,
        string name,
        string content,
        DateTimeOffset? sentAt = null,
        bool usedRecently = false,
        IReadOnlyList<string>? replies = null) =>
        new(id, userId, name, content, sentAt ?? DateTimeOffset.UtcNow, 10, replies ?? [], usedRecently);

    private static ZaloProactiveMessageHistoryData History(
        string lane,
        string contentKey,
        DateTimeOffset sentAt,
        string? subjectUserId = null,
        string? localDate = null) =>
        new(
            Guid.NewGuid().ToString("n"),
            "conn",
            "group",
            localDate ?? sentAt.ToString("yyyy-MM-dd"),
            lane,
            "test",
            contentKey,
            subjectUserId,
            null,
            "message",
            sentAt,
            null,
            Guid.NewGuid().ToString("n"));
}
