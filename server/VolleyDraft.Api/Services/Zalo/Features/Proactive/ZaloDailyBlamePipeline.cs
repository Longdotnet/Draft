using System.Text.Json;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Services;

internal enum ZaloRoastAngleKind
{
    Contradiction,
    CounterReply,
    RepeatedPattern,
    SelfOwnGoal,
    LowEvidence
}

internal sealed record ZaloRoastAngle(
    string Id,
    ZaloRoastAngleKind Kind,
    int Score,
    string Summary,
    IReadOnlyList<ZaloDailyBlameEvidence> Evidence,
    IReadOnlyList<string> Signals);

internal sealed record ZaloRoastContextPack(
    ZaloDailyBlameTarget Target,
    string LocalDate,
    ZaloRoastAngle SelectedAngle,
    IReadOnlyList<ZaloRoastAngle> RankedAngles,
    IReadOnlyList<string> PreviousRoasts);

internal static class ZaloRoastContextBuilder
{
    private sealed record TopicRule(string Id, string Label, string[] Tokens);
    private sealed record ContradictionRule(string Id, string Label, string[] Left, string[] Right);

    private static readonly TopicRule[] TopicRules =
    [
        new("slot", "slot/pass/share", ["slot", "pass", "share", "nhả", "nha slot", "lụm"]),
        new("team", "team/chia đội", ["team", "chung team", "khác team", "chia team"]),
        new("vote", "vote/form/admin", ["vote", "form", "duyệt", "approve", "lọc mem", "kick"]),
        new("block", "block/KPI/đập", ["block", "kpi", "chắn", "dap", "đập"]),
        new("coach", "HLV/KPI người khác", ["thể hiện tốt hơn", "the hien tot hon", "kpi của", "kpi cua", "luyện", "tap"]),
        new("money", "thu tiền/chuyển khoản", ["thu tiền", "tiền sân", "chuyển khoản", "ck", "qr"]),
        new("bot", "bot/automation", ["bot", "bott", "npc", "robot"])
    ];

    private static readonly ContradictionRule[] ContradictionRules =
    [
        new(
            "study-vs-volleyball-admin",
            "nói tập trung học/học hành nhưng lịch sử lại đầy việc sân, slot, team hoặc vote",
            ["chuyên tâm học", "hoc hanh", "học hành", "học bài", "ôn bài", "on bai"],
            ["slot", "vote", "team", "form", "sân", "san", "block", "đập", "dap"]),
        new(
            "love-vs-volleyball",
            "tuyên bố dẹp bồ/bóng chuyền quan trọng hơn nhưng sau đó lại flex hoặc ưu tiên đi chơi với bồ",
            ["dẹp hết bồ", "dep het bo", "bồ bịch", "bo bich", "banh bóng quan trọng", "bóng chuyền quan trọng"],
            ["đi chơi với bồ", "di choi voi bo", "đi chơi vs bồ", "choi vs bo", "bồ nhó", "bo nho"]),
        new(
            "coach-vs-lazy",
            "đặt KPI/nhắc người khác thể hiện nhưng chính mình lại nhận là lười",
            ["thể hiện tốt hơn", "the hien tot hon", "kpi", "cố lên", "co len"],
            ["lười", "luoi", "mệt quá", "met qua"]),
        new(
            "anti-bot-vs-use-bot",
            "chê bot/xạo nhưng vẫn quay lại dùng bot để chia team hoặc xử lý group",
            ["bot nó xạo", "bot no xao", "bot xạo", "npc xạo"],
            ["@bott", "@b o t", "chung team", "khác team", "xac nhan", "xác nhận"]),
        new(
            "no-slot-vs-slot-hunter",
            "than không có/full slot nhưng liên tục săn, xin hoặc lụm slot",
            ["full", "không có slot", "ko có slot", "khong co slot", "tranh slot hong kịp", "tranh slot không kịp"],
            ["xin slot", "lụm", "lum", "ai pass", "lấy slot", "lay slot", "hứng", "hung"])
    ];

