using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotIntelligenceTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 6 ", 6)]
    [InlineData("7", 7)]
    [InlineData("8", 8)]
    [InlineData("9", 9)]
    [InlineData("10", 10)]
    public void Exact_numeric_command_is_accepted(string input, int expected)
    {
        Assert.True(ZaloBotIntelligence.TryGetExactCommand(input, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("1 tuần đánh mấy lần")]
    [InlineData("1+1=?")]
    [InlineData("6 thứ 6")]
    [InlineData("help 1")]
    public void Numeric_prefix_is_not_an_exact_command(string input)
    {
        Assert.False(ZaloBotIntelligence.TryGetExactCommand(input, out _));
    }

    [Theory]
    [InlineData("nhắc nhóm sau 6 tiếng nếu còn thiếu người", ZaloReminderCommandKind.Schedule, 360, true)]
    [InlineData("cứ mỗi 8h tag @all nếu thiếu slot", ZaloReminderCommandKind.Schedule, 480, true)]
    [InlineData("nhắc T6 ngay", ZaloReminderCommandKind.TriggerNow, 0, true)]
    [InlineData("xem lịch nhắc T6", ZaloReminderCommandKind.Status, null, true)]
    [InlineData("tắt nhắc CN", ZaloReminderCommandKind.Disable, null, false)]
    [InlineData("nhắc sau 30 phút chỉ một lần", ZaloReminderCommandKind.Schedule, 30, false)]
    [InlineData("cứ 30 phút nhắc T6 một lần", ZaloReminderCommandKind.Schedule, 30, true)]
    [InlineData("hãy lên lịch schedular giúp tui cho thứ 6, nếu chưa đủ vote là 18 thì cứ cách 8h là thông báo cho mọi người giúp tui", ZaloReminderCommandKind.Schedule, 480, true)]
    public void Reminder_commands_accept_natural_vietnamese(
        string input,
        ZaloReminderCommandKind expectedKind,
        int? expectedDelayMinutes,
        bool expectedRepeats)
    {
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(input, out var command));
        Assert.Equal(expectedKind, command.Kind);
        Assert.Equal(expectedDelayMinutes, command.DelayMinutes);
        Assert.Equal(expectedRepeats, command.Repeats);
    }

    [Theory]
    [InlineData("nhắc nhóm sau 6 tiếng nếu còn thiếu người", ZaloBotIntent.ScheduleReminder)]
    [InlineData("xem lịch nhắc T6", ZaloBotIntent.ReminderStatus)]
    [InlineData("tắt nhắc CN", ZaloBotIntent.CancelReminder)]
    public void Reminder_commands_are_routed_without_calling_ai(string input, ZaloBotIntent expected)
    {
        Assert.Equal(expected, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
    }

    [Fact]
    public void Weekly_count_question_has_its_own_intent()
    {
        var result = ZaloBotIntelligence.ClassifyDeterministically("1 tuần đánh mấy lần vậy bot?");
        Assert.Equal(ZaloBotIntent.WeeklySessionCount, result.Intent);
        Assert.True(result.Confidence >= .9);
    }

    [Fact]
    public void Structured_classifier_output_is_strictly_parsed()
    {
        const string json = """{"intent":"Roster","confidence":0.91,"sessionReference":"CN 12/7","needsClarification":false,"clarificationQuestion":null,"reason":"asks_roster"}""";
        Assert.True(ZaloBotIntelligence.TryParseClassifierJson(json, out var result));
        Assert.Equal(ZaloBotIntent.Roster, result.Intent);
        Assert.Equal("CN 12/7", result.SessionReference);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"intent\":\"DeleteDatabase\",\"confidence\":1}")]
    [InlineData("{\"confidence\":1}")]
    public void Invalid_or_unsupported_classifier_output_falls_back(string value)
    {
        Assert.False(ZaloBotIntelligence.TryParseClassifierJson(value, out var result));
        Assert.Equal(ZaloBotIntent.Unknown, result.Intent);
    }

    [Fact]
    public void Semantic_rule_matching_tolerates_small_wording_changes()
    {
        var close = ZaloBotIntelligence.TokenJaccard("ai dep trai nhat nhom", "ai là đẹp trai nhất nhóm");
        var unrelated = ZaloBotIntelligence.TokenJaccard("ai dep trai nhat nhom", "san o dau gui xe the nao");
        Assert.True(close > unrelated);
        Assert.True(close >= .7);
    }

    [Fact]
    public void Semantic_rule_matching_understands_short_parking_question()
    {
        var score = ZaloBotIntelligence.TokenSimilarity(
            "ai hỏi gửi xe hay bãi xe",
            "gửi xe ở đâu vậy bot?");

        Assert.True(score >= .82);
        Assert.Equal(
            ZaloBotIntent.LocationParking,
            ZaloBotIntelligence.ClassifyDeterministically("ai hỏi gửi xe hay bãi xe").Intent);
    }

    [Theory]
    [InlineData("gửi ảnh và nội dung cho buổi gần nhất luôn thay vì hỏi chính xác")]
    [InlineData("dùng trận gần nhất, không cần hỏi lại")]
    public void Learned_behavior_can_prefer_nearest_session(string answer)
    {
        Assert.True(ZaloBotIntelligence.PrefersNearestSession(answer));
    }

    [Theory]
    [InlineData("hãy lấy danh sách 3 team hôm nay và gửi cho tui", ZaloBotIntent.TeamLineup)]
    [InlineData("cập nhật số lượng đã vote trên web", ZaloBotIntent.SyncPoll)]
    [InlineData("tự khui túi mù rồi draft tự bốc team và chụp màn hình", ZaloBotIntent.AutoDraft)]
    [InlineData("gửi ảnh đội hình ba team", ZaloBotIntent.TeamImage)]
    [InlineData("draft lại trận hôm nay từ đầu", ZaloBotIntent.Redraft)]
    [InlineData("đổi vị trí Thanh Tuyền với Nick Tran", ZaloBotIntent.SwapTeamPlayers)]
    [InlineData("+1 số lượng vote cho bạn của Nick Tran", ZaloBotIntent.AddGuestPlayer)]
    [InlineData("Nick Tran muốn share slot với Thanh Tuyền", ZaloBotIntent.ShareSlot)]
    [InlineData("cập nhật Nick Tran: nam, công, trung bình", ZaloBotIntent.UpdatePlayerProfile)]
    [InlineData("đưa tui danh sách người chưa cập nhật giới tính, trình độ cho buổi thứ 4", ZaloBotIntent.IncompleteProfiles)]
    [InlineData("T4 còn ai thiếu thông tin hồ sơ?", ZaloBotIntent.IncompleteProfiles)]
    [InlineData("ai chưa có giới tính và trình độ trận T6?", ZaloBotIntent.IncompleteProfiles)]
    [InlineData("cho xem những người cần cập nhật trước khi draft", ZaloBotIntent.IncompleteProfiles)]
    [InlineData("lọc giúp người chưa khai giới tính bữa chủ nhật", ZaloBotIntent.IncompleteProfiles)]
    [InlineData("T4 còn ai chưa cập nhật thông tin?", ZaloBotIntent.IncompleteProfiles)]
    public void New_features_understand_natural_vietnamese(string question, ZaloBotIntent expected)
    {
        Assert.Equal(expected, ZaloBotIntelligence.ClassifyDeterministically(question).Intent);
    }

    [Theory]
    [InlineData("đổi vị trí Thanh Tuyền với Nick Tran", "Thanh Tuyền", "Nick Tran")]
    [InlineData("swap Thanh Tuyền với Nick Tran", "Thanh Tuyền", "Nick Tran")]
    [InlineData("đổi chỗ Thanh Tuyền và Nick Tran", "Thanh Tuyền", "Nick Tran")]
    public void Swap_command_extracts_two_player_names(string question, string first, string second)
    {
        Assert.True(ZaloBotIntelligence.TryExtractSwapPlayerNames(question, out var actualFirst, out var actualSecond));
        Assert.Equal(first, actualFirst);
        Assert.Equal(second, actualSecond);
    }

    [Theory]
    [InlineData("Nick Tran muốn share slot với Thanh Tuyền", "Nick Tran", "Thanh Tuyền")]
    [InlineData("Nick Tran share slot với bạn", "Nick Tran", "bạn")]
    public void Share_slot_command_extracts_anchor_and_partner(string question, string anchor, string partner)
    {
        Assert.True(ZaloBotIntelligence.TryExtractSharePlayerNames(question, out var actualAnchor, out var actualPartner));
        Assert.Equal(anchor, actualAnchor);
        Assert.Equal(partner, actualPartner);
    }

    [Theory]
    [InlineData("xác nhận draft")]
    [InlineData("đồng ý")]
    [InlineData("ok chạy")]
    public void Destructive_draft_requires_an_explicit_confirmation_phrase(string value)
    {
        Assert.True(ZaloBotIntelligence.IsConfirmation(value));
    }

    [Fact]
    public void Team_card_renderer_outputs_a_png()
    {
        var bytes = SimpleTeamCardPng.Render(
            "Trận hôm nay",
            new DateTimeOffset(2026, 7, 13, 18, 0, 0, TimeSpan.FromHours(7)),
            [
                new TeamCardTeam("Team A", "Thanh Long", ["Thanh Long", "An", "Bình"]),
                new TeamCardTeam("Team B", "Minh", ["Minh", "Hà", "Phúc"]),
                new TeamCardTeam("Team C", "Nam", ["Nam", "Linh", "Huy"])
            ]);

        Assert.True(bytes.Length > 1_000);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
    }

    [Theory]
    [InlineData("huỷ")]
    [InlineData("cancel")]
    [InlineData("không cần nữa")]
    public void Conversation_can_be_cancelled_naturally(string value)
    {
        Assert.True(ZaloBotIntelligence.IsCancel(value));
    }

    [Fact]
    public void Follow_up_session_alias_resolves_one_candidate()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(7));
        var sessions = new[]
        {
            new ZaloSessionReference("wed", "Trận giữa tuần", new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("fri", "Trận cuối tuần", new DateTimeOffset(2026, 7, 17, 18, 0, 0, TimeSpan.FromHours(7)))
        };
        Assert.Equal(new[] { "fri" }, ZaloBotIntelligence.ResolveSessionReference("T6", sessions, now));
    }

    [Fact]
    public void Ambiguous_day_alias_returns_all_matching_candidates_for_clarification()
    {
        var sessions = new[]
        {
            new ZaloSessionReference("a", "Ca sớm", new DateTimeOffset(2026, 7, 17, 18, 0, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("b", "Ca muộn", new DateTimeOffset(2026, 7, 17, 20, 0, 0, TimeSpan.FromHours(7)))
        };
        Assert.Equal(2, ZaloBotIntelligence.ResolveSessionReference("thứ 6", sessions).Count);
    }

    [Theory]
    [InlineData("từ giờ giờ đấu là 19h")]
    [InlineData("danh sách trận này có Thanh Long")]
    [InlineData("sân đổi sang UTE")]
    public void Learned_rule_cannot_override_protected_business_facts(string value)
    {
        Assert.True(ZaloBotIntelligence.IsProtectedBusinessFactText(value));
    }
}
