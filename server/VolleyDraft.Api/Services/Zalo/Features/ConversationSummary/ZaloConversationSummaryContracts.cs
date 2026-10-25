namespace VolleyDraft.Api.Services;

public enum ZaloConversationSummaryKind
{
    None,
    GroupSummary,
    MemberSummary,
    HotTopics,
    FollowUpDetail
}

public enum ZaloConversationSummaryTimeScope
{
    CurrentConversation,
    Today,
    SinceMorning,
    Yesterday,
    ThisWeek
}

public sealed record ZaloConversationSummaryClassification(
    ZaloConversationSummaryKind Kind,
    double Confidence,
    ZaloConversationSummaryTimeScope TimeScope,
    string? MemberReference,
    string? TopicReference,
    int? TopicOrdinal);

public sealed record ZaloConversationSummaryClassifierContext(
    string Question,
    string SenderZaloUserId,
    string SenderName,
    DateTimeOffset CurrentVietnamTime,
    IReadOnlyList<string> PreviousTopicTitles);

public sealed record ZaloConversationSummaryCandidate(
    string MessageId,
    DateTimeOffset SentAt,
    string SenderId,
    string SenderName,
    string Content,
    string? ReplyContext,
    bool IsTarget,
    string? ReplyToMessageId = null,
    string? QuotedSenderId = null);

public sealed record ZaloConversationSummaryTopic(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> MessageIds,
    IReadOnlyList<string> ParticipantIds,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenQuestions,
    double Importance);

public sealed record ZaloConversationSummaryAiResult(
    string Overview,
    IReadOnlyList<ZaloConversationSummaryTopic> Topics);

public sealed record ZaloConversationSummaryAiContext(
    ZaloConversationSummaryKind Kind,
    string Question,
    string? TargetZaloUserId,
    string? TargetDisplayName,
    string RangeDescription,
    IReadOnlyList<ZaloConversationSummaryCandidate> Candidates);

public sealed record ZaloConversationSummaryFollowUpAiContext(
    string Question,
    ZaloConversationSummaryTopic Topic,
    string? TargetZaloUserId,
    string? TargetDisplayName,
    IReadOnlyList<ZaloConversationSummaryCandidate> Candidates);

public sealed record ZaloConversationSummaryFollowUpAiResult(
    string Answer,
    IReadOnlyList<string> MessageIds);

internal sealed record ZaloConversationSummaryMemory(
    string Overview,
    IReadOnlyList<ZaloConversationSummaryTopic> Topics,
    string? LastTopicId = null,
    string? TargetZaloUserId = null,
    string? TargetDisplayName = null);

internal sealed record ZaloConversationSummaryHandleResult(
    string Text,
    ZaloBotIntent Intent,
    bool AiCalled,
    IReadOnlyList<string> ProtectedTerms);