    public static ZaloRoastContextPack Build(
        ZaloDailyBlameTarget target,
        IReadOnlyList<ZaloDailyBlameEvidence> evidence,
        IReadOnlyList<string> previousRoasts,
        DateTimeOffset localNow)
    {
        var angles = new List<ZaloRoastAngle>();
        angles.AddRange(BuildContradictions(evidence));
        angles.AddRange(BuildCounterReplies(evidence));
        angles.AddRange(BuildRepeatedPatterns(evidence));
        angles.AddRange(BuildSelfOwnGoals(evidence));

        if (angles.Count == 0)
        {
            angles.Add(new ZaloRoastAngle(
                "low-evidence",
                ZaloRoastAngleKind.LowEvidence,
                1,
                "Log gần đây chưa có đủ bằng chứng mạnh; chỉ được điểm danh nhẹ và tuyệt đối không bịa persona.",
                [],
                ["insufficient_evidence"]));
        }

        var ranked = ZaloRoastAngleScorer.Rank(angles, previousRoasts, localNow);

        return new ZaloRoastContextPack(
            target,
            localNow.ToString("yyyy-MM-dd"),
            ranked[0],
            ranked,
            previousRoasts.Take(8).ToArray());
    }

    private static IEnumerable<ZaloRoastAngle> BuildContradictions(IReadOnlyList<ZaloDailyBlameEvidence> evidence)
    {
        foreach (var rule in ContradictionRules)
        {
            var leftCandidates = evidence
                .Where(item => ContainsAny(item.Content, rule.Left))
                .OrderBy(item => item.UsedRecently)
                .ThenByDescending(item => item.SentAt)
                .ToArray();
            ZaloDailyBlameEvidence? left = null;
            ZaloDailyBlameEvidence? right = null;
            foreach (var candidate in leftCandidates)
            {
                var next = evidence
                    .Where(item => ContainsAny(item.Content, rule.Right))
                    .Where(item =>
                        !string.Equals(item.MessageId, candidate.MessageId, StringComparison.Ordinal) &&
                        item.SentAt >= candidate.SentAt)
                    .OrderBy(item => item.UsedRecently)
                    .ThenByDescending(item => item.SentAt)
                    .FirstOrDefault();
                if (next is null) continue;
                left = candidate;
                right = next;
                break;
            }
            if (left is null || right is null) continue;

            var pair = new[] { left, right };
            yield return new ZaloRoastAngle(
                $"contradiction:{rule.Id}",
                ZaloRoastAngleKind.Contradiction,
                70,
                rule.Label,
                pair,
                ["contradiction", rule.Id]);
        }
    }

    private static IEnumerable<ZaloRoastAngle> BuildCounterReplies(IReadOnlyList<ZaloDailyBlameEvidence> evidence)
    {
        foreach (var item in evidence.Where(item => item.NearbyReplies.Count > 0))
        {
            var exactReplyCount = item.NearbyReplies.Count(reply => reply.StartsWith("REPLY ·", StringComparison.Ordinal));
            var nearbyOnlyCount = item.NearbyReplies.Count - exactReplyCount;
            var baseScore = 48 + Math.Min(30, exactReplyCount * 15) + Math.Min(6, nearbyOnlyCount * 3);
            yield return new ZaloRoastAngle(
                $"counter-reply:{item.MessageId}",
                ZaloRoastAngleKind.CounterReply,
                baseScore,
                exactReplyCount > 0
                    ? "Một phát ngôn của target có reply trực tiếp/counter; ưu tiên reply thật như bằng chứng phụ và không chuyển mục tiêu roast sang người phản hồi."
                    : "Có chat lân cận ngay sau phát ngôn của target nhưng chưa chắc là reply trực tiếp; chỉ dùng như ngữ cảnh phụ, không khẳng định đó là phản dame nếu câu chữ không cho thấy rõ.",
                [item],
                [
                    exactReplyCount > 0 ? "exact_counter_reply" : "nearby_context",
                    $"exact_reply_count:{exactReplyCount}",
                    $"nearby_count:{nearbyOnlyCount}"
                ]);
        }
    }

