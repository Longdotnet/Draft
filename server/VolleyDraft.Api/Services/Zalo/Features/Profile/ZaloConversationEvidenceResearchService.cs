using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal static class ZaloConversationEvidenceIntentPolicy
{
    public static bool LooksLikeQuestion(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value);
        if (string.IsNullOrWhiteSpace(q)) return false;

        var pastMarker =
            HasAny(q,
                "truoc do", "truoc day", "hoi truoc", "luc truoc", "hom truoc",
                "bua truoc", "ngay truoc", "tuan truoc", "thang truoc",
                "hoi do", "hoi bua", "luc do",
                "tung noi", "da tung noi", "co noi", "da noi", "noi gi ve",
                "tung nhac", "da nhac", "co nhac", "tung bao", "da bao", "bao rang",
                "lich su chat", "lich su tin nhan", "tin nhan cu",
                "tim lai tin nhan", "tim lai cau", "tim lai doan", "tim doan",
                "tim bang chung", "kiem tra lai", "check lai", "luc chat", "research");
        if (!pastMarker) return false;

        return HasAny(
                   q,
                   "noi", "nhac", "bao", "de cap", "ke", "ban ve", "chat", "tin nhan",
                   "bang chung", "research", "tim lai", "tim doan", "luc xem", "lich su",
                   "kiem tra lai", "check lai") ||
               Regex.IsMatch(
                   q,
                   @"\b(?:co|da|tung)\b.{0,32}\b(?:noi|nhac|bao|de cap|ke)\b",
                   RegexOptions.CultureInvariant);
    }

    public static bool MightBeQuestion(string value)
    {
        if (LooksLikeQuestion(value)) return true;
        var q = ZaloBotIntelligence.Normalize(value);
        if (string.IsNullOrWhiteSpace(q)) return false;
        return HasAny(
            q,
            "tung", "truoc", "hoi do", "hoi bua", "luc do", "bua truoc",
            "luc xem", "luc lai", "tim lai", "search", "research", "lich su",
            "de cap", "ke lai", "ban ve", "nhac lai");
    }

    private static bool HasAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}

internal sealed record ZaloConversationEvidenceResearchResult(
    string Text,
    bool AiCalled,
    IReadOnlyList<string> ProtectedTerms);

