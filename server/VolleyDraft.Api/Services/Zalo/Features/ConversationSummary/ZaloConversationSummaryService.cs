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
            return JsonSerializer.Deserialize<ZaloConversationSummaryMemory>(row.SummaryJson, JsonOptions);
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
        var selected = SelectRepresentativeMessages(messages, normalizedTarget);
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
                normalizedTarget is not null && NormalizeId(message.SenderId) == normalizedTarget);
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
        var memory = new ZaloConversationSummaryMemory(summary.Overview, topics);
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
            FormatSummary(summary.Overview, topics, classification.Kind, targetDisplayName, range.Description, messages, relations, usedFallback),
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
        var sourceMessages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                topicIds.Contains(message.MessageId) &&
                !message.IsFromBot)
            .OrderBy(message => message.SentAt)
            .ToListAsync(cancellationToken);
        if (sourceMessages.Count == 0)
            return null;

        var relations = await new ZaloMessageGraphStore(db).LoadReplyRelationsAsync(connectionId, groupId, cancellationToken);
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
                !string.IsNullOrWhiteSpace(targetUserId) && NormalizeId(message.SenderId) == NormalizeId(targetUserId));
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
                              : topicIds);
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
        string? targetUserId)
    {
        if (messages.Count <= MaxAiCandidates) return messages;
        var indexes = new HashSet<int>();
        if (targetUserId is not null)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (NormalizeId(messages[index].SenderId) != targetUserId) continue;
                indexes.Add(index);
                if (index > 0) indexes.Add(index - 1);
                if (index + 1 < messages.Count) indexes.Add(index + 1);
            }
        }
        var evenly = 110;
        var stride = Math.Max(1d, (messages.Count - 1d) / Math.Max(1, evenly - 1));
        for (var sample = 0; sample < evenly; sample++)
            indexes.Add(Math.Min(messages.Count - 1, (int)Math.Round(sample * stride)));
        for (var index = Math.Max(0, messages.Count - 40); index < messages.Count; index++)
            indexes.Add(index);
        return indexes
            .OrderBy(index => index)
            .TakeLast(MaxAiCandidates)
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
        var relevant = targetUserId is null
            ? messages
            : messages.Where(item => NormalizeId(item.SenderId) == targetUserId).ToList();
        var participantCount = relevant.Select(item => NormalizeId(item.SenderId)).Distinct(StringComparer.Ordinal).Count();
        var segments = BuildFallbackSegments(relevant)
            .Select(segment => new
            {
                Messages = segment,
                Score = CalculateFallbackSegmentScore(segment, relations)
            })
            .OrderByDescending(item => item.Score)
            .Take(4)
            .ToList();

        var topics = new List<ZaloConversationSummaryTopic>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index].Messages;
            var ids = SelectFallbackEvidence(segment, relations, 12)
                .Select(item => item.MessageId)
                .ToList();
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
            var start = segment.Min(item => item.SentAt);
            var end = segment.Max(item => item.SentAt);
            var summary = segment.Count == 1
                ? $"Một tin đáng chú ý lúc {FormatVietnamTime(start)}."
                : $"{segment.Count} tin từ {participants.Count} người, diễn ra khoảng {FormatVietnamClock(start)}–{FormatVietnamClock(end)}.";
            topics.Add(new ZaloConversationSummaryTopic(
                $"topic-{index + 1}",
                InferFallbackTitle(segment, targetDisplayName),
                summary,
                ids,
                participants,
                decisions,
                questions,
                Math.Min(1, segments[index].Score / 20d)));
        }

        if (topics.Count == 0)
        {
            var ids = relevant.TakeLast(8).Select(item => item.MessageId).ToList();
            topics.Add(new ZaloConversationSummaryTopic(
                "topic-1",
                targetUserId is null ? "Đoạn chat gần đây" : $"Phần {targetDisplayName ?? "thành viên"} đã nói",
                $"Có {relevant.Count} tin nhắn trong {rangeDescription}.",
                ids,
                relevant.TakeLast(8).Select(item => NormalizeId(item.SenderId)).Distinct().ToList(),
                [],
                [],
                .5));
        }

        var overview = targetUserId is null
            ? $"Bot ghi nhận {relevant.Count} tin nhắn từ {participantCount} người trong {rangeDescription}. Dưới đây là các cụm trao đổi nổi bật dựa trực tiếp trên tin nhắn đã lưu."
            : $"Bot ghi nhận {relevant.Count} tin của {targetDisplayName ?? "người bạn hỏi"} trong {rangeDescription}; các đoạn dưới đây chỉ dùng lời của đúng UID đó làm phát biểu chính.";
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
        string overview,
        IReadOnlyList<ZaloConversationSummaryTopic> topics,
        ZaloConversationSummaryKind kind,
        string? targetDisplayName,
        string rangeDescription,
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        bool usedFallback)
    {
        var header = kind switch
        {
            ZaloConversationSummaryKind.MemberSummary => $"Tóm tắt phần {targetDisplayName ?? "thành viên"} nói trong {rangeDescription}:",
            ZaloConversationSummaryKind.HotTopics => $"Mấy chuyện đáng chú ý trong {rangeDescription}:",
            _ => $"Tóm tắt {rangeDescription}:"
        };
        var lines = new List<string> { header, overview };
        for (var index = 0; index < topics.Count; index++)
        {
            var topic = topics[index];
            lines.Add($"{index + 1}. {topic.Title} — {topic.Summary}");
            if (topic.Decisions.Count > 0) lines.Add($"   • Đã chốt/xác nhận: {string.Join("; ", topic.Decisions)}");
            if (topic.OpenQuestions.Count > 0) lines.Add($"   • Còn mở: {string.Join("; ", topic.OpenQuestions)}");
            var evidence = SelectTopicEvidence(topic, messages, relations, 2);
            foreach (var item in evidence)
                lines.Add($"   ↳ {FormatVietnamTime(item.SentAt)} · {Clean(item.SenderName, 60)}: “{SanitizeContent(item.Content, 200)}”");
        }
        if (topics.Count > 0)
            lines.Add("Bạn có thể hỏi tiếp tự nhiên như `vụ số 2 nói kỹ hơn` hoặc `cái vụ team đi giải ai đề xuất vậy?`.");
        if (usedFallback)
            lines.Add("Lưu ý: lượt này AI không phản hồi kịp, nên bot tự gom cụm từ dữ liệu gốc và chỉ nêu những gì có tin nhắn làm căn cứ.");
        return string.Join("\n", lines);
    }

    private static IReadOnlyList<IReadOnlyList<ZaloGroupMessage>> BuildFallbackSegments(IReadOnlyList<ZaloGroupMessage> messages)
    {
        if (messages.Count == 0) return [];
        var ordered = messages.OrderBy(item => item.SentAt).ThenBy(item => item.MessageId).ToList();
        var result = new List<IReadOnlyList<ZaloGroupMessage>>();
        var current = new List<ZaloGroupMessage>();
        foreach (var message in ordered)
        {
            if (current.Count > 0)
            {
                var gap = message.SentAt - current[^1].SentAt;
                if (gap >= TimeSpan.FromMinutes(25) || current.Count >= 30)
                {
                    result.Add(current);
                    current = [];
                }
            }
            current.Add(message);
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private static double CalculateFallbackSegmentScore(
        IReadOnlyList<ZaloGroupMessage> segment,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var participants = segment.Select(item => NormalizeId(item.SenderId)).Distinct(StringComparer.Ordinal).Count();
        var substantive = segment.Count(item => !IsLowSignalMessage(item.Content));
        var replies = segment.Count(item => relations.ContainsKey(item.MessageId));
        var questions = segment.Count(item => LooksLikeQuestion(item.Content));
        var decisions = segment.Count(item => LooksLikeDecision(item.Content));
        return Math.Min(segment.Count, 20) * .45 +
               substantive * .55 +
               participants * 1.3 +
               replies * 1.6 +
               questions * .45 +
               decisions * .75;
    }

    private static IReadOnlyList<ZaloGroupMessage> SelectFallbackEvidence(
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        int limit) =>
        messages
            .OrderByDescending(item => CalculateMessageSignal(item, relations))
            .ThenBy(item => item.SentAt)
            .Take(limit)
            .OrderBy(item => item.SentAt)
            .ToList();

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

    private static string InferFallbackTitle(IReadOnlyList<ZaloGroupMessage> segment, string? targetDisplayName)
    {
        var normalized = ZaloBotIntelligence.Normalize(string.Join(" ", segment.Select(item => item.Content)));
        string? title = null;
        if (HasAny(normalized, "giai", "team", "doi hinh", "tap bai"))
            title = "Team / giải đấu";
        else if (HasAny(normalized, "lich", "san", "vote", "slot", "danh sach da"))
            title = "Lịch chơi / sân / slot";
        else if (HasAny(normalized, "chuyen hai", "vi tri", "trinh do", "libero", "cong", "thu"))
            title = "Vị trí / trình độ thành viên";
        else if (HasAny(normalized, "bot", "help", "ho tro"))
            title = "Sử dụng bot / hỗ trợ thành viên";
        else if (HasAny(normalized, "dang ky", "gui", "form", "cap nhat"))
            title = "Gửi / cập nhật thông tin";

        if (!string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(targetDisplayName) ? title : $"{targetDisplayName}: {title}";

        var representative = segment
            .OrderByDescending(item => (item.Content ?? string.Empty).Length)
            .FirstOrDefault(item => !IsLowSignalMessage(item.Content)) ?? segment[^1];
        var cue = SanitizeContent(representative.Content, 72);
        return string.IsNullOrWhiteSpace(targetDisplayName)
            ? $"Trao đổi quanh “{cue}”"
            : $"{targetDisplayName}: “{cue}”";
    }

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

    private static string FormatVietnamClock(DateTimeOffset value) => value.ToOffset(VietnamOffset).ToString("HH:mm");

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
