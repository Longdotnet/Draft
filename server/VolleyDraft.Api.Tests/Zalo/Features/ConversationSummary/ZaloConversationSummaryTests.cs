using System.Net;
using System.Text;
using System.Text.Json;
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

public sealed class ZaloConversationSummaryTests
{
    [Fact]
    public void Vietnam_calendar_range_is_converted_to_utc_before_database_query()
    {
        var now = DateTimeOffset.Parse("2026-10-26T00:10:00+07:00");

        var yesterday = ZaloConversationSummaryService.ResolveRange(
            ZaloConversationSummaryTimeScope.Yesterday,
            now);

        Assert.Equal(TimeSpan.Zero, yesterday.Start.Offset);
        Assert.Equal(TimeSpan.Zero, yesterday.End.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T17:00:00Z"), yesterday.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T16:59:59.9999999Z"), yesterday.End);
    }

    [Fact]
    public async Task Current_conversation_starts_after_last_ninety_minute_gap()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.AddMessage("old-1", "u1", "Long", "chuyện cũ một", now.AddHours(-5));
        fixture.AddMessage("old-2", "u2", "Vũ", "chuyện cũ hai", now.AddHours(-4).AddMinutes(-50));
        fixture.AddMessage("recent-1", "u1", "Long", "bàn team đi giải", now.AddMinutes(-35));
        fixture.AddMessage("recent-2", "u2", "Vũ", "gom bốn người tập bài", now.AddMinutes(-20));
        await fixture.Db.SaveChangesAsync();

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q1", "sender", "tóm tắt nãy giờ", now),
            "tóm tắt nãy giờ", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SummarizeConversation, answer.Intent);
        var ids = await fixture.LoadSavedAllTopicMessageIdsAsync("sender");
        Assert.Contains("recent-1", ids);
        Assert.Contains("recent-2", ids);
        Assert.DoesNotContain("old-1", ids);
        Assert.DoesNotContain("old-2", ids);
    }

    [Fact]
    public async Task Yesterday_scope_does_not_include_midnight_today_in_vietnam()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-26T00:10:00+07:00");
        fixture.AddMessage("yesterday-last", "u1", "Long", "tin 23:59 hôm qua", DateTimeOffset.Parse("2026-10-25T23:59:00+07:00"));
        fixture.AddMessage("today-midnight", "u2", "Vũ", "tin đúng 00:00 hôm nay", DateTimeOffset.Parse("2026-10-26T00:00:00+07:00"));
        await fixture.Db.SaveChangesAsync();

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q2", "sender", "tóm tắt hôm qua", now),
            "tóm tắt hôm qua", false, CancellationToken.None);

        Assert.NotNull(answer);
        var ids = await fixture.LoadSavedTopicMessageIdsAsync("sender");
        Assert.Contains("yesterday-last", ids);
        Assert.DoesNotContain("today-midnight", ids);
    }

    [Fact]
    public async Task Structured_mention_uid_wins_when_member_names_are_duplicated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.Db.ZaloGroupMembers.AddRange(
            Member("target-real", "Nguyễn Hoàng Vũ", now),
            Member("target-decoy", "Nguyễn Hoàng Vũ", now));
        fixture.AddMessage("real-msg", "target-real", "Nguyễn Hoàng Vũ", "tui tính gom bốn người đi giải", now.AddMinutes(-20));
        fixture.AddMessage("decoy-msg", "target-decoy", "Nguyễn Hoàng Vũ", "tui nói chuyện khác", now.AddMinutes(-15));
        await fixture.Db.SaveChangesAsync();
        var question = "tóm tắt cuộc trò chuyện của @Nguyễn Hoàng Vũ nãy giờ";
        var label = "@Nguyễn Hoàng Vũ";
        var incoming = Incoming(
            "q3",
            "sender",
            question,
            now,
            [new ZaloBridgeMention("target-real", question.IndexOf(label, StringComparison.Ordinal), label.Length)]);

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", incoming, question, false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SummarizeMemberConversation, answer.Intent);
        var row = await fixture.Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync();
        Assert.Equal("target-real", row.TargetZaloUserId);
        var ids = ReadTopicMessageIds(row.SummaryJson);
        Assert.Contains("real-msg", ids);
        Assert.DoesNotContain("decoy-msg", ids);
    }

    [Fact]
    public async Task Ai_semantic_probe_understands_slang_outside_phrase_bank()
    {
        var classifier = """{"kind":"HotTopics","confidence":0.97,"timeScope":"SinceMorning","memberReference":null,"topicReference":null,"topicOrdinal":null}""";
        var summary = """{"overview":"Sáng giờ có một vụ đáng chú ý.","topics":[{"id":"topic-1","title":"Team đi giải","summary":"Mọi người đang bàn đội hình đi giải.","messageIds":["m1"],"participantIds":["u1"],"decisions":[],"openQuestions":["Chưa chốt đủ người"],"importance":0.9}]}""";
        var (ai, handler) = CreateConfiguredAi(classifier, summary);
        await using var fixture = await Fixture.CreateAsync(ai);
        var now = DateTimeOffset.Parse("2026-10-25T11:00:00+07:00");
        fixture.AddMessage("m1", "u1", "Long", "đang bàn team đi giải", DateTimeOffset.Parse("2026-10-25T08:15:00+07:00"));
        await fixture.Db.SaveChangesAsync();
        const string question = "group từ sáng tới giờ có món nào đáng hóng hông ta?";

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q4", "sender", question, now),
            question, true, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SummarizeHotTopics, answer.Intent);
        Assert.Contains("Team đi giải", answer.Text);
        Assert.Contains("Từ sáng tới giờ", answer.Text);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Ai_understands_natural_member_summary_without_structured_mention()
    {
        var classifier = """{"kind":"MemberSummary","confidence":0.96,"timeScope":"CurrentConversation","memberReference":"Vũ","topicReference":null,"topicOrdinal":null}""";
        var summary = """{"overview":"Vũ chủ yếu bàn chuyện team đi giải.","topics":[{"id":"topic-1","title":"Team đi giải","summary":"Vũ nói đang tính gom bốn người để tập.","messageIds":["vu-msg"],"participantIds":["vu-uid"],"decisions":[],"openQuestions":["Chưa chốt danh sách cuối"],"importance":0.9}]}""";
        var (ai, handler) = CreateConfiguredAi(classifier, summary);
        await using var fixture = await Fixture.CreateAsync(ai);
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.Db.ZaloGroupMembers.Add(Member("vu-uid", "Nguyễn Hoàng Vũ", now));
        fixture.AddMessage("other-msg", "u1", "Long", "team mình tính sao", now.AddMinutes(-25));
        fixture.AddMessage("vu-msg", "vu-uid", "Nguyễn Hoàng Vũ", "tui đang tính gom bốn người để tập", now.AddMinutes(-20));
        await fixture.Db.SaveChangesAsync();
        const string question = "nãy giờ Vũ nói gì vậy?";

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-member", "sender", question, now),
            question, true, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SummarizeMemberConversation, answer.Intent);
        Assert.Contains("Team đi giải", answer.Text);
        Assert.Contains("Vũ nói đang tính gom bốn người để tập", answer.Text);
        var row = await fixture.Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync();
        Assert.Equal("vu-uid", row.TargetZaloUserId);
        var ids = ReadTopicMessageIds(row.SummaryJson);
        Assert.Equal(["vu-msg"], ids);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Ai_summary_drops_fabricated_message_and_participant_ids()
    {
        var response = """{"overview":"ok","topics":[{"id":"t1","title":"Long nói nội dung","summary":"Long nói nội dung đã lưu.","messageIds":["real","fake"],"participantIds":["u1","fake-user"],"decisions":[],"openQuestions":[],"importance":0.8}]}""";
        var (ai, _) = CreateConfiguredAi(response);
        var result = await ai.SummarizeConversationAsync(
            new ZaloConversationSummaryAiContext(
                ZaloConversationSummaryKind.GroupSummary,
                "tóm tắt",
                null,
                null,
                "hôm nay",
                [new ZaloConversationSummaryCandidate("real", DateTimeOffset.UtcNow, "u1", "Long", "nội dung", null, false)]),
            CancellationToken.None);

        Assert.NotNull(result);
        var topic = Assert.Single(result.Topics);
        Assert.Equal(["real"], topic.MessageIds);
        Assert.Equal(["u1"], topic.ParticipantIds);
    }

    [Fact]
    public async Task Ai_summary_rejects_story_text_not_supported_by_its_real_message_ids()
    {
        var response = """{"overview":"ok","topics":[{"id":"t1","title":"Vivian làm mất ví","summary":"Vivian báo làm mất ví và nhờ mọi người tìm giúp.","messageIds":["real"],"participantIds":["u1"],"decisions":[],"openQuestions":[],"importance":0.8}]}""";
        var (ai, _) = CreateConfiguredAi(response);

        var result = await ai.SummarizeConversationAsync(
            new ZaloConversationSummaryAiContext(
                ZaloConversationSummaryKind.GroupSummary,
                "tóm tắt",
                null,
                null,
                "hôm nay",
                [new ZaloConversationSummaryCandidate("real", DateTimeOffset.UtcNow, "u1", "Vivian", "mình góp ý cách xếp team cần cân bằng hơn", null, false)]),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Member_follow_up_ai_cannot_select_same_name_decoy_message()
    {
        var response = """{"answer":"Vũ đồng ý gom team.","messageIds":["decoy","real"]}""";
        var (ai, _) = CreateConfiguredAi(response);
        var result = await ai.AnswerConversationSummaryFollowUpAsync(
            new ZaloConversationSummaryFollowUpAiContext(
                "Vũ nói gì về vụ đó?",
                new ZaloConversationSummaryTopic("t1", "Team đi giải", "Bàn team", ["real", "decoy"], ["target", "decoy-user"], [], [], .9),
                "target",
                "Vũ",
                [
                    new ZaloConversationSummaryCandidate("real", DateTimeOffset.UtcNow, "target", "Vũ", "tui đồng ý gom team", null, true),
                    new ZaloConversationSummaryCandidate("decoy", DateTimeOffset.UtcNow, "decoy-user", "Vũ", "tui không đồng ý", null, false)
                ]),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["real"], result.MessageIds);
    }

    [Fact]
    public async Task Follow_up_by_topic_number_only_uses_saved_topic_evidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.AddMessage("m1", "u1", "Long", "chuyện lịch đánh", now.AddMinutes(-30));
        fixture.AddMessage("m2", "u2", "Vũ", "team đi giải gồm bốn người", now.AddMinutes(-20));
        await fixture.Db.SaveChangesAsync();
        await fixture.SaveMemoryAsync("sender", now,
            new ZaloConversationSummaryMemory(
                "hai chủ đề",
                [
                    new ZaloConversationSummaryTopic("t1", "Lịch đánh", "Bàn lịch", ["m1"], ["u1"], [], [], .7),
                    new ZaloConversationSummaryTopic("t2", "Team đi giải", "Bàn team đi giải", ["m2"], ["u2"], [], ["Chưa chốt"], .9)
                ]));

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q5", "sender", "vụ số 2 nói kỹ hơn", now),
            "vụ số 2 nói kỹ hơn", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.ConversationSummaryFollowUp, answer.Intent);
        Assert.Contains("Team đi giải", answer.Text);
        Assert.Contains("team đi giải gồm bốn người", answer.Text);
        Assert.DoesNotContain("chuyện lịch đánh", answer.Text);
    }

    [Fact]
    public async Task Vague_follow_up_with_multiple_topics_asks_which_topic()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        await fixture.SaveMemoryAsync("sender", now,
            new ZaloConversationSummaryMemory(
                "hai chủ đề",
                [
                    new ZaloConversationSummaryTopic("t1", "Lịch đánh", "Bàn lịch", ["m1"], ["u1"], [], [], .7),
                    new ZaloConversationSummaryTopic("t2", "Team đi giải", "Bàn team", ["m2"], ["u2"], [], [], .8)
                ]));

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q6", "sender", "nói kỹ hơn", now),
            "nói kỹ hơn", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("1. Lịch đánh", answer.Text);
        Assert.Contains("2. Team đi giải", answer.Text);
    }

    [Fact]
    public async Task Mentioned_member_follow_up_filters_same_name_evidence_by_uid_without_ai()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.Db.ZaloGroupMembers.AddRange(
            Member("target-real", "Vũ", now),
            Member("target-decoy", "Vũ", now));
        fixture.AddMessage("real", "target-real", "Vũ", "tui đồng ý gom team", now.AddMinutes(-20));
        fixture.AddMessage("decoy", "target-decoy", "Vũ", "tui không liên quan vụ này", now.AddMinutes(-19));
        await fixture.Db.SaveChangesAsync();
        await fixture.SaveMemoryAsync("sender", now,
            new ZaloConversationSummaryMemory(
                "một chủ đề",
                [new ZaloConversationSummaryTopic("t1", "Team đi giải", "Bàn chuyện team", ["real", "decoy"], ["target-real", "target-decoy"], [], [], .9)]));
        const string question = "@Vũ nói gì về vụ đó?";
        var incoming = Incoming("q7", "sender", question, now, [new ZaloBridgeMention("target-real", 0, 3)]);

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", incoming, question, false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("tui đồng ý gom team", answer.Text);
        Assert.DoesNotContain("tui không liên quan", answer.Text);
    }

    [Fact]
    public async Task Generic_follow_up_after_member_summary_keeps_saved_target_uid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-25T17:00:00Z");
        fixture.AddMessage("real", "target-real", "Vũ", "tui đồng ý gom team để tập", now.AddMinutes(-20));
        fixture.AddMessage("decoy", "target-decoy", "Vũ", "tui không liên quan vụ này", now.AddMinutes(-19));
        await fixture.Db.SaveChangesAsync();
        await fixture.SaveMemoryAsync("sender", now,
            new ZaloConversationSummaryMemory(
                "một chủ đề",
                [new ZaloConversationSummaryTopic("t1", "Vũ bàn team", "Vũ nói về team", ["real", "decoy"], ["target-real", "target-decoy"], [], [], .9)],
                TargetZaloUserId: "target-real",
                TargetDisplayName: "Vũ"));

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-follow-saved-target", "sender", "vụ đó nói kỹ hơn", now),
            "vụ đó nói kỹ hơn", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("tui đồng ý gom team để tập", answer.Text);
        Assert.DoesNotContain("tui không liên quan vụ này", answer.Text);
    }

    [Fact]
    public async Task Grounded_fallback_keeps_direct_delayed_reply_in_original_story()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-26T01:10:00+07:00");
        var parentTime = DateTimeOffset.Parse("2026-10-25T10:00:00+07:00");
        var replyTime = parentTime.AddMinutes(75);
        fixture.AddMessage("story-parent", "vivian", "Vivian",
            "Mình góp ý cách xếp team hiện tại đang mất cân bằng, nên để bot hỗ trợ chia lại cho công bằng.", parentTime);
        fixture.AddMessage("story-reply", "long", "Thanh Long",
            "Anh hiểu ý em, phần này anh sẽ nhắc mọi người lưu ý hơn.", replyTime);
        fixture.AddMessage("noise-late", "other", "Người khác", "chiều nay ai đi uống cà phê không", replyTime.AddMinutes(-5));
        await fixture.Db.SaveChangesAsync();
        await new ZaloMessageGraphStore(fixture.Db).RememberIncomingQuoteAsync(
            "connection-a",
            new ZaloIncomingMessageEvent(
                "bot-a", "bot-a", "group-1", "story-reply", "long", "Thanh Long",
                "Anh hiểu ý em, phần này anh sẽ nhắc mọi người lưu ý hơn.", [], false,
                replyTime.ToUnixTimeMilliseconds(),
                new ZaloBridgeMessageQuote(
                    "story-parent", "vivian", "Vivian",
                    "Mình góp ý cách xếp team hiện tại đang mất cân bằng, nên để bot hỗ trợ chia lại cho công bằng.",
                    "chat", parentTime.ToUnixTimeMilliseconds(), null)));

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-delayed-reply", "sender", "tóm tắt hôm qua", now),
            "tóm tắt hôm qua", false, CancellationToken.None);

        Assert.NotNull(answer);
        var row = await fixture.Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync();
        using var doc = JsonDocument.Parse(row.SummaryJson);
        var topic = doc.RootElement.GetProperty("topics").EnumerateArray()
            .Single(item => item.GetProperty("messageIds").EnumerateArray().Any(id => id.GetString() == "story-parent"));
        var ids = topic.GetProperty("messageIds").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Contains("story-parent", ids);
        Assert.Contains("story-reply", ids);
        Assert.DoesNotContain("noise-late", ids);
    }

    [Fact]
    public async Task Grounded_fallback_does_not_dedupe_repeated_keyword_stories_hours_apart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-26T01:10:00+07:00");
        fixture.AddMessage("morning-team", "u1", "Long", "Sáng nay team đi giải đang bàn danh sách bốn người tập bài", DateTimeOffset.Parse("2026-10-25T08:00:00+07:00"));
        fixture.AddMessage("morning-reply", "u2", "Vũ", "Team sáng chốt tập bài trước rồi tính danh sách giải", DateTimeOffset.Parse("2026-10-25T08:05:00+07:00"));
        fixture.AddMessage("evening-team", "u3", "Nick", "Tối nay team đi giải khác đang hỏi danh sách bốn người tập bài", DateTimeOffset.Parse("2026-10-25T20:00:00+07:00"));
        fixture.AddMessage("evening-reply", "u4", "Sin", "Team tối này là nhóm khác, chưa chốt người đánh giải", DateTimeOffset.Parse("2026-10-25T20:05:00+07:00"));
        await fixture.Db.SaveChangesAsync();

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-repeat-story", "sender", "tóm tắt hôm qua", now),
            "tóm tắt hôm qua", false, CancellationToken.None);

        Assert.NotNull(answer);
        var row = await fixture.Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync();
        using var doc = JsonDocument.Parse(row.SummaryJson);
        var topicIds = doc.RootElement.GetProperty("topics").EnumerateArray()
            .Select(topic => topic.GetProperty("messageIds").EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal))
            .ToList();
        Assert.Contains(topicIds, ids => ids.Contains("morning-team") && !ids.Contains("evening-team"));
        Assert.Contains(topicIds, ids => ids.Contains("evening-team") && !ids.Contains("morning-team"));
    }

    [Fact]
    public async Task Grounded_fallback_summarizes_111_messages_into_topics_with_real_evidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-26T01:10:00+07:00");
        var sequence = 0;
        AddCluster(27, DateTimeOffset.Parse("2026-10-25T08:00:00+07:00"), i =>
            i % 3 == 0 ? "team đi giải mình chốt ai tập bài vậy?" : $"bàn team đi giải lượt {i}");
        AddCluster(28, DateTimeOffset.Parse("2026-10-25T12:00:00+07:00"), i =>
            i % 4 == 0 ? "vote sân tuần sau mấy giờ đánh?" : $"bàn lịch sân và slot lượt {i}");
        AddCluster(28, DateTimeOffset.Parse("2026-10-25T17:00:00+07:00"), i =>
            i % 5 == 0 ? "@All bạn mới cần hỗ trợ thì @bot help nha" : $"hướng dẫn bot help lượt {i}");
        AddCluster(28, DateTimeOffset.Parse("2026-10-25T22:00:00+07:00"), i =>
            i % 4 == 0 ? "mình set thêm vị trí chuyền hai được ko?" : $"cập nhật vị trí và trình độ lượt {i}");
        await fixture.Db.SaveChangesAsync();

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-fallback-111", "sender", "tóm tắt hôm qua có gì", now),
            "tóm tắt hôm qua có gì", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(ZaloBotIntent.SummarizeConversation, answer.Intent);
        Assert.Contains("chuyện chính", answer.Text);
        Assert.Contains("Mọi người bàn về đội đi giải", answer.Text);
        Assert.Contains("Trao đổi về lịch chơi / sân / slot", answer.Text);
        Assert.Contains("Hướng dẫn thành viên sử dụng bot", answer.Text);
        Assert.Contains("Bàn về vị trí / trình độ thành viên", answer.Text);
        Assert.Contains("↳", answer.Text);
        Assert.Contains("team đi giải", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tin từ", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chỉ giữ phần dữ liệu gốc gần nhất", answer.Text);

        void AddCluster(int count, DateTimeOffset start, Func<int, string> content)
        {
            for (var i = 0; i < count; i++)
            {
                fixture.AddMessage(
                    $"fallback-{++sequence}",
                    $"u{(i % 6) + 1}",
                    $"Member {(i % 6) + 1}",
                    content(i),
                    start.AddMinutes(i));
            }
        }
    }

    [Fact]
    public async Task Grounded_fallback_reconstructs_vivian_team_complaint_as_story_and_excludes_nearby_bottle_chat()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.Parse("2026-10-26T01:10:00+07:00");
        fixture.AddMessage(
            "vivian-main", "vivian", "Vivian",
            "Em có đôi điều muốn nói luôn ạ. Thật sự em không thích cách mng tự chọn cho mình nguyên 1 team luôn vì như vậy là không tôn trọng những người còn lại. Em nghĩ chỉ nên ghép 2-3 người thôi, còn lại để BOT cân bằng. Hổm nay cứ pick team mạnh, team khác lại thiếu vị trí hỗ trợ nên mất cân bằng.",
            DateTimeOffset.Parse("2026-10-25T19:47:00+07:00"));
        fixture.AddMessage(
            "phan-rule", "phan", "Phan Nguyên",
            "Oke từ nay mọi người lưu ý ghép tối đa 3 người thôi nha, để cho cân bằng",
            DateTimeOffset.Parse("2026-10-25T19:57:00+07:00"));
        fixture.AddMessage(
            "bottle-1", "thanh", "Thanh Thanh",
            "Cả nhà ơi em để quên cái bình trên sân, ai lấy giúp em với",
            DateTimeOffset.Parse("2026-10-25T20:02:00+07:00"));
        fixture.AddMessage(
            "bottle-2", "phan", "Phan Nguyên",
            "@Thanh Thanh đặc điểm bình như thế nào em",
            DateTimeOffset.Parse("2026-10-25T20:03:00+07:00"));
        fixture.AddMessage(
            "long-asks", "long", "Thanh Long",
            "@Vivian sao thế em, em khó chịu gì hả",
            DateTimeOffset.Parse("2026-10-25T20:20:00+07:00"));
        fixture.AddMessage(
            "vivian-reply", "vivian", "Vivian",
            "@Thanh Long dạ em đã viết đầy đủ ý của mình ở trên rồi ạ. Chuyện này xảy ra không chỉ hôm nay và không riêng gì Vũ nha.",
            DateTimeOffset.Parse("2026-10-25T20:21:00+07:00"));
        fixture.AddMessage(
            "team-tournament", "nick", "Nick Tran",
            "@Nguyễn Hoàng Vũ em có gom team đi đánh giải thiệt không, sao anh hỏi mấy bạn nói không biết",
            DateTimeOffset.Parse("2026-10-25T20:27:00+07:00"));
        await fixture.Db.SaveChangesAsync();

        var answer = await fixture.Bot.TryHandleConversationSummaryAsync(
            "connection-a", "group-1", Incoming("q-story-vivian", "sender", "tóm tắt hôm qua có gì", now),
            "tóm tắt hôm qua có gì", false, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("Vivian phản ánh việc xếp team thiếu cân bằng", answer.Text);
        Assert.Contains("Vivian cho rằng việc mọi người tự chọn nguyên team", answer.Text);
        Assert.Contains("Phan Nguyên", answer.Text);
        Assert.Contains("thống nhất từ nay chỉ ghép tối đa 3 người", answer.Text);

        var row = await fixture.Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync();
        using var doc = JsonDocument.Parse(row.SummaryJson);
        var vivianTopic = doc.RootElement.GetProperty("topics").EnumerateArray()
            .Single(topic => (topic.GetProperty("title").GetString() ?? string.Empty).Contains("Vivian", StringComparison.Ordinal));
        var storyIds = vivianTopic.GetProperty("messageIds").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Contains("vivian-main", storyIds);
        Assert.Contains("phan-rule", storyIds);
        Assert.Contains("long-asks", storyIds);
        Assert.DoesNotContain("bottle-1", storyIds);
        Assert.DoesNotContain("bottle-2", storyIds);
        Assert.DoesNotContain("team-tournament", storyIds);
    }

    private static ZaloGroupMember Member(string userId, string displayName, DateTimeOffset now) => new()
    {
        ZaloConnectionId = "connection-a",
        GroupId = "group-1",
        ZaloUserId = userId,
        DisplayName = displayName,
        FirstSeenAt = now.AddDays(-30),
        LastSeenAt = now,
        LastSyncedAt = now,
        IsCurrentMember = true
    };

    private static ZaloIncomingMessageEvent Incoming(
        string messageId,
        string senderId,
        string content,
        DateTimeOffset sentAt,
        IReadOnlyList<ZaloBridgeMention>? mentions = null) =>
        new("bot-a", "bot-a", "group-1", messageId, senderId, "Người hỏi", content, mentions ?? [], true, sentAt.ToUnixTimeMilliseconds());

    private static IReadOnlyList<string> ReadTopicMessageIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("topics")[0].GetProperty("messageIds").EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    private static (AiAssistantService Service, SequenceAiHandler Handler) CreateConfiguredAi(params string[] responseContents)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();
        var handler = new SequenceAiHandler(responseContents);
        return (new AiAssistantService(new HttpClient(handler), configuration, NullLogger<AiAssistantService>.Instance), handler);
    }

    private sealed class Fixture(SqliteConnection connection, VolleyDraftDbContext db, ZaloMemberIntelligenceBotService bot) : IAsyncDisposable
    {
        public VolleyDraftDbContext Db { get; } = db;
        public ZaloMemberIntelligenceBotService Bot { get; } = bot;

        public static async Task<Fixture> CreateAsync(AiAssistantService? aiOverride = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User { Id = "admin", DisplayName = "Admin", Email = "admin@summary.test", PasswordHash = "hash" });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection-a",
                AdminUserId = "admin",
                AccountZaloId = "bot-a",
                DisplayName = "Bott",
                EncryptedCredentials = "x",
                Status = ZaloConnectionStatus.Connected
            });
            await db.SaveChangesAsync();
            var ai = aiOverride ?? new AiAssistantService(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<AiAssistantService>.Instance);
            var bot = new ZaloMemberIntelligenceBotService(db, null!, null!, null!, null!, ai, NullLogger<ZaloMemberIntelligenceBotService>.Instance);
            return new Fixture(connection, db, bot);
        }

        public void AddMessage(string id, string senderId, string senderName, string content, DateTimeOffset sentAt)
        {
            Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                MessageId = id,
                SenderId = senderId,
                SenderName = senderName,
                Content = content,
                SentAt = sentAt,
                ReceivedAt = sentAt
            });
        }

        public async Task<IReadOnlyList<string>> LoadSavedTopicMessageIdsAsync(string requesterId)
        {
            var row = await Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync(item => item.RequesterZaloUserId == requesterId);
            return ReadTopicMessageIds(row.SummaryJson);
        }

        public async Task<IReadOnlyList<string>> LoadSavedAllTopicMessageIdsAsync(string requesterId)
        {
            var row = await Db.ZaloConversationSummaryContexts.AsNoTracking().SingleAsync(item => item.RequesterZaloUserId == requesterId);
            using var doc = JsonDocument.Parse(row.SummaryJson);
            return doc.RootElement.GetProperty("topics").EnumerateArray()
                .SelectMany(topic => topic.GetProperty("messageIds").EnumerateArray())
                .Select(item => item.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public async Task SaveMemoryAsync(string requesterId, DateTimeOffset now, ZaloConversationSummaryMemory memory)
        {
            Db.ZaloConversationSummaryContexts.Add(new ZaloConversationSummaryContext
            {
                ZaloConnectionId = "connection-a",
                GroupId = "group-1",
                RequesterZaloUserId = requesterId,
                ScopeKind = "GroupSummary",
                RangeStart = now.AddHours(-2),
                RangeEnd = now,
                SummaryJson = JsonSerializer.Serialize(memory, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ExpiresAt = now.AddHours(6),
                CreatedAt = now,
                UpdatedAt = now
            });
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SequenceAiHandler(IReadOnlyList<string> responseContents) : HttpMessageHandler
    {
        private int index;
        public int CallCount => index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = responseContents[Math.Min(index, responseContents.Count - 1)];
            index += 1;
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