    private static IEnumerable<ZaloRoastAngle> BuildRepeatedPatterns(IReadOnlyList<ZaloDailyBlameEvidence> evidence)
    {
        foreach (var topic in TopicRules)
        {
            var matches = evidence
                .Where(item => ContainsAny(item.Content, topic.Tokens))
                .OrderBy(item => item.UsedRecently)
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.SentAt)
                .Take(4)
                .ToArray();
            if (matches.Length < 2) continue;

            yield return new ZaloRoastAngle(
                $"pattern:{topic.Id}",
                ZaloRoastAngleKind.RepeatedPattern,
                42 + Math.Min(24, matches.Length * 6),
                $"Pattern lặp lại quanh {topic.Label}; roast vào thói quen thể hiện trực tiếp trong log, không suy rộng thành tính cách ngoài group.",
                matches,
                ["repeated_pattern", topic.Id, $"count:{matches.Length}"]);
        }
    }

    private static IEnumerable<ZaloRoastAngle> BuildSelfOwnGoals(IReadOnlyList<ZaloDailyBlameEvidence> evidence)
    {
        foreach (var item in evidence.OrderByDescending(item => item.Score).Take(6))
        {
            yield return new ZaloRoastAngle(
                $"self-own:{item.MessageId}",
                ZaloRoastAngleKind.SelfOwnGoal,
                24,
                "Một câu tự thân đã đủ làm punchline; bám sát nguyên ý và không thêm sự kiện ngoài log.",
                [item],
                ["self_own"]);
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> tokens)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        return tokens.Any(token => ContainsToken(normalized, ZaloBotIntelligence.Normalize(token)));
    }

    private static bool ContainsToken(string normalizedText, string normalizedToken)
    {
        if (normalizedToken.Length == 0) return false;
        if (normalizedToken.Contains(' '))
            return normalizedText.Contains(normalizedToken, StringComparison.Ordinal);
        if (normalizedToken.Length >= 4)
            return normalizedText.Contains(normalizedToken, StringComparison.Ordinal);
        var padded = $" {normalizedText} ";
        return padded.Contains($" {normalizedToken} ", StringComparison.Ordinal);
    }
}