internal sealed class ZaloConversationEvidenceResearchService(
    VolleyDraftDbContext db,
    AiAssistantService? ai)
{
    private const int MaxAiCandidates = 120;
    private const int MaxDisplayedEvidence = 3;
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "bot", "npc", "tui", "toi", "minh", "em", "anh", "chi", "ban", "nguoi",
        "do", "nay", "kia", "truoc", "sau", "hoi", "luc", "hom", "co", "khong",
        "da", "tung", "noi", "nhac", "bao", "de", "cap", "chat", "tin", "nhan", "gi", "ve", "cho",
        "hoi", "xem", "tim", "lai", "giup", "duoc", "phai", "la"
    };

    public async Task<ZaloConversationEvidenceResearchResult> ResearchAsync(
        string connectionId,
        string groupId,
        string targetUserId,
        string targetDisplayName,
        string question,
        string? excludedMessageId,
        bool allowAi,
        CancellationToken cancellationToken)
    {
        var normalizedTargetId = NormalizeId(targetUserId);
        var messages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                message.SenderId == normalizedTargetId &&
                !message.IsFromBot &&
                (excludedMessageId == null || message.MessageId != excludedMessageId))
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.MessageId)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return new ZaloConversationEvidenceResearchResult(
                $"Tui chưa có tin nhắn nào của {targetDisplayName} trong phần lịch sử bot đã lưu của group này. " +
                "Điều đó không có nghĩa là người đó chưa từng nói; chỉ là bot chưa có dữ liệu để dẫn chứng.",
                false,
                [targetDisplayName]);
        }

        var graph = new ZaloMessageGraphStore(db);
        var relations = await graph.LoadReplyRelationsForSenderAsync(
            connectionId,
            groupId,
            normalizedTargetId,
            cancellationToken);
        var ranked = Rank(messages, relations, question, targetDisplayName);
        var candidateRows = ranked
            .Take(MaxAiCandidates)
            .ToList();
        var aiCandidates = candidateRows
            .Select(item => new ZaloConversationEvidenceAiCandidate(
                item.Message.MessageId,
                item.Message.SentAt,
                SanitizeContent(item.Message.Content, 500),
                BuildReplyContext(item.Relation)))
            .ToList();

        ZaloConversationEvidenceJudgement? judgement = null;
        var aiCalled = false;
        if (allowAi && ai is { IsConfigured: true })
        {
            judgement = await ai.JudgeConversationEvidenceAsync(
                new ZaloConversationEvidenceAiContext(
                    question,
                    normalizedTargetId,
                    targetDisplayName,
                    aiCandidates),
                cancellationToken);
            aiCalled = true;
        }

        judgement ??= BuildDeterministicFallback(ranked);
        if (judgement.Verdict == ZaloConversationEvidenceVerdict.NotFound ||
            judgement.MessageIds.Count == 0)
        {
            return new ZaloConversationEvidenceResearchResult(
                $"Tui chưa tìm thấy câu nào đủ liên quan trong {messages.Count} tin nhắn của {targetDisplayName} mà bot đang lưu trong group này. " +
                "Điều này không có nghĩa là người đó chưa từng nói; dữ liệu bot lưu có thể không bao phủ toàn bộ lịch sử Zalo.",
                aiCalled,
                [targetDisplayName, messages.Count.ToString()]);
        }

        var byId = messages.ToDictionary(message => message.MessageId, StringComparer.Ordinal);
        var selected = judgement.MessageIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderBy(message => message.SentAt)
            .Take(MaxDisplayedEvidence)
            .ToList();
        if (selected.Count == 0)
        {
            return new ZaloConversationEvidenceResearchResult(
                $"Tui chưa tìm thấy bằng chứng phù hợp trong phần lịch sử bot đang lưu của {targetDisplayName}.",
                aiCalled,
                [targetDisplayName]);
        }

        var evidenceLines = new List<string>();
        var protectedTerms = new List<string> { targetDisplayName };
        foreach (var message in selected)
        {
            var content = SanitizeContent(message.Content, 650);
            evidenceLines.Add($"- {FormatVietnamTime(message.SentAt)} — “{content}”");
            protectedTerms.Add(content);
            protectedTerms.Add(message.MessageId);

            relations.TryGetValue(message.MessageId, out var relation);
            if (relation is { RelationType: "ReplyTo" } &&
                !string.IsNullOrWhiteSpace(relation.QuotedContentSnapshot))
            {
                var quoted = SanitizeContent(relation.QuotedContentSnapshot, 420);
                var quotedSender = string.IsNullOrWhiteSpace(relation.QuotedSenderName)
                    ? "tin được trả lời"
                    : relation.QuotedSenderName.Trim();
                evidenceLines.Add($"  ↳ đang trả lời {quotedSender}: “{quoted}”");
                protectedTerms.Add(quoted);
            }
        }

        var intro = judgement.Verdict == ZaloConversationEvidenceVerdict.Supported
            ? $"Có nha. Tui tìm thấy bằng chứng trong lịch sử đã lưu của {targetDisplayName}:"
            : $"Tui tìm thấy đoạn chat có liên quan của {targetDisplayName}, nhưng chưa đủ để khẳng định hoàn toàn đúng ý bạn hỏi:";
        var conclusion = judgement.Verdict == ZaloConversationEvidenceVerdict.Supported
            ? "Dựa trên chính câu đã lưu này, nội dung bạn hỏi có bằng chứng khá rõ."
            : "Đoạn này có liên quan, nhưng tui không muốn suy diễn thêm ngoài câu chữ đã lưu.";

        return new ZaloConversationEvidenceResearchResult(
            $"{intro}\n{string.Join("\n", evidenceLines)}\n\n{conclusion}",
            aiCalled,
            protectedTerms.Distinct(StringComparer.Ordinal).ToList());
    }

    internal static IReadOnlyList<(ZaloGroupMessage Message, ZaloMessageGraphRelation? Relation, int Score)> Rank(
        IReadOnlyList<ZaloGroupMessage> messages,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations,
        string question,
        string targetDisplayName)
    {
        var queryTokens = SignificantTokens(question, targetDisplayName);
        var queryTokenSet = queryTokens.ToHashSet(StringComparer.Ordinal);
        var queryBigrams = BuildBigrams(queryTokens);
        var scored = messages
            .Select(message =>
            {
                relations.TryGetValue(message.MessageId, out var relation);
                var normalizedContent = ZaloBotIntelligence.Normalize(
                    $"{message.Content ?? string.Empty} {relation?.QuotedContentSnapshot ?? string.Empty}");
                var contentTokens = Tokenize(normalizedContent).ToHashSet(StringComparer.Ordinal);
                var overlap = queryTokenSet.Count(contentTokens.Contains);
                var bigramOverlap = queryBigrams.Count(bigram =>
                    normalizedContent.Contains(bigram, StringComparison.Ordinal));
                var score = overlap * 100 + bigramOverlap * 80;
                if (queryTokenSet.Count >= 2 &&
                    queryTokenSet.All(contentTokens.Contains))
                    score += 250;
                return (Message: message, Relation: relation, Score: score);
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Message.SentAt)
            .ToList();

        if (scored.Count <= MaxAiCandidates)
            return scored;

        var lexical = scored.Where(item => item.Score > 0).Take(100).ToList();
        var selectedIds = lexical.Select(item => item.Message.MessageId).ToHashSet(StringComparer.Ordinal);
        var recent = scored
            .Where(item => !selectedIds.Contains(item.Message.MessageId))
            .OrderByDescending(item => item.Message.SentAt)
            .Take(MaxAiCandidates - lexical.Count);
        return lexical
            .Concat(recent)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Message.SentAt)
            .ToList();
    }

    private static ZaloConversationEvidenceJudgement BuildDeterministicFallback(
        IReadOnlyList<(ZaloGroupMessage Message, ZaloMessageGraphRelation? Relation, int Score)> ranked)
    {
        var selected = ranked
            .Where(item => item.Score > 0)
            .Take(MaxDisplayedEvidence)
            .Select(item => item.Message.MessageId)
            .ToList();
        return selected.Count == 0
            ? new ZaloConversationEvidenceJudgement(
                ZaloConversationEvidenceVerdict.NotFound,
                1,
                [])
            : new ZaloConversationEvidenceJudgement(
                ZaloConversationEvidenceVerdict.Related,
                .5,
                selected);
    }

    private static IReadOnlyList<string> SignificantTokens(string value, string targetDisplayName)
    {
        var targetTokens = Tokenize(ZaloBotIntelligence.Normalize(targetDisplayName))
            .ToHashSet(StringComparer.Ordinal);
        return Tokenize(ZaloBotIntelligence.Normalize(value))
            .Where(token => token.Length >= 2)
            .Where(token => !StopWords.Contains(token))
            .Where(token => !targetTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();
    }

    private static IReadOnlyList<string> BuildBigrams(IEnumerable<string> tokens)
    {
        var values = tokens.ToList();
        var result = new List<string>();
        for (var index = 0; index + 1 < values.Count; index++)
            result.Add(values[index] + " " + values[index + 1]);
        return result;
    }

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Split(value ?? string.Empty, @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length > 0);

    private static string FormatVietnamTime(DateTimeOffset value) =>
        value.ToOffset(VietnamOffset).ToString("dd/MM/yyyy HH:mm");

    private static string? BuildReplyContext(ZaloMessageGraphRelation? relation)
    {
        if (relation is not { RelationType: "ReplyTo" } ||
            string.IsNullOrWhiteSpace(relation.QuotedContentSnapshot))
            return null;
        var sender = string.IsNullOrWhiteSpace(relation.QuotedSenderName)
            ? "người khác"
            : relation.QuotedSenderName.Trim();
        return $"{sender}: {SanitizeContent(relation.QuotedContentSnapshot, 420)}";
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static string SanitizeContent(string? value, int maxLength)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        text = Regex.Replace(
            text,
            @"(?i)\bsk-(?:or-v1-)?[a-z0-9_-]{12,}\b",
            "[đã ẩn API key]");
        text = Regex.Replace(
            text,
            @"(?i)\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\b",
            "[đã ẩn JWT]");
        text = Regex.Replace(
            text,
            @"(?i)\b(authorization\s*:\s*bearer|bearer)\s+[a-z0-9._~+/=-]{12,}",
            "$1 [đã ẩn]");
        text = Regex.Replace(
            text,
            @"(?i)\b([a-z][a-z0-9+.-]*://[^:\s/@]+:)[^@\s/]+(@)",
            "$1[đã ẩn]$2");
        text = Regex.Replace(
            text,
            @"(?i)\b(zpw_(?:sek|enk)|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|client[_ -]?secret|client secret|password|mật khẩu|mat khau)\s*[:=]\s*\S+",
            "$1: [đã ẩn]");
        return Truncate(text, maxLength);
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}
