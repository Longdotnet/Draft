using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloConversationSummaryService(
    VolleyDraftDbContext db,
    AiAssistantService ai)
{
    private const int MaxDbMessages = 500;
    private const int MaxAiCandidates = 160;
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan CurrentConversationGap = TimeSpan.FromMinutes(90);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> StoryStopWords = new(StringComparer.Ordinal)
    {
        "anh", "chi", "em", "minh", "ban", "moi", "nguoi", "nay", "do", "kia", "roi", "nha", "nhe", "voi",
        "cho", "cua", "mot", "nhung", "ma", "la", "thi", "co", "khong", "ko", "hong", "duoc", "dc", "dang",
        "cung", "gio", "hom", "qua", "nay", "luon", "lam", "ve", "trong", "ngoai", "them", "gi", "sao", "oi",
        "all", "bot", "bott", "noi", "hoi", "biet", "viet", "thay", "day", "du", "y", "ra", "xay", "chi",
        "rieng", "tu", "toi", "de", "nen", "thiet", "may", "ha", "a"
    };

    public async Task<ZaloConversationSummaryMemory?> LoadMemoryAsync(
        string connectionId,
        string groupId,
        string requesterId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await db.ZaloConversationSummaryContexts
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.RequesterZaloUserId == NormalizeId(requesterId) &&
                item.ExpiresAt > now,
                cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.SummaryJson)) return null;
        try
        {
            var memory = JsonSerializer.Deserialize<ZaloConversationSummaryMemory>(row.SummaryJson, JsonOptions);
            return memory is null
                ? null
                : memory with
                {
                    TargetZaloUserId = memory.TargetZaloUserId ?? row.TargetZaloUserId
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ZaloConversationSummaryHandleResult> SummarizeAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        ZaloConversationSummaryClassification classification,
        string? targetUserId,
        string? targetDisplayName,
        bool allowAi,
        CancellationToken cancellationToken)
    {
        var now = ResolveIncomingTime(incoming);
        var range = ResolveRange(classification.TimeScope, now);
        var messages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                !message.IsFromBot &&
                message.MessageId != NormalizeId(incoming.MessageId) &&
                message.SentAt >= range.Start &&
                message.SentAt <= range.End)
            .OrderByDescending(message => message.SentAt)
            .Take(MaxDbMessages)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.MessageId)
            .ToListAsync(cancellationToken);

        if (classification.TimeScope == ZaloConversationSummaryTimeScope.CurrentConversation)
            messages = SliceCurrentConversation(messages);

        var normalizedTarget = string.IsNullOrWhiteSpace(targetUserId) ? null : NormalizeId(targetUserId);
        if (classification.Kind == ZaloConversationSummaryKind.MemberSummary &&
            (normalizedTarget is null || messages.All(message => NormalizeId(message.SenderId) != normalizedTarget)))
        {
            var who = string.IsNullOrWhiteSpace(targetDisplayName) ? "người bạn hỏi" : targetDisplayName;
            return new ZaloConversationSummaryHandleResult(
                $"Tui chưa có tin nhắn nào của {who} trong {range.Description} mà bot đang lưu ở group này.",
                ZaloBotIntent.SummarizeMemberConversation,
                false,
                string.IsNullOrWhiteSpace(targetDisplayName) ? [] : [targetDisplayName]);
        }

        if (messages.Count == 0)
        {
            return new ZaloConversationSummaryHandleResult(
                $"Tui chưa có đủ tin nhắn trong {range.Description} để tóm tắt.",
                MapIntent(classification.Kind),
                false,
                []);
        }

        var relations = await new ZaloMessageGraphStore(db).LoadReplyRelationsAsync(connectionId, groupId, cancellationToken);
        var selected = SelectRepresentativeMessages(messages, normalizedTarget, relations);
        var candidates = selected.Select(message =>
        {
            relations.TryGetValue(message.MessageId, out var relation);
            return new ZaloConversationSummaryCandidate(
                message.MessageId,
                message.SentAt,
                NormalizeId(message.SenderId),
                Clean(message.SenderName, 120),
                SanitizeContent(message.Content, 360),
                BuildReplyContext(relation),
                normalizedTarget is not null && NormalizeId(message.SenderId) == normalizedTarget,
                relation?.ToMessageId,
                NormalizeId(relation?.QuotedSenderId));
        }).ToList();

        ZaloConversationSummaryAiResult? summary = null;
        var aiCalled = false;
        if (allowAi && ai.IsConfigured)
        {
            summary = await ai.SummarizeConversationAsync(
                new ZaloConversationSummaryAiContext(
                    classification.Kind,
                    question,
                    normalizedTarget,
                    targetDisplayName,
                    range.Description,
                    candidates),
                cancellationToken);
            aiCalled = true;
        }

        var usedFallback = summary is null;
        summary ??= BuildGroundedFallback(messages, normalizedTarget, targetDisplayName, range.Description, relations);
        var topics = summary.Topics
            .OrderByDescending(topic => classification.Kind == ZaloConversationSummaryKind.HotTopics ? CalculateHotScore(topic, selected, relations) : topic.Importance)
            .Take(5)
            .ToList();
        var memory = new ZaloConversationSummaryMemory(
            summary.Overview,
            topics,
            TargetZaloUserId: normalizedTarget,
            TargetDisplayName: targetDisplayName);
        await SaveMemoryAsync(
            connectionId,
            groupId,
            incoming.SenderId,
            classification.Kind,
            normalizedTarget,
            messages.Min(item => item.SentAt),
            messages.Max(item => item.SentAt),
            memory,
            now,
            cancellationToken);

        return new ZaloConversationSummaryHandleResult(
            FormatSummary(topics, classification.Kind, targetDisplayName, range.Description, messages, relations, usedFallback),
            MapIntent(classification.Kind),
            aiCalled,
            BuildProtectedTerms(topics, targetDisplayName));
    }

    public async Task<ZaloConversationSummaryHandleResult?> AnswerFollowUpAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        ZaloConversationSummaryClassification classification,
        ZaloConversationSummaryMemory memory,
        string? targetUserId,
        string? targetDisplayName,
        bool allowAi,
        CancellationToken cancellationToken)
    {
        var topic = ResolveTopic(memory, classification);
        if (topic is null)
        {
            if (memory.Topics.Count == 0) return null;
            var choices = string.Join("\n", memory.Topics.Select((item, index) => $"{index + 1}. {item.Title}"));
            return new ZaloConversationSummaryHandleResult(
                $"Bạn muốn hỏi kỹ phần nào trong bản tóm tắt?\n{choices}\n\nCó thể nói tự nhiên như `@bot vụ số 2 nói kỹ hơn`.",
                ZaloBotIntent.ConversationSummaryFollowUp,
                false,
                memory.Topics.Select(item => item.Title).ToList());
        }

        var topicIds = topic.MessageIds.ToHashSet(StringComparer.Ordinal);
        var relations = await new ZaloMessageGraphStore(db).LoadReplyRelationsAsync(connectionId, groupId, cancellationToken);
        var expandedTopicIds = ExpandTopicMessageIds(topicIds, relations, 80);
        var sourceMessages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                expandedTopicIds.Contains(message.MessageId) &&
                !message.IsFromBot)
            .OrderBy(message => message.SentAt)
            .ToListAsync(cancellationToken);
        if (sourceMessages.Count == 0)
            return null;

        var candidates = sourceMessages.Select(message =>
        {
            relations.TryGetValue(message.MessageId, out var relation);
            return new ZaloConversationSummaryCandidate(
                message.MessageId,
                message.SentAt,
                NormalizeId(message.SenderId),
                Clean(message.SenderName, 120),
                SanitizeContent(message.Content, 520),
                BuildReplyContext(relation),
                !string.IsNullOrWhiteSpace(targetUserId) && NormalizeId(message.SenderId) == NormalizeId(targetUserId),
                relation?.ToMessageId,
                NormalizeId(relation?.QuotedSenderId));
        }).ToList();

        ZaloConversationSummaryFollowUpAiResult? answer = null;
        var aiCalled = false;
        if (allowAi && ai.IsConfigured)
        {
            answer = await ai.AnswerConversationSummaryFollowUpAsync(
                new ZaloConversationSummaryFollowUpAiContext(question, topic, NormalizeId(targetUserId), targetDisplayName, candidates),
                cancellationToken);
            aiCalled = true;
        }

        var selectedIds = answer?.MessageIds.ToHashSet(StringComparer.Ordinal) ??
                          (!string.IsNullOrWhiteSpace(targetUserId)
                              ? sourceMessages
                                  .Where(item => NormalizeId(item.SenderId) == NormalizeId(targetUserId))
                                  .Select(item => item.MessageId)
                                  .ToHashSet(StringComparer.Ordinal)
                              : expandedTopicIds);
        var grounded = sourceMessages.Where(item => selectedIds.Contains(item.MessageId)).Take(4).ToList();
        if (grounded.Count == 0) grounded = sourceMessages.Take(4).ToList();
        var detail = answer?.Answer;
        if (string.IsNullOrWhiteSpace(detail))
            detail = !string.IsNullOrWhiteSpace(targetUserId)
                ? $"Lượt này chưa dùng được AI để diễn giải sâu, nên tui chỉ đưa đúng các tin của {targetDisplayName ?? "người bạn hỏi"} thuộc topic này ở phần căn cứ bên dưới."
                : topic.Summary + (topic.OpenQuestions.Count > 0 ? $" Phần còn mở: {string.Join("; ", topic.OpenQuestions)}." : string.Empty);
        var evidence = string.Join("\n", grounded.Select(item =>
            $"- {FormatVietnamTime(item.SentAt)} · {Clean(item.SenderName, 80)}: “{SanitizeContent(item.Content, 360)}”"));

        var updated = memory with { LastTopicId = topic.Id };
        await UpdateMemoryAsync(connectionId, groupId, incoming.SenderId, updated, ResolveIncomingTime(incoming), cancellationToken);
        return new ZaloConversationSummaryHandleResult(
            $"**{topic.Title}**\n{detail}\n\nTin nhắn làm căn cứ:\n{evidence}",
            ZaloBotIntent.ConversationSummaryFollowUp,
            aiCalled,
            [topic.Title, ..grounded.Select(item => SanitizeContent(item.Content, 360))]);
    }

    public async Task<(string? UserId, string? DisplayName, string? Error)> ResolveTargetMemberAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string? memberReference,
        CancellationToken cancellationToken)
    {
        var humanMentions = incoming.Mentions
            .Where(mention => NormalizeId(mention.Uid) != NormalizeId(incoming.BotId))
            .Select(mention => NormalizeId(mention.Uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (humanMentions.Count > 1)
            return (null, null, "Bạn đang tag nhiều người. Hãy tag đúng 1 người cần tóm tắt để tui không lấy nhầm UID.");

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member => member.ZaloConnectionId == connectionId && member.GroupId == groupId && member.IsCurrentMember)
            .ToListAsync(cancellationToken);
        if (humanMentions.Count == 1)
        {
            var id = humanMentions[0];
            var member = members.FirstOrDefault(item => NormalizeId(item.ZaloUserId) == id);
            return (id, member?.DisplayName ?? ExtractMentionLabel(incoming, id) ?? id, null);
        }

        var reference = ZaloBotIntelligence.Normalize(memberReference ?? string.Empty);
        if (string.IsNullOrWhiteSpace(reference))
        {
            var normalizedQuestion = ZaloBotIntelligence.Normalize(incoming.Content);
            var mentionedByName = members
                .Where(member =>
                {
                    var name = ZaloBotIntelligence.Normalize(member.DisplayName);
                    return name.Length >= 2 && normalizedQuestion.Contains(name, StringComparison.Ordinal);
                })
                .DistinctBy(member => member.ZaloUserId)
                .ToList();
            if (mentionedByName.Count == 1)
                return (NormalizeId(mentionedByName[0].ZaloUserId), mentionedByName[0].DisplayName, null);
            if (mentionedByName.Count > 1)
                return (null, null, "Có nhiều thành viên cùng khớp tên trong câu hỏi. Hãy @mention đúng người cần tóm tắt để tui khóa đúng UID.");
            return (null, null, "Tui hiểu bạn muốn tóm tắt một người, nhưng chưa xác định được ai. Hãy @mention hoặc ghi rõ tên người đó.");
        }
        var candidates = members
            .Where(member =>
            {
                var name = ZaloBotIntelligence.Normalize(member.DisplayName);
                return name == reference || name.Contains(reference, StringComparison.Ordinal) ||
                       reference.Contains(name, StringComparison.Ordinal) ||
                       ZaloBotIntelligence.TokenSimilarity(name, reference) >= .84;
            })
            .DistinctBy(member => member.ZaloUserId)
            .Take(10)
            .ToList();
        if (candidates.Count == 1)
            return (NormalizeId(candidates[0].ZaloUserId), candidates[0].DisplayName, null);
        if (candidates.Count == 0)
            return (null, null, $"Tui chưa xác định được `{memberReference}` là thành viên nào trong group. Tag người đó giúp tui để khóa đúng UID.");
        return (null, null, $"Có {candidates.Count} người khớp tên `{memberReference}`. Hãy @mention đúng người cần tóm tắt để tui không nhầm danh tính.");
    }

    private async Task SaveMemoryAsync(
        string connectionId,
        string groupId,
        string requesterId,
        ZaloConversationSummaryKind kind,
        string? targetUserId,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        ZaloConversationSummaryMemory memory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        requesterId = NormalizeId(requesterId);
        var row = await db.ZaloConversationSummaryContexts.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.RequesterZaloUserId == requesterId,
            cancellationToken);
        if (row is null)
        {
            row = new ZaloConversationSummaryContext
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                RequesterZaloUserId = requesterId
            };
            db.ZaloConversationSummaryContexts.Add(row);
        }
        row.ScopeKind = kind.ToString();
        row.TargetZaloUserId = targetUserId;
        row.RangeStart = rangeStart;
        row.RangeEnd = rangeEnd;
        row.SummaryJson = JsonSerializer.Serialize(memory, JsonOptions);
        row.ExpiresAt = now.AddHours(6);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateMemoryAsync(
        string connectionId,
        string groupId,
        string requesterId,
        ZaloConversationSummaryMemory memory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        requesterId = NormalizeId(requesterId);
        var row = await db.ZaloConversationSummaryContexts.SingleOrDefaultAsync(item =>
            item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.RequesterZaloUserId == requesterId,
            cancellationToken);
        if (row is null) return;
        row.SummaryJson = JsonSerializer.Serialize(memory, JsonOptions);
        row.ExpiresAt = now.AddHours(6);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<ZaloGroupMessage> SliceCurrentConversation(List<ZaloGroupMessage> messages)
    {
        if (messages.Count <= 1) return messages;
        var start = 0;
        for (var index = messages.Count - 1; index > 0; index--)
        {
            if (messages[index].SentAt - messages[index - 1].SentAt < CurrentConversationGap) continue;
            start = index;
            break;
        }
        return messages.Skip(start).ToList();
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectRepresentativeMessages(
        IReadOnlyList<ZaloGroupMessage> messages,
        string? targetUserId,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        if (messages.Count <= MaxAiCandidates) return messages;
        var priorities = new Dictionary<int, double>();
        void Add(int index, double priority)
        {
            if (index < 0 || index >= messages.Count) return;
            if (!priorities.TryGetValue(index, out var current) || priority > current)
                priorities[index] = priority;
        }

        if (targetUserId is not null)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (NormalizeId(messages[index].SenderId) != targetUserId) continue;
                Add(index, 100);
                Add(index - 1, 80);
                Add(index + 1, 80);
            }
        }

        var anchors = messages
            .Select((message, index) => new
            {
                Index = index,
                Score = CalculateStoryAnchorScore(message, relations)
            })
            .OrderByDescending(item => item.Score)
            .Take(70)
            .ToList();
        foreach (var anchor in anchors)
        {
            Add(anchor.Index, 50 + anchor.Score);
            Add(anchor.Index - 1, 35 + anchor.Score);
            Add(anchor.Index + 1, 35 + anchor.Score);
        }

        for (var index = Math.Max(0, messages.Count - 24); index < messages.Count; index++)
            Add(index, 25);

        var indexByMessageId = messages
            .Select((message, index) => new { message.MessageId, Index = index })
            .ToDictionary(item => item.MessageId, item => item.Index, StringComparer.Ordinal);
        foreach (var relation in relations.Values)
        {
            if (indexByMessageId.TryGetValue(relation.FromMessageId, out var fromIndex)) Add(fromIndex, 72);
            if (!string.IsNullOrWhiteSpace(relation.ToMessageId) && indexByMessageId.TryGetValue(relation.ToMessageId, out var toIndex)) Add(toIndex, 76);
        }

        var evenly = 36;
        var stride = Math.Max(1d, (messages.Count - 1d) / Math.Max(1, evenly - 1));
        for (var sample = 0; sample < evenly; sample++)
            Add(Math.Min(messages.Count - 1, (int)Math.Round(sample * stride)), 10);

        return priorities
            .OrderByDescending(item => item.Value)
            .ThenByDescending(item => messages[item.Key].SentAt)
            .Take(MaxAiCandidates)
            .Select(item => item.Key)
            .OrderBy(index => index)
            .Select(index => messages[index])
            .ToList();
    }

    private static ZaloConversationSummaryAiResult BuildGroundedFallback(
        IReadOnlyList<ZaloGroupMessage> messages,
        string? targetUserId,
        string? targetDisplayName,
        string rangeDescription,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var targetMessages = targetUserId is null
            ? messages
            : messages.Where(item => NormalizeId(item.SenderId) == targetUserId).ToList();
        var stories = BuildFallbackStories(messages, relations, targetUserId).Take(5).ToList();

        var topics = new List<ZaloConversationSummaryTopic>();
        for (var index = 0; index < stories.Count; index++)
        {
            var story = stories[index];
            var segment = story.Messages;
            var evidence = SelectFallbackStoryEvidence(story, relations, 24);
            var ids = evidence.Select(item => item.MessageId).ToList();
            if (ids.Count == 0) continue;
            var participants = segment
                .Select(item => NormalizeId(item.SenderId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var decisions = segment
                .Where(item => LooksLikeDecision(item.Content))
                .OrderByDescending(item => CalculateMessageSignal(item, relations))
                .Take(2)
                .Select(item => $"{Clean(item.SenderName, 60)}: “{SanitizeContent(item.Content, 180)}”")
                .ToList();
            var questions = segment
                .Where(item => LooksLikeQuestion(item.Content))
                .OrderByDescending(item => CalculateMessageSignal(item, relations))
                .Take(2)
                .Select(item => $"{Clean(item.SenderName, 60)}: “{SanitizeContent(item.Content, 180)}”")
                .ToList();
            topics.Add(new ZaloConversationSummaryTopic(
                $"topic-{index + 1}",
                InferFallbackStoryTitle(story, targetDisplayName),
                BuildFallbackStoryNarrative(story, relations),
                ids,
                participants,
                decisions,
                questions,
                Math.Min(1, story.Score / 20d)));
        }

        if (topics.Count == 0)
        {
            var ids = targetMessages.TakeLast(8).Select(item => item.MessageId).ToList();
            topics.Add(new ZaloConversationSummaryTopic(
                "topic-1",
                targetUserId is null ? "Đoạn chat gần đây" : $"Phần {targetDisplayName ?? "thành viên"} đã nói",
                $"Có {targetMessages.Count} tin nhắn trong {rangeDescription}.",
                ids,
                targetMessages.TakeLast(8).Select(item => NormalizeId(item.SenderId)).Distinct().ToList(),
                [],
                [],
                .5));
        }

        var lead = RangeLead(rangeDescription);
        var overview = targetUserId is null
            ? $"{lead} nhóm có khoảng {topics.Count} chuyện chính."
            : $"{lead}, {targetDisplayName ?? "người bạn hỏi"} có khoảng {topics.Count} chuyện đáng chú ý.";
        return new ZaloConversationSummaryAiResult(overview, topics);
    }

    private static double CalculateHotScore(
        ZaloConversationSummaryTopic topic,
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var ids = topic.MessageIds.ToHashSet(StringComparer.Ordinal);
        var participants = messages.Where(item => ids.Contains(item.MessageId)).Select(item => NormalizeId(item.SenderId)).Distinct().Count();
        var replies = topic.MessageIds.Count(relations.ContainsKey);
        return topic.Importance * 10 + participants * 2 + replies * 1.5 + topic.Decisions.Count + topic.OpenQuestions.Count * .5;
    }

    private static string FormatSummary(
        IReadOnlyList<ZaloConversationSummaryTopic> topics,
        ZaloConversationSummaryKind kind,
        string? targetDisplayName,
        string rangeDescription,
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        bool usedFallback)
    {
        var lead = RangeLead(rangeDescription);
        var intro = kind switch
        {
            ZaloConversationSummaryKind.MemberSummary => $"{lead}, {targetDisplayName ?? "người bạn hỏi"} chủ yếu có khoảng {topics.Count} chuyện đáng chú ý.",
            ZaloConversationSummaryKind.HotTopics => $"{lead} có khoảng {topics.Count} chuyện đáng chú ý.",
            _ => $"{lead} nhóm có khoảng {topics.Count} chuyện chính."
        };
        var lines = new List<string> { intro };
        for (var index = 0; index < topics.Count; index++)
        {
            var topic = topics[index];
            lines.Add($"{index + 1}. {topic.Title}: {topic.Summary}");
            var evidence = SelectTopicEvidenceInTopicOrder(topic, messages, 3);
            foreach (var item in evidence)
                lines.Add($"   ↳ {FormatVietnamTime(item.SentAt)} · {Clean(item.SenderName, 60)}: “{SanitizeContent(item.Content, 200)}”");
            if (index + 1 < topics.Count) lines.Add(string.Empty);
        }
        if (topics.Count > 0)
            lines.Add("Bạn có thể hỏi tiếp tự nhiên như `vụ số 2 nói kỹ hơn` hoặc `cái vụ team đi giải ai đề xuất vậy?`.");
        if (usedFallback)
            lines.Add("Nguồn: dữ liệu chat đã lưu; các dòng ↳ là dẫn chứng gốc để kiểm chứng phần tóm tắt.");
        return string.Join("\n", lines);
    }

    private sealed record FallbackStory(
        ZaloGroupMessage Anchor,
        IReadOnlyList<ZaloGroupMessage> Messages,
        double Score);

    private static IReadOnlyList<FallbackStory> BuildFallbackStories(
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        string? targetUserId = null)
    {
        if (messages.Count == 0) return [];
        var ordered = messages.OrderBy(item => item.SentAt).ThenBy(item => item.MessageId).ToList();
        var anchors = ordered
            .Where(item => targetUserId is null || NormalizeId(item.SenderId) == targetUserId)
            .Where(item => !IsLowSignalMessage(item.Content))
            .Select(item => new { Message = item, Score = CalculateStoryAnchorScore(item, relations) })
            .OrderByDescending(item => item.Score)
            .Take(60)
            .ToList();
        var candidates = new List<FallbackStory>();
        foreach (var seed in anchors)
        {
            var cluster = new List<ZaloGroupMessage> { seed.Message };
            for (var pass = 0; pass < 3; pass++)
            {
                var added = false;
                foreach (var item in ordered)
                {
                    if (cluster.Any(existing => existing.MessageId == item.MessageId)) continue;
                    if (!cluster.Any(existing => IsStoryRelated(existing, item, relations))) continue;
                    cluster.Add(item);
                    added = true;
                }
                if (!added) break;
            }
            cluster = cluster
                .DistinctBy(item => item.MessageId)
                .OrderBy(item => item.SentAt)
                .ThenBy(item => item.MessageId)
                .ToList();
            var participants = cluster.Select(item => NormalizeId(item.SenderId)).Distinct(StringComparer.Ordinal).Count();
            var decisions = cluster.Count(item => LooksLikeDecision(item.Content));
            var replies = cluster.Count(item => relations.ContainsKey(item.MessageId));
            var score = seed.Score + Math.Min(cluster.Count, 12) * .55 + participants * .8 + decisions * 1.4 + replies * .7;
            candidates.Add(new FallbackStory(seed.Message, cluster, score));
        }

        var selected = new List<FallbackStory>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            var ids = candidate.Messages.Select(item => item.MessageId).ToHashSet(StringComparer.Ordinal);
            var duplicate = selected.Any(existing =>
            {
                var existingIds = existing.Messages.Select(item => item.MessageId).ToHashSet(StringComparer.Ordinal);
                var overlap = ids.Count == 0 ? 0 : ids.Count(existingIds.Contains) / (double)Math.Min(ids.Count, existingIds.Count);
                var anchorGap = (candidate.Anchor.SentAt - existing.Anchor.SentAt).Duration();
                return overlap >= .55 ||
                       (anchorGap <= TimeSpan.FromMinutes(75) &&
                        StoryTokenSimilarity(candidate.Anchor.Content, existing.Anchor.Content) >= .72);
            });
            if (duplicate) continue;
            selected.Add(candidate);
            if (selected.Count >= 8) break;
        }
        return selected;
    }

    private static double CalculateStoryAnchorScore(
        ZaloGroupMessage message,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var content = message.Content ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(content);
        var score = CalculateMessageSignal(message, relations);
        var inboundReplies = relations.Values.Count(relation => relation.ToMessageId == message.MessageId);
        score += Math.Min(inboundReplies, 4) * 1.6;
        if (content.Length >= 120) score += 3.5;
        else if (content.Length >= 70) score += 1.8;
        if (LooksLikeConcernOrProposal(normalized)) score += 4;
        if (LooksLikeDecision(content)) score += 2;
        if (LooksLikeQuestion(content)) score += .8;
        return score;
    }

    private static bool IsStoryRelated(
        ZaloGroupMessage anchor,
        ZaloGroupMessage candidate,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        if (anchor.MessageId == candidate.MessageId) return true;
        if (relations.TryGetValue(candidate.MessageId, out var candidateRelation) && candidateRelation.ToMessageId == anchor.MessageId)
            return true;
        if (relations.TryGetValue(anchor.MessageId, out var anchorRelation) && anchorRelation.ToMessageId == candidate.MessageId)
            return true;

        var gap = (candidate.SentAt - anchor.SentAt).Duration();
        if (gap > TimeSpan.FromMinutes(55)) return false;

        var candidateNormalized = ZaloBotIntelligence.Normalize(candidate.Content ?? string.Empty);
        var anchorName = ZaloBotIntelligence.Normalize(anchor.SenderName ?? string.Empty);
        if (anchorName.Length >= 3 && candidateNormalized.Contains(anchorName, StringComparison.Ordinal) && gap <= TimeSpan.FromMinutes(40))
            return true;

        var similarity = StoryTokenSimilarity(anchor.Content, candidate.Content);
        if (similarity >= .34) return true;
        var shared = StoryTokens(anchor.Content).Intersect(StoryTokens(candidate.Content), StringComparer.Ordinal).Count();
        if (shared >= 3 && gap <= TimeSpan.FromMinutes(40)) return true;
        if (NormalizeId(anchor.SenderId) == NormalizeId(candidate.SenderId) && shared >= 1 && gap <= TimeSpan.FromMinutes(25)) return true;
        return false;
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectFallbackStoryEvidence(
        FallbackStory story,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        int limit)
    {
        var selected = new List<ZaloGroupMessage> { story.Anchor };
        var responses = SelectStoryResponses(story, relations, excludeDecision: true, 2);
        if (responses.Count > 0) selected.Add(responses[0]);
        var decision = SelectBestStoryResolution(story, relations);
        if (decision is not null) selected.Add(decision);
        if (responses.Count > 1) selected.Add(responses[1]);
        selected.AddRange(story.Messages
            .Where(item => selected.All(chosen => chosen.MessageId != item.MessageId))
            .OrderByDescending(item => CalculateMessageSignal(item, relations))
            .Take(Math.Max(0, limit - selected.Count)));
        return selected.DistinctBy(item => item.MessageId).Take(limit).ToList();
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectTopicEvidence(
        ZaloConversationSummaryTopic topic,
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        int limit)
    {
        var ids = topic.MessageIds.ToHashSet(StringComparer.Ordinal);
        return messages
            .Where(item => ids.Contains(item.MessageId))
            .OrderByDescending(item => CalculateMessageSignal(item, relations))
            .ThenBy(item => item.SentAt)
            .Take(limit)
            .OrderBy(item => item.SentAt)
            .ToList();
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectTopicEvidenceInTopicOrder(
        ZaloConversationSummaryTopic topic,
        IReadOnlyList<ZaloGroupMessage> messages,
        int limit)
    {
        var byId = messages.ToDictionary(item => item.MessageId, StringComparer.Ordinal);
        return topic.MessageIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Take(limit)
            .ToList();
    }

    private static double CalculateMessageSignal(
        ZaloGroupMessage message,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var content = (message.Content ?? string.Empty).Trim();
        var lengthScore = Math.Min(content.Length, 180) / 45d;
        var score = lengthScore;
        if (relations.ContainsKey(message.MessageId)) score += 2.5;
        if (LooksLikeQuestion(content)) score += 1.3;
        if (LooksLikeDecision(content)) score += 1.7;
        if (content.Contains('@')) score += .35;
        if (IsLowSignalMessage(content)) score -= 4;
        return score;
    }

    private static string InferFallbackStoryTitle(FallbackStory story, string? targetDisplayName)
    {
        var normalized = ZaloBotIntelligence.Normalize(story.Anchor.Content ?? string.Empty);
        var anchorName = Clean(story.Anchor.SenderName, 60);
        string? title = null;
        if (LooksLikeTeamBalanceConcern(normalized))
            title = $"{anchorName} phản ánh việc xếp team thiếu cân bằng";
        else if (HasAny(normalized, "danh giai", "di giai", "team di giai", "ghom team", "gom team"))
            title = story.Messages.Select(item => NormalizeId(item.SenderId)).Distinct(StringComparer.Ordinal).Count() > 1
                ? "Mọi người bàn về đội đi giải"
                : $"{anchorName} nói về đội đi giải";
        else if (HasAny(normalized, "de quen", "bo quen", "cai binh", "binh nuoc", "lay giup"))
            title = $"{anchorName} nhờ tìm đồ để quên trên sân";
        else if (LooksLikeScheduleOrSlot(normalized))
            title = "Trao đổi về lịch chơi / sân / slot";
        else if (HasAny(normalized, "chuyen hai", "vi tri", "trinh do", "libero", "set cong", "set thu"))
            title = "Bàn về vị trí / trình độ thành viên";
        else if (HasAny(normalized, "bot", "help", "ho tro"))
            title = "Hướng dẫn thành viên sử dụng bot";
        else if (HasAny(normalized, "dang ky", "gui", "form", "cap nhat"))
            title = "Gửi / cập nhật thông tin";

        if (!string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(targetDisplayName) ? title : $"{targetDisplayName}: {title}";

        var cue = CompactNarrativeText(story.Anchor.Content, 72);
        return string.IsNullOrWhiteSpace(targetDisplayName)
            ? $"Trao đổi về {cue}"
            : $"{targetDisplayName}: “{cue}”";
    }

    private static string BuildFallbackStoryNarrative(
        FallbackStory story,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var anchor = story.Anchor;
        var normalized = ZaloBotIntelligence.Normalize(anchor.Content ?? string.Empty);
        var sender = Clean(anchor.SenderName, 60);
        var parts = new List<string>();
        if (LooksLikeTeamBalanceConcern(normalized))
        {
            if (HasExplicitTeamBalanceProposal(normalized))
                parts.Add($"{sender} cho rằng việc mọi người tự chọn nguyên team đang làm mất cân bằng và đề nghị chỉ ghép 2–3 người, phần còn lại để bot cân bằng.");
            else
                parts.Add($"{sender} phản ánh cách xếp/chọn team hiện tại đang thiếu cân bằng và ảnh hưởng sự công bằng giữa các đội.");
        }
        else if (HasAny(normalized, "danh giai", "di giai", "team di giai", "ghom team", "gom team"))
        {
            parts.Add($"{sender} nêu chuyện đội đi giải: {CompactNarrativeText(anchor.Content, 150)}.");
        }
        else
        {
            parts.Add($"{sender} nêu: {CompactNarrativeText(anchor.Content, 160)}.");
        }

        var responses = SelectStoryResponses(story, relations, excludeDecision: true, 2);
        for (var index = 0; index < responses.Count; index++)
        {
            var response = responses[index];
            var prefix = index == 0 ? "Sau đó" : "Tiếp đó";
            parts.Add($"{prefix} {Clean(response.SenderName, 60)} phản hồi: {CompactNarrativeText(response.Content, 130)}.");
        }

        var decision = SelectBestStoryResolution(story, relations);
        if (decision is not null)
        {
            var decisionNormalized = ZaloBotIntelligence.Normalize(decision.Content ?? string.Empty);
            if (LooksLikeTeamBalanceConcern(normalized) && HasAny(decisionNormalized, "toi da 3", "ghep toi da", "de cho can bang"))
                parts.Add($"Kết quả hiện tại: {Clean(decision.SenderName, 60)} thống nhất từ nay chỉ ghép tối đa 3 người để giữ cân bằng.");
            else
                parts.Add($"Kết quả hiện tại: {Clean(decision.SenderName, 60)} chốt/xác nhận: {CompactNarrativeText(decision.Content, 130)}.");
        }
        else if (LooksLikeQuestion(anchor.Content) || story.Messages.Any(item => LooksLikeQuestion(item.Content)))
        {
            parts.Add("Hiện chưa thấy nhóm chốt rõ câu trả lời cuối cùng trong đoạn chat đã lưu.");
        }
        return string.Join(" ", parts);
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectStoryResponses(
        FallbackStory story,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        bool excludeDecision,
        int limit) =>
        story.Messages
            .Where(item => item.MessageId != story.Anchor.MessageId)
            .Where(item => NormalizeId(item.SenderId) != NormalizeId(story.Anchor.SenderId))
            .Where(item => !excludeDecision || (!LooksLikeDecision(item.Content) && !LooksLikeResolution(item.Content)))
            .Where(item => !IsLowSignalMessage(item.Content))
            .OrderByDescending(item => DirectlyReferencesStoryAnchor(story.Anchor, item, relations) ? 1 : 0)
            .ThenByDescending(item => CalculateMessageSignal(item, relations))
            .ThenBy(item => item.SentAt)
            .DistinctBy(item => NormalizeId(item.SenderId))
            .Take(limit)
            .ToList();

    private static ZaloGroupMessage? SelectBestStoryResolution(
        FallbackStory story,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations) =>
        story.Messages
            .Where(item => item.MessageId != story.Anchor.MessageId && LooksLikeResolution(item.Content))
            .OrderByDescending(item => item.SentAt)
            .ThenByDescending(item => CalculateMessageSignal(item, relations))
            .FirstOrDefault();

    private static bool DirectlyReferencesStoryAnchor(
        ZaloGroupMessage anchor,
        ZaloGroupMessage candidate,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        if (relations.TryGetValue(candidate.MessageId, out var relation) && relation.ToMessageId == anchor.MessageId)
            return true;
        var anchorName = ZaloBotIntelligence.Normalize(anchor.SenderName ?? string.Empty);
        var content = ZaloBotIntelligence.Normalize(candidate.Content ?? string.Empty);
        return anchorName.Length >= 3 && content.Contains(anchorName, StringComparison.Ordinal);
    }

    private static bool LooksLikeConcernOrProposal(string normalized) => HasAny(normalized,
        "khong thich", "khong ton trong", "mat can bang", "cong bang", "can bang", "phan anh", "complain",
        "de nghi", "nen ", "vui long", "luu y", "rut kinh nghiem", "y kien", "gop y");

    private static bool LooksLikeTeamBalanceConcern(string normalized) =>
        LooksLikeConcernOrProposal(normalized) &&
        HasAny(normalized, "team", "xep", "pick", "ghep") &&
        HasAny(normalized, "can bang", "cong bang", "khong ton trong", "tu chon", "nguyen 1 team", "nguyen team");

    private static bool LooksLikeScheduleOrSlot(string normalized) =>
        HasAny(normalized, "slot", "vote", "lich san", "san nao", "may gio", "danh sach da") ||
        (normalized.Contains("lich", StringComparison.Ordinal) && normalized.Contains("san", StringComparison.Ordinal));

    private static bool HasExplicitTeamBalanceProposal(string normalized) =>
        HasAny(normalized, "2-3", "2 3", "hai ba", "ghep 2", "ghep hai", "con lai de bot", "de bot can bang");

    private static bool LooksLikeResolution(string? value)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return HasAny(normalized,
            "chot", "thong nhat", "tu nay", "ke tu nay", "toi da", "quy dinh", "xac nhan se", "luu y ghep", "de cho can bang");
    }

    private static IReadOnlyList<string> StoryTokens(string? value) =>
        Regex.Split(ZaloBotIntelligence.Normalize(value ?? string.Empty), @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 2 && !StoryStopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(80)
            .ToList();

    private static double StoryTokenSimilarity(string? left, string? right)
    {
        var a = StoryTokens(left).ToHashSet(StringComparer.Ordinal);
        var b = StoryTokens(right).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static string CompactNarrativeText(string? value, int maxLength)
    {
        var text = SanitizeContent(value, Math.Max(maxLength * 2, 160));
        text = Regex.Replace(text, @"\s+", " ");
        text = text.Trim(' ', '.', ',', ':', ';', '-', '–', '—');
        if (text.Length > maxLength) text = text[..maxLength].TrimEnd() + "…";
        return text;
    }

    private static string RangeLead(string rangeDescription) => rangeDescription switch
    {
        "hôm qua" => "Hôm qua",
        "hôm nay" => "Hôm nay",
        "từ sáng tới giờ" => "Từ sáng tới giờ",
        "tuần này" => "Tuần này",
        "đoạn hội thoại hiện tại" => "Trong đoạn hội thoại hiện tại",
        _ => $"Trong {rangeDescription}"
    };

    private static bool HasAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static bool LooksLikeDecision(string? value)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return HasAny(normalized, "dong y", "chot", "nhat tri", "ghi nhan", "xac nhan", "ok nha", "oke nha", "duoc nha");
    }

    private static bool LooksLikeQuestion(string? value)
    {
        var text = value ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(text);
        return text.Contains('?') || HasAny(normalized, "duoc ko", "duoc khong", "sao", "the nao", "bao gio", "may gio", "ai ", "co ai");
    }

    private static bool IsLowSignalMessage(string? value)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty).Trim();
        if (normalized.Length <= 3) return true;
        return normalized is "daaa" or "daa" or "da" or "ok" or "oke" or "uh" or "u" or "alo" or "gui a" or "e gui a" or "em gui a";
    }

    private static IReadOnlyList<string> BuildProtectedTerms(IReadOnlyList<ZaloConversationSummaryTopic> topics, string? targetDisplayName)
    {
        var terms = topics.SelectMany(topic => new[] { topic.Title, topic.Summary }).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (!string.IsNullOrWhiteSpace(targetDisplayName)) terms.Add(targetDisplayName);
        return terms.Distinct(StringComparer.Ordinal).ToList();
    }

    private static ZaloConversationSummaryTopic? ResolveTopic(
        ZaloConversationSummaryMemory memory,
        ZaloConversationSummaryClassification classification)
    {
        if (classification.TopicOrdinal is > 0 && classification.TopicOrdinal <= memory.Topics.Count)
            return memory.Topics[classification.TopicOrdinal.Value - 1];
        var reference = ZaloBotIntelligence.Normalize(classification.TopicReference ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var ranked = memory.Topics
                .Select(topic => new
                {
                    Topic = topic,
                    Score = ZaloBotIntelligence.TokenSimilarity(ZaloBotIntelligence.Normalize(topic.Title), reference)
                })
                .OrderByDescending(item => item.Score)
                .ToList();
            if (ranked.Count > 0 && ranked[0].Score >= .55) return ranked[0].Topic;
        }
        if (!string.IsNullOrWhiteSpace(memory.LastTopicId))
            return memory.Topics.FirstOrDefault(item => item.Id == memory.LastTopicId);
        return memory.Topics.Count == 1 ? memory.Topics[0] : null;
    }

    private static HashSet<string> ExpandTopicMessageIds(
        IReadOnlySet<string> seedIds,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        int maxIds)
    {
        var expanded = seedIds.ToHashSet(StringComparer.Ordinal);
        for (var pass = 0; pass < 3 && expanded.Count < maxIds; pass++)
        {
            var added = false;
            foreach (var relation in relations.Values)
            {
                if (!string.Equals(relation.RelationType, "ReplyTo", StringComparison.Ordinal)) continue;
                var fromIn = expanded.Contains(relation.FromMessageId);
                var toIn = !string.IsNullOrWhiteSpace(relation.ToMessageId) && expanded.Contains(relation.ToMessageId);
                if (!fromIn && !toIn) continue;
                if (expanded.Count < maxIds && expanded.Add(relation.FromMessageId)) added = true;
                if (!string.IsNullOrWhiteSpace(relation.ToMessageId) && expanded.Count < maxIds && expanded.Add(relation.ToMessageId)) added = true;
            }
            if (!added) break;
        }
        return expanded;
    }

    internal static (DateTimeOffset Start, DateTimeOffset End, string Description) ResolveRange(
        ZaloConversationSummaryTimeScope scope,
        DateTimeOffset now)
    {
        var local = now.ToOffset(VietnamOffset);
        var startOfToday = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, VietnamOffset);
        var localRange = scope switch
        {
            ZaloConversationSummaryTimeScope.Today => (startOfToday, local, "hôm nay"),
            ZaloConversationSummaryTimeScope.SinceMorning => (local.Hour >= 6 ? startOfToday.AddHours(6) : startOfToday, local, "từ sáng tới giờ"),
            ZaloConversationSummaryTimeScope.Yesterday => (startOfToday.AddDays(-1), startOfToday.AddTicks(-1), "hôm qua"),
            ZaloConversationSummaryTimeScope.ThisWeek => (startOfToday.AddDays(-(((int)local.DayOfWeek + 6) % 7)), local, "tuần này"),
            _ => (local.AddHours(-24), local, "đoạn hội thoại hiện tại")
        };
        return (localRange.Item1.ToUniversalTime(), localRange.Item2.ToUniversalTime(), localRange.Item3);
    }

    private static DateTimeOffset ResolveIncomingTime(ZaloIncomingMessageEvent incoming)
    {
        try
        {
            return incoming.SentAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(incoming.SentAtUnixMs)
                : DateTimeOffset.UtcNow;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string? BuildReplyContext(ZaloMessageGraphRelation? relation)
    {
        if (relation is not { RelationType: "ReplyTo" } || string.IsNullOrWhiteSpace(relation.QuotedContentSnapshot)) return null;
        var sender = string.IsNullOrWhiteSpace(relation.QuotedSenderName) ? "người khác" : Clean(relation.QuotedSenderName, 100);
        return $"{sender}: {SanitizeContent(relation.QuotedContentSnapshot, 360)}";
    }

    private static string? ExtractMentionLabel(ZaloIncomingMessageEvent incoming, string userId)
    {
        var mention = incoming.Mentions.FirstOrDefault(item => NormalizeId(item.Uid) == userId && item.Pos >= 0 && item.Len > 0);
        if (mention is null || mention.Pos + mention.Len > incoming.Content.Length) return null;
        return incoming.Content.Substring(mention.Pos, mention.Len).Trim().TrimStart('@');
    }

    private static string FormatVietnamTime(DateTimeOffset value) => value.ToOffset(VietnamOffset).ToString("dd/MM HH:mm");

    private static string SanitizeContent(string? value, int maxLength)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"(?i)\bsk-(?:or-v1-)?[a-z0-9_-]{12,}\b", "[đã ẩn API key]");
        text = Regex.Replace(text, @"(?i)\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\b", "[đã ẩn JWT]");
        text = Regex.Replace(text, @"(?i)\b(authorization\s*:\s*bearer|bearer)\s+[a-z0-9._~+/=-]{12,}", "$1 [đã ẩn]");
        text = Regex.Replace(text, @"(?i)\b([a-z][a-z0-9+.-]*://[^:\s/@]+:)[^@\s/]+(@)", "$1[đã ẩn]$2");
        text = Regex.Replace(text, @"(?i)\b(zpw_(?:sek|enk)|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|client[_ -]?secret|client secret|password|mật khẩu|mat khau)\s*[:=]\s*\S+", "$1: [đã ẩn]");
        return Clean(text, maxLength);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static ZaloBotIntent MapIntent(ZaloConversationSummaryKind kind) => kind switch
    {
        ZaloConversationSummaryKind.MemberSummary => ZaloBotIntent.SummarizeMemberConversation,
        ZaloConversationSummaryKind.HotTopics => ZaloBotIntent.SummarizeHotTopics,
        ZaloConversationSummaryKind.FollowUpDetail => ZaloBotIntent.ConversationSummaryFollowUp,
        _ => ZaloBotIntent.SummarizeConversation
    };
}
