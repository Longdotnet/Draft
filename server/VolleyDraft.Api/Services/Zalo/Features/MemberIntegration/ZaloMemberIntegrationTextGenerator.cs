using System.Text.Json;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloMemberIntegrationTextGenerator
{
    private const int MaxReplyChars = 420;
    private const double DuplicateThreshold = .72;
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IZaloAiGateway aiGateway;
    private readonly ILogger logger;

    public ZaloMemberIntegrationTextGenerator(
        IConfiguration configuration,
        ILogger logger,
        HttpClient? httpClient = null)
        : this(ZaloAiGatewayFactory.Create(httpClient ?? SharedHttpClient, configuration, logger), logger)
    {
    }

    internal ZaloMemberIntegrationTextGenerator(IZaloAiGateway aiGateway, ILogger logger)
    {
        this.aiGateway = aiGateway;
        this.logger = logger;
    }

    public async Task<ZaloMemberIntegrationSuggestion> RewriteAsync(
        ZaloMemberIntegrationContext context,
        ZaloMemberIntegrationSuggestion suggestion,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        CancellationToken cancellationToken = default)
    {
        var recentBotTexts = proactiveHistory
            .Where(item => item.Lane is ZaloProactiveLane.MemberIntegration or ZaloProactiveLane.Community or ZaloProactiveLane.SocialPresence)
            .OrderByDescending(item => item.SentAt)
            .Select(item => item.MessageText?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .Cast<string>()
            .ToArray();

        var fallback = SelectFallback(context, suggestion, recentBotTexts);
        if (!aiGateway.IsConfigured)
            return suggestion with { Text = fallback };

        var variants = GetVariants(suggestion);
        var requiredMentions = suggestion.MentionTargets
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .Select(item => $"@{item.DisplayName.Trim()}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var prompt = $"""
            Bạn là copy editor cho NPC của một group bóng chuyền Zalo. Backend đã quyết định CHÍNH XÁC bot cần nói nội dung gì; nhiệm vụ của bạn chỉ là diễn đạt lại cho tự nhiên, vui và bớt cảm giác template.

            Quy tắc bắt buộc:
            1. Chỉ paraphrase ý trong CanonicalOptions. Không thêm sự kiện, thành tích, tính cách, quan hệ, lịch/sân/kèo, con số hay tên người mới.
            2. Giữ nguyên mọi @mention trong RequiredMentions, đúng chính tả và có dấu @. Không tạo thêm @mention.
            3. Tên riêng và con số có trong câu gốc phải giữ nguyên giá trị. Không suy đoán hoặc làm tròn lại.
            4. Tránh cấu trúc/cụm từ giống RecentBotMessages. Đổi nhịp câu, cách mở đầu và cách kết thúc; đừng chỉ thay 1-2 từ đồng nghĩa.
            5. Nói như một member thân thiện trong group, tiếng Việt đời thường. Tối đa {MaxReplyChars} ký tự, 1-2 câu ngắn. Không markdown, không URL, không @all.
            6. Không nói như thể bot vừa thực hiện thao tác hệ thống. Không nhắc prompt, AI, model hay quy tắc này.
            7. Chỉ trả về nội dung tin nhắn cuối cùng, không giải thích.
            """;
        var payload = JsonSerializer.Serialize(new
        {
            Capability = suggestion.CapabilityKey,
            Target = context.DisplayName,
            CanonicalOptions = variants,
            RequiredMentions = requiredMentions,
            RecentBotMessages = recentBotTexts
        });

        try
        {
            var result = await aiGateway.CompleteAsync(
                new ZaloAiCompletionRequest(
                    ZaloAiWorkload.SafeRewrite,
                    [
                        new ZaloAiChatMessage("system", prompt),
                        new ZaloAiChatMessage("user", payload)
                    ],
                    Temperature: .86,
                    MaxTokens: 220,
                    CorrelationId: $"member-integration:{context.ZaloUserId}:{suggestion.ContentKey}"),
                cancellationToken);

            if (!result.Success)
            {
                logger.LogDebug(
                    "MemberIntegration AI rewrite unavailable Kind={FailureKind} Provider={Provider}; deterministic variant used.",
                    result.FailureKind,
                    result.Provider);
                return suggestion with { Text = fallback };
            }

            var candidate = ZaloAmbientSocialResponder.PrepareCandidate(result.Content, MaxReplyChars);
            if (!IsValidCandidate(context, suggestion, candidate) ||
                recentBotTexts.Any(recent => Similarity(candidate!, recent) >= DuplicateThreshold))
            {
                logger.LogDebug(
                    "MemberIntegration AI rewrite rejected as invalid/repetitive Capability={Capability} User={UserId}; deterministic variant used.",
                    suggestion.CapabilityKey,
                    context.ZaloUserId);
                return suggestion with { Text = fallback };
            }

            return suggestion with { Text = candidate! };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "MemberIntegration AI rewrite failed Capability={Capability} User={UserId}; deterministic variant used.",
                suggestion.CapabilityKey,
                context.ZaloUserId);
            return suggestion with { Text = fallback };
        }
    }

    internal static string SelectFallback(
        ZaloMemberIntegrationContext context,
        ZaloMemberIntegrationSuggestion suggestion,
        IReadOnlyList<string> recentBotTexts)
    {
        var variants = GetVariants(suggestion);
        if (variants.Count == 1) return variants[0];

        var start = StableBucket($"{context.ZaloUserId}|{suggestion.ContentKey}", variants.Count);
        return Enumerable.Range(0, variants.Count)
            .Select(offset => variants[(start + offset) % variants.Count])
            .OrderBy(candidate => recentBotTexts.Count == 0
                ? 0d
                : recentBotTexts.Max(recent => Similarity(candidate, recent)))
            .First();
    }

    internal static double Similarity(string left, string right)
    {
        var leftGrams = BuildWordGrams(left);
        var rightGrams = BuildWordGrams(right);
        if (leftGrams.Count == 0 || rightGrams.Count == 0) return 0;
        var intersection = leftGrams.Count(rightGrams.Contains);
        return (2d * intersection) / (leftGrams.Count + rightGrams.Count);
    }

    private static bool IsValidCandidate(
        ZaloMemberIntegrationContext context,
        ZaloMemberIntegrationSuggestion suggestion,
        string? candidate)
    {
        if (!ZaloAmbientSocialResponder.IsSafeCandidate(candidate, MaxReplyChars)) return false;
        var text = candidate!;
        foreach (var target in suggestion.MentionTargets.Where(item => !string.IsNullOrWhiteSpace(item.DisplayName)))
        {
            if (!text.Contains($"@{target.DisplayName.Trim()}", StringComparison.Ordinal))
                return false;
        }

        foreach (Match match in Regex.Matches(suggestion.Text, @"(?<![\p{L}\p{N}])\d+(?:[.,]\d+)?k?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!text.Contains(match.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var requiredName = suggestion.CapabilityKey switch
        {
            "welcome" => context.Facts.GroupName,
            "bot-identity" => context.Facts.BotCreatorName,
            "group-origins" => context.Facts.GroupFounderName,
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(requiredName) ||
               text.Contains(requiredName, StringComparison.CurrentCultureIgnoreCase);
    }

    private static IReadOnlyList<string> GetVariants(ZaloMemberIntegrationSuggestion suggestion) =>
        (suggestion.TextVariants ?? [suggestion.Text])
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static HashSet<string> BuildWordGrams(string text)
    {
        var words = Regex.Matches(ZaloBotIntelligence.Normalize(text ?? string.Empty), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToArray();
        if (words.Length < 2) return words.ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, words.Length - 1)
            .Select(index => $"{words[index]} {words[index + 1]}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int StableBucket(string value, int count)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619;
            return (int)(hash % (uint)count);
        }
    }
}