internal static class ZaloRoastAngleScorer
{
    public static IReadOnlyList<ZaloRoastAngle> Rank(
        IEnumerable<ZaloRoastAngle> candidates,
        IReadOnlyList<string> previousRoasts,
        DateTimeOffset now)
    {
        return candidates
            .Select(angle => angle with { Score = Score(angle, previousRoasts, now) })
            .OrderByDescending(angle => angle.Score)
            .ThenByDescending(angle => angle.Evidence.Count == 0 ? DateTimeOffset.MinValue : angle.Evidence.Max(item => item.SentAt))
            .ThenBy(angle => angle.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static int Score(
        ZaloRoastAngle angle,
        IReadOnlyList<string> previousRoasts,
        DateTimeOffset now)
    {
        var score = angle.Score + RecencyScore(angle.Evidence, now) + EvidenceQualityScore(angle.Evidence) - UsedPenalty(angle.Evidence);
        if (previousRoasts.Count == 0) return score;

        var fingerprint = string.Join(' ',
            angle.Summary,
            string.Join(' ', angle.Signals),
            string.Join(' ', angle.Evidence.Select(item => item.Content)));
        var maxOverlap = previousRoasts
            .Select(previous => ZaloRoastValidator.Similarity(fingerprint, previous))
            .DefaultIfEmpty(0)
            .Max();
        return score - (maxOverlap >= .55 ? 35 : maxOverlap >= .35 ? 15 : 0);
    }

    private static int RecencyScore(IReadOnlyList<ZaloDailyBlameEvidence> evidence, DateTimeOffset now)
    {
        if (evidence.Count == 0) return 0;
        var newest = evidence.Max(item => item.SentAt);
        var age = now - newest;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age <= TimeSpan.FromDays(2)) return 18;
        if (age <= TimeSpan.FromDays(7)) return 12;
        if (age <= TimeSpan.FromDays(14)) return 7;
        return 2;
    }

    private static int EvidenceQualityScore(IEnumerable<ZaloDailyBlameEvidence> evidence) =>
        Math.Min(24, evidence.Sum(item => Math.Min(8, item.Score)));

    private static int UsedPenalty(IEnumerable<ZaloDailyBlameEvidence> evidence) =>
        evidence.Count(item => item.UsedRecently) * 45;
}

internal sealed class ZaloRoastTextGenerator(
    IZaloAiGateway aiGateway,
    ILogger logger)
{
    private const string Prompt = """
        Bạn là copywriter cho NPC trong group bóng chuyền Zalo. Backend đã PHÂN TÍCH XONG và chọn đúng một góc roast có bằng chứng. Bạn KHÔNG được tự suy luận thêm fact, không được chọn góc khác và không được roast người thứ hai.

        NHIỆM VỤ DUY NHẤT:
        - Viết lại ContextPack.SelectedAngle thành 1 tin DAILY BLAME dài, sắc, vui, kiểu forensic audit.
        - 4-6 câu, khoảng 300-850 ký tự. Câu cuối là punchline mạnh nhất.
        - Mở đầu chính xác bằng [[TARGET]]. Không tự viết @Tên; backend sẽ tag UID thật.
        - Có thể quote/paraphrase evidence, bắt mâu thuẫn, KPI, pattern, phản dame theo Summary/Signals đã cho.
        - Chỉ dùng facts xuất hiện trong SelectedAngle.Evidence/NearbyReplies/Summary. Không bịa tính cách, scandal, thành tích hay sự kiện.
        - NearbyReplies chỉ là bằng chứng phụ; không chuyển target sang người phản hồi.
        - Không đụng gia đình, ngoại hình/cơ thể, bệnh tật, khuyết tật, giới/giới tính, xu hướng tính dục, chủng tộc, tôn giáo, tình dục, private data, đe dọa hay tự hại.
        - PreviousRoasts là bài cũ của cùng target: tránh lặp punchline, phép so sánh, cấu trúc.

        Chỉ trả JSON thuần:
        {"targetId":"...","angleId":"...","text":"[[TARGET]] ...","evidenceIds":["..."]}
        """;

    public async Task<ZaloDailyBlameSection?> GenerateAsync(
        ZaloRoastContextPack context,
        CancellationToken cancellationToken)
    {
        if (!aiGateway.IsConfigured || context.SelectedAngle.Kind == ZaloRoastAngleKind.LowEvidence)
            return null;

        var payload = JsonSerializer.Serialize(new
        {
            ContextPack = new
            {
                context.LocalDate,
                Target = new
                {
                    targetId = context.Target.ZaloUserId,
                    context.Target.DisplayName,
                    context.Target.Style
                },
                SelectedAngle = new
                {
                    context.SelectedAngle.Id,
                    kind = context.SelectedAngle.Kind.ToString(),
                    context.SelectedAngle.Score,
                    context.SelectedAngle.Summary,
                    context.SelectedAngle.Signals,
                    Evidence = context.SelectedAngle.Evidence.Select(item => new
                    {
                        item.MessageId,
                        item.Content,
                        sentAt = item.SentAt.ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM HH:mm"),
                        item.NearbyReplies
                    })
                },
                PreviousRoasts = context.PreviousRoasts.Take(6)
            }
        });

        try
        {
            var result = await aiGateway.CompleteAsync(
                new ZaloAiCompletionRequest(
                    ZaloAiWorkload.SocialReply,
                    [new ZaloAiChatMessage("system", Prompt), new ZaloAiChatMessage("user", payload)],
                    Temperature: .82,
                    MaxTokens: 900,
                    CorrelationId: $"daily-blame:{context.LocalDate}:{context.Target.ZaloUserId}:{context.SelectedAngle.Id}",
                    UseAnswerBudget: true),
                cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content)) return null;
            return Parse(result.Content, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Daily blame copywriter failed Target={TargetName} Angle={AngleId}", context.Target.DisplayName, context.SelectedAngle.Id);
            return null;
        }
    }

    internal static ZaloDailyBlameSection? Parse(string raw, ZaloRoastContextPack context)
    {
        try
        {
            var json = StripCodeFence(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var targetId = root.TryGetProperty("targetId", out var targetNode) ? targetNode.GetString()?.Trim() ?? string.Empty : string.Empty;
            var angleId = root.TryGetProperty("angleId", out var angleNode) ? angleNode.GetString()?.Trim() ?? string.Empty : string.Empty;
            var text = root.TryGetProperty("text", out var textNode) ? textNode.GetString()?.Trim() ?? string.Empty : string.Empty;
            var modelIds = root.TryGetProperty("evidenceIds", out var idsNode) && idsNode.ValueKind == JsonValueKind.Array
                ? idsNode.EnumerateArray().Select(item => item.GetString()?.Trim() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(4)
                    .ToArray()
                : [];

            if (!string.Equals(targetId, context.Target.ZaloUserId, StringComparison.Ordinal) ||
                !string.Equals(angleId, context.SelectedAngle.Id, StringComparison.Ordinal))
                return null;

            var ownedIds = context.SelectedAngle.Evidence
                .Select(item => item.MessageId)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            var allowedIds = ownedIds.ToHashSet(StringComparer.Ordinal);
            if (modelIds.Any(id => !allowedIds.Contains(id)))
                return null;
            var section = new ZaloDailyBlameSection(targetId, text, ownedIds);
            return ZaloRoastValidator.Validate(section, context).Accepted ? section : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine >= 0) text = text[(firstNewLine + 1)..];
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? text[..lastFence] : text).Trim();
    }
}

internal sealed record ZaloRoastValidationResult(bool Accepted, string Reason, double MaxSimilarity = 0);

internal static class ZaloRoastValidator
{
    private static readonly ZaloTrashTalkPlan SafeRoastPlan =
        new(false, ZaloTrashTalkLevel.Friend, false, false, false, "daily_grounded_roast");

    public static ZaloRoastValidationResult Validate(ZaloDailyBlameSection section, ZaloRoastContextPack context)
    {
        if (!string.Equals(section.TargetId, context.Target.ZaloUserId, StringComparison.Ordinal))
            return new(false, "wrong_target");
        if (string.IsNullOrWhiteSpace(section.Text) ||
            !section.Text.StartsWith("[[TARGET]]", StringComparison.Ordinal) ||
            section.Text.Length is < 120 or > 1000)
            return new(false, "invalid_text_shape");
        if (section.Text.IndexOf("[[TARGET]]", "[[TARGET]]".Length, StringComparison.Ordinal) >= 0)
            return new(false, "duplicate_target_placeholder");
        if (section.Text.Contains('@'))
            return new(false, "model_created_mention");
        if (!ZaloSocialSafetyPolicy.IsSafeCandidate(section.Text, SafeRoastPlan))
            return new(false, "unsafe_text");

        var allowedIds = context.SelectedAngle.Evidence
            .Select(item => item.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        if (allowedIds.Count > 0 && section.EvidenceIds.Count == 0)
            return new(false, "missing_evidence");
        if (section.EvidenceIds.Any(id => !allowedIds.Contains(id)))
            return new(false, "foreign_evidence");

        var maxSimilarity = context.PreviousRoasts
            .Select(previous => Similarity(section.Text, previous))
            .DefaultIfEmpty(0)
            .Max();
        if (maxSimilarity >= .72)
            return new(false, "too_similar_to_previous", maxSimilarity);

        return new(true, "ok", maxSimilarity);
    }

    internal static double Similarity(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.Ordinal).Count();
        var union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokens(string value) =>
        ZaloBotIntelligence.Normalize(RemoveBoilerplate(value))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);

    private static string RemoveBoilerplate(string value) =>
        (value ?? string.Empty)
            .Replace("🚨 NPC DAILY BLAME", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("📌 Hồ sơ hôm nay lấy từ log thật của chính nhân vật. NPC không bịa, NPC chỉ audit =))", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[[TARGET]]", string.Empty, StringComparison.Ordinal);
}
