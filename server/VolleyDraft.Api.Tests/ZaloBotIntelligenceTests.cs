using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
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
    [InlineData("12", 12)]
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

    [Fact]
    public void Command_12_opens_member_inactivity_flow()
    {
        Assert.Equal(ZaloBotIntent.ListMostInactiveMembers, ZaloBotIntelligence.IntentForCommand(12));
    }

    [Theory]
    [InlineData("ai 4 tháng rồi chưa vote?", ZaloBotIntent.ListMembersWithoutRecentVote)]
    [InlineData("ai 90 ngày chưa nhắn?", ZaloBotIntent.ListMembersWithoutRecentMessage)]
    [InlineData("Long hoạt động gần nhất khi nào?", ZaloBotIntent.GetMemberLastActivity)]
    [InlineData("Long vote poll gần nhất nào?", ZaloBotIntent.GetMemberLastVote)]
    [InlineData("top 10 người ít hoạt động nhất", ZaloBotIntent.ListMostInactiveMembers)]
    [InlineData("ai đang có dấu hiệu giảm hoạt động?", ZaloBotIntent.ListAtRiskMembers)]
    [InlineData("tình hình hoạt động của nhóm tháng này thế nào?", ZaloBotIntent.AnalyzeGroupEngagement)]
    [InlineData("đồng bộ lại dữ liệu cũ", ZaloBotIntent.SyncMemberActivity)]
    [InlineData("đồng bộ tới đâu rồi?", ZaloBotIntent.GetActivitySyncStatus)]
    public void Member_intelligence_phrases_have_deterministic_routes(string input, ZaloBotIntent expected)
    {
        Assert.Equal(expected, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
    }

    [Theory]
    [InlineData("Những ai vào nhóm được 45 ngày đổ lại?", ZaloBotIntent.ListRecentlyJoinedMembers)]
    [InlineData("7 ngày qua có ai mới vào?", ZaloBotIntent.ListRecentlyJoinedMembers)]
    [InlineData("Có bao nhiêu thành viên mới trong 30 ngày?", ZaloBotIntent.ListRecentlyJoinedMembers)]
    [InlineData("Tui vào nhóm ngày nào?", ZaloBotIntent.GetMemberJoinDate)]
    public void Membership_history_questions_have_deterministic_routes(string input, ZaloBotIntent expected)
    {
        Assert.Equal(expected, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
    }

    [Theory]
    [InlineData("nhắc nhóm sau 6 tiếng nếu còn thiếu người", ZaloReminderCommandKind.Schedule, 360, true)]
    [InlineData("cứ mỗi 8h tag @all nếu thiếu slot", ZaloReminderCommandKind.Schedule, 480, true)]
    [InlineData("nhắc T6 ngay", ZaloReminderCommandKind.TriggerNow, 0, true)]
    [InlineData("xem lịch nhắc T6", ZaloReminderCommandKind.Status, null, true)]
    [InlineData("lịch nhắc cho thứ 6 cách 8h hiện tại đâu?", ZaloReminderCommandKind.Status, null, true)]
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

    [Theory]
    [InlineData("giải thích chi tiết về waitlist")]
    [InlineData("waitlist là gì và cách hoạt động")]
    [InlineData("hiện tại có danh sách chờ không khi đủ 18")]
    [InlineData("waitlist ấy")]
    public void Waitlist_questions_are_routed_without_requiring_ai(string input)
    {
        var decision = ZaloBotIntelligence.ClassifyDeterministically(input);

        Assert.Equal(ZaloBotIntent.WaitlistStatus, decision.Intent);
    }

    [Theory]
    [InlineData("@Sin muốn vào waitlist thứ 4")]
    [InlineData("xin thêm @Sin vào danh sách chờ T4")]
    public void Waitlist_join_with_a_named_player_is_not_misclassified_as_status(string input)
    {
        var decision = ZaloBotIntelligence.ClassifyDeterministically(input);

        Assert.Equal(ZaloBotIntent.WaitlistJoin, decision.Intent);
    }

    [Theory]
    [InlineData("@Nguyễn Thanh Tâm muốn rút nhường cho @Sin")]
    [InlineData("@Nguyen Thanh Tam hủy slot cho @Sin")]
    public void Slot_transfer_is_distinguished_from_accepting_a_waitlist_invitation(string input)
    {
        var decision = ZaloBotIntelligence.ClassifyDeterministically(input);

        Assert.Equal(ZaloBotIntent.SlotTransfer, decision.Intent);
    }

    [Fact]
    public void Slot_transfer_confirmation_is_not_accepted_as_ai_classifier_output()
    {
        const string json = "{\"intent\":\"SlotTransferConfirm\",\"confidence\":1}";

        Assert.False(ZaloBotIntelligence.TryParseClassifierJson(json, out var result));
        Assert.Equal(ZaloBotIntent.Unknown, result.Intent);
    }

    [Theory]
    [InlineData("xác nhận")]
    [InlineData("đồng ý")]
    [InlineData("chốt đi")]
    public void Reminder_confirmation_accepts_natural_acknowledgements(string input)
    {
        Assert.True(ZaloBotIntelligence.IsConfirmation(input));
    }

    [Theory]
    [InlineData("hủy lịch nhắc")]
    [InlineData("hủy toàn bộ lịch nhắc")]
    public void Pending_reminder_can_be_cancelled_with_a_full_phrase(string input)
    {
        Assert.True(ZaloBotIntelligence.IsCancel(input));
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
    [InlineData("gửi lại team rồi tag từng người", ZaloBotIntent.TeamLineup)]
    [InlineData("cập nhật số lượng đã vote trên web", ZaloBotIntent.SyncPoll)]
    [InlineData("tự khui túi mù rồi draft tự bốc team và chụp màn hình", ZaloBotIntent.AutoDraft)]
    [InlineData("gửi ảnh đội hình ba team", ZaloBotIntent.TeamImage)]
    [InlineData("gửi card 3 team cho tui", ZaloBotIntent.TeamImage)]
    [InlineData("chụp danh sách team", ZaloBotIntent.TeamImage)]
    [InlineData("draft lại trận hôm nay từ đầu", ZaloBotIntent.Redraft)]
    [InlineData("cân bằng lại đội hình team 2 và team 3", ZaloBotIntent.RebalanceTeams)]
    [InlineData("balance team A-C giúp tui", ZaloBotIntent.RebalanceTeams)]
    [InlineData("chỉnh đều lại đội hai với đội ba", ZaloBotIntent.RebalanceTeams)]
    [InlineData("đổi vị trí Thanh Tuyền với Nick Tran", ZaloBotIntent.SwapTeamPlayers)]
    [InlineData("đổi slot Thanh Tuyền với Khánh Chi thứ 6", ZaloBotIntent.SwapTeamPlayers)]
    [InlineData("+1 số lượng vote cho bạn của Nick Tran", ZaloBotIntent.AddGuestPlayer)]
    [InlineData("Ngọc Huyền thêm +1 bạn hôm nay", ZaloBotIntent.AddGuestPlayer)]
    [InlineData("To An muốn chơi chung với Anh Duy thứ 6", ZaloBotIntent.TeamPreference)]
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
    [InlineData("cân bằng team 2 và team 3", 2, 3)]
    [InlineData("cân bằng team 1-3", 1, 3)]
    [InlineData("cân bằng lại đội hình 1-3", 1, 3)]
    [InlineData("balance đội A với đội C", 1, 3)]
    [InlineData("chỉnh đều đội hai và đội ba", 2, 3)]
    public void Rebalance_command_extracts_exact_team_pair(string question, int first, int second)
    {
        Assert.True(ZaloBotIntelligence.TryParseTeamPair(question, out var actualFirst, out var actualSecond));
        Assert.Equal(first, actualFirst);
        Assert.Equal(second, actualSecond);
    }

    [Fact]
    public void Repair_share_slot_is_a_protected_admin_intent()
    {
        var decision = ZaloBotIntelligence.ClassifyDeterministically(
            "sửa share slot của Vivian từ Thanh Long sang Vinh cho T4");

        Assert.Equal(ZaloBotIntent.RepairShareSlot, decision.Intent);
    }

    [Theory]
    [InlineData("đổi vị trí Thanh Tuyền với Nick Tran", "Thanh Tuyền", "Nick Tran")]
    [InlineData("swap Thanh Tuyền với Nick Tran", "Thanh Tuyền", "Nick Tran")]
    [InlineData("đổi chỗ Thanh Tuyền và Nick Tran", "Thanh Tuyền", "Nick Tran")]
    [InlineData("đổi slot Thanh Tuyền với Khánh Chi", "Thanh Tuyền", "Khánh Chi")]
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

    [Fact]
    public void Explicit_share_mentions_override_sender_or_ai_anchor()
    {
        var current = new ZaloShareSlotCommand("Thanh Long", ["Vivian"], 1);
        var mentions = new List<ZaloMentionedUser>
        {
            new("vinh-id", "Vinh"),
            new("vivian-id", "Vivian")
        };

        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(mentions, current);

        Assert.NotNull(command);
        Assert.Equal("Vinh", command!.Anchor);
        Assert.Equal(["Vivian"], command.Partners);
        Assert.Equal(1, command.RequestedPartnerCount);
    }

    [Fact]
    public void Explicit_three_player_mentions_bind_plus_two_in_order()
    {
        var mentions = new List<ZaloMentionedUser>
        {
            new("vinh-id", "Vinh"),
            new("a-id", "An"),
            new("b-id", "Bình")
        };

        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(mentions, null);

        Assert.NotNull(command);
        Assert.Equal("Vinh", command!.Anchor);
        Assert.Equal(["An", "Bình"], command.Partners);
        Assert.Equal(2, command.RequestedPartnerCount);
    }

    [Theory]
    [InlineData("@Duy Nam share slot với Trần Chí Cường", "Duy Nam", "duy-id", "Trần Chí Cường")]
    [InlineData("@Sin share slot với Lê Hữu Lý", "Sin", "sin-id", "Lê Hữu Lý")]
    public void One_anchor_mention_overrides_ai_fuzzy_match(
        string question,
        string expectedAnchor,
        string anchorId,
        string expectedPartner)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(question, out var parsed));
        var aiCommand = new ZaloShareSlotCommand("Minh Nam", [expectedPartner], 1);

        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser(anchorId, expectedAnchor)],
            aiCommand,
            parsed);

        Assert.NotNull(command);
        Assert.Equal(expectedAnchor, command!.Anchor);
        Assert.Equal(anchorId, command.AnchorZaloUserId);
        Assert.Equal([expectedPartner], command.Partners);
    }

    [Fact]
    public void One_partner_mention_keeps_plain_anchor_and_binds_partner_uid()
    {
        const string question = "Trần Chí Cường share slot với @Sin";
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(question, out var parsed));

        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("sin-id", "Sin")],
            parsed,
            parsed);

        Assert.NotNull(command);
        Assert.Equal("Trần Chí Cường", command!.Anchor);
        Assert.Equal(["Sin"], command.Partners);
        Assert.Equal(["sin-id"], command.PartnerZaloUserIds);
    }

    [Fact]
    public void Extract_question_removes_only_bot_mention_and_keeps_first_player_mention()
    {
        const string content = "@Npc @Duy Nam share slot với Trần Chí Cường";
        var incoming = new ZaloIncomingMessageEvent(
            "account-id",
            "bot-id",
            "group-id",
            "message-id",
            "sender-id",
            "Thanh Long",
            content,
            [
                new ZaloBridgeMention("bot-id", 0, "@Npc".Length),
                new ZaloBridgeMention("duy-id", "@Npc ".Length, "@Duy Nam".Length)
            ],
            true,
            0);

        var question = ZaloBotService.ExtractQuestion(incoming);

        Assert.Equal("@Duy Nam share slot với Trần Chí Cường", question);
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
            "Volley Draft Thứ Tư 15/07",
            new DateTimeOffset(2026, 7, 13, 18, 0, 0, TimeSpan.FromHours(7)),
            [
                new TeamCardTeam("Team A", "Thanh Tuyền", ["Thanh Tuyền", "Đặng Thế Nguyễn", "Minh Nam", "Thế Hoàng", "Tô An", "Trần Long Nhật"]),
                new TeamCardTeam("Team B", "Ngọc Huyền", ["Ngọc Huyền", "Anh Duy", "Đoàn Trí Tài", "Nick Tran / Vivian", "Thanh Long", "Trọng Hòa"]),
                new TeamCardTeam("Team C", "Nguyễn Trí Nhân", ["Nguyễn Trí Nhân", "Duy Nam", "Nguyễn Thanh Tâm", "Thành Đạt", "Thanh Trúc", "Vinh"])
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
    public void Follow_up_calendar_date_resolves_the_original_upcoming_session()
    {
        var now = new DateTimeOffset(2026, 8, 7, 23, 0, 0, TimeSpan.FromHours(7));
        var sessions = new[]
        {
            new ZaloSessionReference("jul-26", "CN 26/7", new DateTimeOffset(2026, 7, 26, 17, 30, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("aug-2", "CN 2/8", new DateTimeOffset(2026, 8, 2, 17, 30, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("aug-9", "CN 9/8", new DateTimeOffset(2026, 8, 9, 17, 30, 0, TimeSpan.FromHours(7)))
        };

        Assert.Equal(new[] { "aug-9" }, ZaloBotIntelligence.ResolveSessionReference("9/8", sessions, now));
    }

    [Fact]
    public void Operational_weekday_reference_excludes_old_sessions()
    {
        var now = new DateTimeOffset(2026, 8, 7, 23, 0, 0, TimeSpan.FromHours(7));
        var sessions = new[]
        {
            new ZaloSessionReference("jul-26", "CN 26/7", new DateTimeOffset(2026, 7, 26, 17, 30, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("aug-2", "CN 2/8", new DateTimeOffset(2026, 8, 2, 17, 30, 0, TimeSpan.FromHours(7))),
            new ZaloSessionReference("aug-9", "CN 9/8", new DateTimeOffset(2026, 8, 9, 17, 30, 0, TimeSpan.FromHours(7)))
        };

        Assert.Equal(
            ["aug-9"],
            ZaloBotIntelligence.SelectOperationalSessionCandidateIds("cho tui vào danh sách chờ CN", sessions, now));
        Assert.Equal(
            ["aug-2"],
            ZaloBotIntelligence.SelectOperationalSessionCandidateIds("xem CN 2/8", sessions, now));
    }

    [Theory]
    [InlineData("lần sau ai muốn share slot thì nói với @Npc nha")]
    [InlineData("nếu ai cần share slot thì tag bot giúp mình")]
    public void Share_slot_guidance_is_not_routed_as_a_mutation(string question)
    {
        Assert.True(ZaloBotIntelligence.IsShareSlotAnnouncement(question));
        Assert.Equal(ZaloBotIntent.GeneralChat, ZaloBotIntelligence.ClassifyDeterministically(question).Intent);
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

    [Theory]
    [InlineData(ZaloBotIntent.Roster)]
    [InlineData(ZaloBotIntent.TeamLineup)]
    [InlineData(ZaloBotIntent.RedraftConfirm)]
    [InlineData(ZaloBotIntent.RebalanceTeamsConfirm)]
    [InlineData(ZaloBotIntent.UpdatePlayerProfile)]
    [InlineData(ZaloBotIntent.ShareSlot)]
    [InlineData(ZaloBotIntent.SlotTransferConfirm)]
    public void Ai_style_rewrite_is_disabled_for_structured_or_mutating_answers(ZaloBotIntent intent)
    {
        Assert.False(ZaloBotIntelligence.CanUseAiStyleRewrite(intent));
    }

    [Theory]
    [InlineData(ZaloBotIntent.SessionSchedule)]
    [InlineData(ZaloBotIntent.LocationParking)]
    [InlineData(ZaloBotIntent.MissingSlots)]
    public void Ai_style_rewrite_remains_available_for_low_risk_information(ZaloBotIntent intent)
    {
        Assert.True(ZaloBotIntelligence.CanUseAiStyleRewrite(intent));
    }
}

public sealed class ZaloMembershipHistoryEventTests
{
    [Fact]
    public async Task Provider_events_update_existing_member_presence_without_changing_first_seen()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var now = DateTimeOffset.UtcNow;
        var firstSeen = now.AddDays(-15);
        fixture.Db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            ZaloConnectionId = "connection-a", GroupId = "group-1", ZaloUserId = "member-a",
            DisplayName = "An", FirstSeenAt = firstSeen, LastSeenAt = firstSeen,
            LastSyncedAt = firstSeen, IsCurrentMember = true
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "joined", now.AddDays(-10)));
        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "left", now.AddDays(-5)));
        var member = await fixture.Db.ZaloGroupMembers.SingleAsync();
        Assert.False(member.IsCurrentMember);
        Assert.Equal(now.AddDays(-5).ToUnixTimeMilliseconds(), member.LeftAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(firstSeen, member.FirstSeenAt);
        Assert.Equal(0, (await fixture.Service.QueryRecentAsync("connection-a", "group-1", 30, now)).UnknownCurrentMemberCount);

        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "rejoined", now.AddMinutes(-1)));
        Assert.True(member.IsCurrentMember);
        Assert.Null(member.LeftAt);
        Assert.Equal(firstSeen, member.FirstSeenAt);
    }

    [Fact]
    public async Task Delayed_provider_event_cannot_overwrite_newer_directory_presence()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var now = DateTimeOffset.UtcNow;
        fixture.Db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            ZaloConnectionId = "connection-a", GroupId = "group-1", ZaloUserId = "member-a",
            DisplayName = "An", FirstSeenAt = now.AddDays(-20), LastSeenAt = now,
            LastSyncedAt = now.AddMinutes(-2), IsCurrentMember = true
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "old-leave", now.AddHours(-1)));
        Assert.True((await fixture.Db.ZaloGroupMembers.SingleAsync()).IsCurrentMember);
    }

    [Fact]
    public async Task Directory_snapshot_started_before_a_leave_cannot_recreate_a_departed_member()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var now = DateTimeOffset.UtcNow;
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "start", now.AddDays(-4)));
        var directoryRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "leave", now.AddHours(-1)));

        var newer = await fixture.Service.ObserveDirectoryAsync(
            "connection-a", "group-1", ["member-a"], true,
            DateTimeOffset.UtcNow, directoryRequestedAt: directoryRequestedAt);
        await fixture.Db.SaveChangesAsync();

        Assert.Contains("member-a", newer);
        Assert.Empty(await fixture.Db.ZaloGroupMembershipPeriods.Where(period => period.IsCurrentPeriod).ToListAsync());
    }

    [Fact]
    public async Task Complete_directory_started_before_a_join_cannot_close_the_newer_join()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var directoryRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var joinedAt = DateTimeOffset.UtcNow.AddDays(-1);
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "join", joinedAt));

        var newer = await fixture.Service.ObserveDirectoryAsync(
            "connection-a", "group-1", [], true, DateTimeOffset.UtcNow,
            directoryRequestedAt: directoryRequestedAt);
        await fixture.Db.SaveChangesAsync();

        Assert.Contains("member-a", newer);
        var period = Assert.Single(await fixture.Db.ZaloGroupMembershipPeriods.ToListAsync());
        Assert.True(period.IsCurrentPeriod);
        Assert.Null(period.LeftAt);
        Assert.Equal(joinedAt.ToUnixTimeMilliseconds(), period.JoinedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Unprivileged_recent_join_request_is_denied_before_it_can_queue_a_backfill()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var bot = new ZaloMemberIntelligenceBotService(
            fixture.Db,
            null!,
            fixture.Service,
            null!,
            null!,
            new AiAssistantService(new HttpClient(), new ConfigurationBuilder().Build(),
                NullLogger<AiAssistantService>.Instance),
            NullLogger<ZaloMemberIntelligenceBotService>.Instance);
        var question = "Có bao nhiêu thành viên mới trong 30 ngày?";

        var answer = await bot.TryHandleAsync("connection-a", "group-1",
            new ZaloIncomingMessageEvent("bot-a", "bot-a", "group-1", "msg-1", "member-a",
                "An", question, [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            question,
            CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.ListRecentlyJoinedMembers, answer.Intent);
        Assert.Contains("trưởng nhóm", answer.Text);
        Assert.Empty(await fixture.Db.ZaloActivityBackfillJobs.ToListAsync());
    }

    [Fact]
    public async Task Tracked_group_without_a_bot_session_accepts_real_events_and_isolates_accounts()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        await fixture.TrackAsync("connection-b", "group-1");
        var joinedAt = DateTimeOffset.UtcNow.AddDays(-3);

        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "join-1", joinedAt));
        Assert.False(await fixture.RecordAsync("bot-c", "join", "member-a", "join-2", joinedAt));

        var current = await fixture.Service.QueryRecentAsync("connection-a", "group-1", 7, DateTimeOffset.UtcNow);
        var member = Assert.Single(current.Members);
        Assert.Equal("member-a", member.ZaloUserId);
        Assert.Equal(joinedAt.ToUnixTimeMilliseconds(), member.JoinedAt.ToUnixTimeMilliseconds());
        Assert.False(member.IsRejoin);
        Assert.False(current.CoverageIsComplete);
        Assert.Empty((await fixture.Service.QueryRecentAsync("connection-b", "group-1", 7, DateTimeOffset.UtcNow)).Members);
    }

    [Fact]
    public async Task Missing_or_future_provider_timestamp_never_becomes_a_verified_join()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");

        Assert.False(await fixture.Service.RecordProviderEventAsync(
            new ZaloMembershipChangedEvent("bot-a", "group-1", "join", null,
                ["member-a"], "missing-time", 0)));
        Assert.False(await fixture.RecordAsync("bot-a", "join", "member-a", "future-time",
            DateTimeOffset.UtcNow.AddDays(1)));

        Assert.Empty(await fixture.Db.ZaloGroupMembershipPeriods.ToListAsync());
    }

    [Fact]
    public async Task Replayed_and_late_membership_events_do_not_resurrect_or_close_a_newer_period()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var now = DateTimeOffset.UtcNow;
        var firstJoin = now.AddDays(-30);
        var firstLeave = now.AddDays(-20);
        var rejoin = now.AddDays(-5);

        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "first-join", firstJoin));
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "first-join", firstJoin));
        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "first-leave", firstLeave));
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "rejoin", rejoin));
        Assert.True(await fixture.RecordAsync("bot-a", "remove_member", "member-a", "late-leave",
            now.AddDays(-25)));
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "late-join",
            now.AddDays(-29)));

        var periods = await fixture.Db.ZaloGroupMembershipPeriods
            .OrderBy(period => period.JoinedAt)
            .ToListAsync();
        Assert.Equal(2, periods.Count);
        Assert.Equal(firstLeave.ToUnixTimeMilliseconds(), periods[0].LeftAt!.Value.ToUnixTimeMilliseconds());
        Assert.True(periods[1].IsCurrentPeriod);
        Assert.Equal(ZaloMembershipEvidenceKind.ProviderRejoinEvent, periods[1].EvidenceKind);
        Assert.Equal(rejoin.ToUnixTimeMilliseconds(), periods[1].JoinedAt!.Value.ToUnixTimeMilliseconds());
        var recent = await fixture.Service.QueryRecentAsync("connection-a", "group-1", 7, now);
        Assert.True(Assert.Single(recent.Members).IsRejoin);
    }

    [Fact]
    public async Task Late_join_after_departure_and_old_leave_after_directory_observation_are_ignored()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var now = DateTimeOffset.UtcNow;
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "join-1", now.AddDays(-10)));
        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "leave-1", now.AddDays(-5)));
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "delayed-join", now.AddDays(-8)));
        Assert.Empty((await fixture.Service.QueryRecentAsync("connection-a", "group-1", 30, now)).Members);

        // This directory snapshot is requested after the earlier provider events
        // have been processed. Reusing a synthetic earlier observation time would
        // incorrectly describe an in-flight snapshot from before their delivery.
        var directoryObservedAt = DateTimeOffset.UtcNow;
        await fixture.Service.ObserveDirectoryAsync("connection-a", "group-1", ["member-a"], true,
            directoryObservedAt);
        await fixture.Db.SaveChangesAsync();
        Assert.True(await fixture.RecordAsync("bot-a", "leave", "member-a", "delayed-leave",
            now.AddDays(-3)));
        var observed = await fixture.Db.ZaloGroupMembershipPeriods.SingleAsync(period => period.IsCurrentPeriod);
        Assert.Equal(ZaloMembershipEvidenceKind.ObservedOnly, observed.EvidenceKind);
        Assert.Null(observed.JoinedAt);
    }

    [Fact]
    public async Task First_seen_is_observation_only_but_an_earlier_real_join_can_verify_it()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        await fixture.TrackAsync("connection-a", "group-1");
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await fixture.Service.ObserveDirectoryAsync("connection-a", "group-1", ["member-a"], true, observedAt);
        fixture.Db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            ZaloConnectionId = "connection-a", GroupId = "group-1", ZaloUserId = "member-a",
            DisplayName = "An", FirstSeenAt = observedAt, LastSeenAt = observedAt,
            LastSyncedAt = observedAt
        });
        await fixture.Db.SaveChangesAsync();

        var before = await fixture.Service.GetJoinDateAsync("connection-a", "group-1", "member-a");
        Assert.NotNull(before);
        Assert.False(before.HasVerifiedEvidence);
        Assert.Null(before.JoinedAt);

        var actualJoin = observedAt.AddDays(-4);
        Assert.True(await fixture.RecordAsync("bot-a", "join", "member-a", "provider-join", actualJoin));
        var after = await fixture.Service.GetJoinDateAsync("connection-a", "group-1", "member-a");
        Assert.True(after!.HasVerifiedEvidence);
        Assert.Equal(actualJoin.ToUnixTimeMilliseconds(), after.JoinedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal("An", after.DisplayName);
        Assert.Equal(observedAt, (await fixture.Db.ZaloGroupMembers.SingleAsync()).FirstSeenAt);
    }

    private sealed class MembershipFixture(SqliteConnection connection, VolleyDraftDbContext db) : IAsyncDisposable
    {
        public VolleyDraftDbContext Db { get; } = db;
        public ZaloMembershipHistoryService Service { get; } = new(db, NullLogger<ZaloMembershipHistoryService>.Instance);

        public static async Task<MembershipFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User { Id = "admin", DisplayName = "Admin", Email = "admin@membership.test", PasswordHash = "hash" });
            db.ZaloConnections.AddRange(
                new ZaloConnection { Id = "connection-a", AdminUserId = "admin", AccountZaloId = "bot-a", Status = ZaloConnectionStatus.Connected },
                new ZaloConnection { Id = "connection-b", AdminUserId = "admin", AccountZaloId = "bot-b", Status = ZaloConnectionStatus.Connected });
            await db.SaveChangesAsync();
            return new MembershipFixture(connection, db);
        }

        public async Task TrackAsync(string connectionId, string groupId) =>
            _ = await new ZaloAutoSessionSettingsStore(Db).InsertIfMissingAsync(new ZaloTrackedGroupData
            {
                AdminUserId = "admin", ZaloConnectionId = connectionId,
                GroupId = groupId, GroupName = "Test group"
            });

        public Task<bool> RecordAsync(string accountId, string type, string memberId, string eventId, DateTimeOffset at) =>
            Service.RecordProviderEventAsync(new ZaloMembershipChangedEvent(accountId, "group-1", type,
                null, [memberId], eventId, at.ToUnixTimeMilliseconds()));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
