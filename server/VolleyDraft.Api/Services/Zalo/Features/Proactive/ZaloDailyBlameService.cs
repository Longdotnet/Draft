using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services.Zalo.AI;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDailyBlameTarget(string ZaloUserId, string DisplayName, string Style);

internal sealed record ZaloDailyBlameEvidence(
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    DateTimeOffset SentAt,
    int Score,
    IReadOnlyList<string> NearbyReplies,
    bool UsedRecently = false);

internal sealed record ZaloDailyBlameMessageRow(
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    DateTimeOffset SentAt);

internal sealed record ZaloDailyBlameSection(
    string TargetId,
    string Text,
    IReadOnlyList<string> EvidenceIds);

internal sealed record ZaloDailyBlameCandidate(
    string TargetId,
    string Text,
    IReadOnlyList<BridgeOutgoingMention> Mentions,
    IReadOnlyList<string> EvidenceIds,
    string? ImageBase64 = null,
    string? ImageContentType = null,
    string? ImageFileName = null);

internal sealed record ZaloDailyBlameSettings(
    bool Enabled,
    bool SendEnabled,
    string GroupId,
    int LookbackDays,
    int MaxEvidencePerTarget,
    int StickerChancePercent,
    int PhotoChancePercent,
    int ExtraTargetsPerDay,
    int MinimumMessagesForExtraTarget,
    int ExtraTargetCooldownDays,
    IReadOnlyList<ZaloDailyBlameTarget> Targets)
{
    public static ZaloDailyBlameSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("ZaloBot:Ambient:DailyBlame");
        var targets = section.GetSection("Targets").GetChildren()
            .Select(item => new ZaloDailyBlameTarget(
                Clean(item["ZaloUserId"]),
                Clean(item["DisplayName"]),
                Clean(item["Style"])))
            .Where(item => item.ZaloUserId.Length > 0 && item.DisplayName.Length > 0)
            .DistinctBy(item => item.ZaloUserId, StringComparer.Ordinal)
            .ToArray();

        return new ZaloDailyBlameSettings(
            section.GetValue("Enabled", false),
            section.GetValue("SendEnabled", false),
            Clean(section["GroupId"]),
            Math.Clamp(section.GetValue("LookbackDays", 30), 7, 90),
            Math.Clamp(section.GetValue("MaxEvidencePerTarget", 24), 4, 80),
            Math.Clamp(section.GetValue("StickerChancePercent", 70), 0, 100),
            Math.Clamp(section.GetValue("PhotoChancePercent", 25), 0, 100),
            Math.Clamp(section.GetValue("ExtraTargetsPerDay", 2), 0, 6),
            Math.Clamp(section.GetValue("MinimumMessagesForExtraTarget", 3), 3, 50),
            Math.Clamp(section.GetValue("ExtraTargetCooldownDays", 10), 1, 60),
            targets);
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}

/// <summary>
/// Grounded daily roast lane. Every configured target receives one separate message per
/// local day; after those required targets, the lane can rotate additional current group
/// members that have enough real message history. A cycle sends at most one target, so
/// retries stay isolated. Wording is generated from that member's real recent group messages
/// and durable history prevents the same evidence/punchline from being reused too soon.
/// </summary>
public sealed class ZaloDailyBlameService
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan SendLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(22) };
    private static readonly ZaloTrashTalkPlan SafeRoastPlan =
        new(false, ZaloTrashTalkLevel.Friend, false, false, false, "daily_grounded_roast");

    private readonly VolleyDraftDbContext db;
    private readonly ZaloBridgeClient bridge;
    private readonly ILogger<ZaloDailyBlameService> logger;
    private readonly ZaloDailyBlameSettings settings;
    private readonly IZaloAiGateway aiGateway;
    private readonly ZaloProactiveMessageStore proactiveStore;

    public ZaloDailyBlameService(
        VolleyDraftDbContext db,
        ZaloBridgeClient bridge,
        ILogger<ZaloDailyBlameService> logger,
        IConfiguration configuration)
    {
        this.db = db;
        this.bridge = bridge;
        this.logger = logger;
        settings = ZaloDailyBlameSettings.FromConfiguration(configuration);
        aiGateway = ZaloAiGatewayFactory.Create(SharedHttpClient, configuration, logger);
        proactiveStore = new ZaloProactiveMessageStore(db);
    }

    internal ZaloDailyBlameService(
        VolleyDraftDbContext db,
        ZaloBridgeClient bridge,
        ILogger<ZaloDailyBlameService> logger,
        ZaloDailyBlameSettings settings,
        IZaloAiGateway aiGateway)
    {
        this.db = db;
        this.bridge = bridge;
        this.logger = logger;
        this.settings = settings;
        this.aiGateway = aiGateway;
        proactiveStore = new ZaloProactiveMessageStore(db);
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || !settings.SendEnabled || settings.GroupId.Length == 0 || settings.Targets.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var localNow = now.ToOffset(VietnamOffset);
        var target = (await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.GroupId, settings.GroupId, StringComparison.Ordinal));
        if (target is null) return 0;

        await proactiveStore.EnsureAsync(cancellationToken);
        var localDate = localNow.ToString("yyyy-MM-dd");
        var history = await proactiveStore.GetHistoryAsync(target.ConnectionId, target.GroupId, 1000, cancellationToken);
        var blameTarget = SelectPendingTarget(settings.Targets, history, localDate);
        blameTarget ??= await SelectExtraTargetAsync(
            target.ConnectionId,
            target.GroupId,
            target.AccountId,
            history,
            localDate,
            localNow,
            cancellationToken);
        if (blameTarget is null) return 0;

        var idempotencyKey = $"daily-blame:{target.ConnectionId}:{target.GroupId}:{localDate}:{blameTarget.ZaloUserId}";
        if (!await proactiveStore.TryAcquireLaneLeaseAsync(
                target.ConnectionId,
                target.GroupId,
                ZaloProactiveLane.DailyBlame,
                idempotencyKey,
                now,
                SendLeaseDuration,
                cancellationToken))
            return 0;

        var accepted = false;
        try
        {
            var usedEvidence = ExtractUsedEvidenceIds(history, now.AddDays(-21));
            var evidence = await LoadEvidenceAsync(
                target.ConnectionId,
                target.GroupId,
                [blameTarget],
                usedEvidence,
                cancellationToken);
            var previousRoasts = history
                .Where(item =>
                    string.Equals(item.Lane, ZaloProactiveLane.DailyBlame, StringComparison.Ordinal) &&
                    string.Equals(item.SubjectUserId, blameTarget.ZaloUserId, StringComparison.Ordinal))
                .OrderByDescending(item => item.SentAt)
                .Take(10)
                .Select(item => item.MessageText)
                .ToArray();

            var targetEvidence = evidence.GetValueOrDefault(blameTarget.ZaloUserId) ?? [];
            var candidate = await BuildCandidateAsync(
                localNow,
                blameTarget,
                targetEvidence,
                previousRoasts,
                target.ConnectionId,
                target.GroupId,
                cancellationToken);
            if (candidate is null)
            {
                await proactiveStore.ReleaseLaneLeaseAsync(
                    target.ConnectionId,
                    target.GroupId,
                    ZaloProactiveLane.DailyBlame,
                    idempotencyKey,
                    cancellationToken);
                return 0;
            }

            var response = await bridge.SendGroupMessageAsync(
                target.AccountId,
                target.GroupId,
                candidate.Text,
                candidate.Mentions,
                idempotencyKey: idempotencyKey,
                imageBase64: candidate.ImageBase64,
                imageContentType: candidate.ImageContentType,
                imageFileName: candidate.ImageFileName);
            if (!response.Sent)
            {
                await proactiveStore.ReleaseLaneLeaseAsync(
                    target.ConnectionId,
                    target.GroupId,
                    ZaloProactiveLane.DailyBlame,
                    idempotencyKey,
                    cancellationToken);
                return 0;
            }

            accepted = true;
            var contentKey = "evidence:" + string.Join(',', candidate.EvidenceIds.Distinct(StringComparer.Ordinal));
            await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                Guid.NewGuid().ToString("n"),
                target.ConnectionId,
                target.GroupId,
                localDate,
                ZaloProactiveLane.DailyBlame,
                "daily_blame_member",
                contentKey,
                blameTarget.ZaloUserId,
                blameTarget.DisplayName,
                candidate.Text,
                now,
                response.MessageId,
                idempotencyKey), cancellationToken);
            await proactiveStore.ReleaseLaneLeaseAsync(
                target.ConnectionId,
                target.GroupId,
                ZaloProactiveLane.DailyBlame,
                idempotencyKey,
                cancellationToken);

            if (candidate.ImageBase64 is null && ShouldUseMedia(localDate, blameTarget.ZaloUserId, "sticker", settings.StickerChancePercent))
            {
                var reaction = SelectStickerReaction(blameTarget.ZaloUserId, localDate);
                await bridge.TrySendGroupStickerAsync(
                    target.AccountId,
                    target.GroupId,
                    reaction,
                    $"{idempotencyKey}:sticker",
                    cancellationToken);
            }

            logger.LogInformation(
                "Daily blame sent Group={GroupId} Date={LocalDate} Target={TargetName} Evidence={EvidenceCount} Photo={HasPhoto}",
                target.GroupId,
                localDate,
                blameTarget.DisplayName,
                candidate.EvidenceIds.Count,
                candidate.ImageBase64 is not null);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!accepted)
            {
                try
                {
                    await proactiveStore.ReleaseLaneLeaseAsync(
                        target.ConnectionId,
                        target.GroupId,
                        ZaloProactiveLane.DailyBlame,
                        idempotencyKey,
                        cancellationToken);
                }
                catch
                {
                    // Best effort. The short lease expires by itself.
                }
            }

            logger.LogWarning(
                exception,
                "Daily blame cycle failed Group={GroupId} Target={TargetName}",
                target.GroupId,
                blameTarget.DisplayName);
            return 0;
        }
    }

    internal static ZaloDailyBlameTarget? SelectPendingTarget(
        IReadOnlyList<ZaloDailyBlameTarget> targets,
        IEnumerable<ZaloProactiveMessageHistoryData> history,
        string localDate)
    {
        var sentIds = history
            .Where(item =>
                string.Equals(item.Lane, ZaloProactiveLane.DailyBlame, StringComparison.Ordinal) &&
                string.Equals(item.LocalDate, localDate, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.SubjectUserId))
            .Select(item => item.SubjectUserId!)
            .ToHashSet(StringComparer.Ordinal);
        return targets.FirstOrDefault(item => !sentIds.Contains(item.ZaloUserId));
    }

    private async Task<ZaloDailyBlameTarget?> SelectExtraTargetAsync(
        string connectionId,
        string groupId,
        string botAccountId,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history,
        string localDate,
        DateTimeOffset localNow,
        CancellationToken cancellationToken)
    {
        if (settings.ExtraTargetsPerDay <= 0) return null;

        var requiredIds = settings.Targets
            .Select(item => item.ZaloUserId)
            .ToHashSet(StringComparer.Ordinal);
        var todayExtraIds = history
            .Where(item =>
                string.Equals(item.Lane, ZaloProactiveLane.DailyBlame, StringComparison.Ordinal) &&
                string.Equals(item.LocalDate, localDate, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.SubjectUserId) &&
                !requiredIds.Contains(item.SubjectUserId!))
            .Select(item => item.SubjectUserId!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (todayExtraIds.Count >= settings.ExtraTargetsPerDay) return null;

        var cooldownCutoff = localNow.AddDays(-settings.ExtraTargetCooldownDays);
        var extraHistory = history
            .Where(item =>
                string.Equals(item.Lane, ZaloProactiveLane.DailyBlame, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.SubjectUserId) &&
                !requiredIds.Contains(item.SubjectUserId!))
            .ToArray();
        var recentlyRoasted = extraHistory
            .Where(item => item.SentAt >= cooldownCutoff)
            .Select(item => item.SubjectUserId!)
            .ToHashSet(StringComparer.Ordinal);
        var lastRoastedAt = extraHistory
            .GroupBy(item => item.SubjectUserId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.SentAt), StringComparer.Ordinal);

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.IsCurrentMember)
            .Select(item => new
            {
                item.ZaloUserId,
                item.DisplayName
            })
            .ToListAsync(cancellationToken);
        var candidates = members
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ZaloUserId) &&
                !string.IsNullOrWhiteSpace(item.DisplayName) &&
                !requiredIds.Contains(item.ZaloUserId) &&
                !todayExtraIds.Contains(item.ZaloUserId) &&
                !recentlyRoasted.Contains(item.ZaloUserId) &&
                !string.Equals(item.ZaloUserId, botAccountId, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0) return null;

        var candidateIds = candidates.Select(item => item.ZaloUserId).ToArray();
        var cutoff = localNow.ToUniversalTime().AddDays(-settings.LookbackDays);
        var messages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                !item.IsFromBot &&
                item.SentAt >= cutoff &&
                candidateIds.Contains(item.SenderId))
            .OrderByDescending(item => item.SentAt)
            .Take(5000)
            .Select(item => new ExtraTargetMessage(item.SenderId, item.Content, item.SentAt))
            .ToListAsync(cancellationToken);

        var bySender = messages
            .GroupBy(item => item.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ranked = candidates
            .Select(member =>
            {
                bySender.TryGetValue(member.ZaloUserId, out var memberMessages);
                memberMessages ??= [];
                var safeMessages = memberMessages
                    .Where(item => IsSafeEvidence(item.Content))
                    .ToArray();
                var score = ScoreExtraTarget(member.ZaloUserId, localDate, safeMessages, localNow);
                var fairness = ScoreExtraTargetFairness(
                    lastRoastedAt.GetValueOrDefault(member.ZaloUserId),
                    localNow);
                return new
                {
                    member.ZaloUserId,
                    member.DisplayName,
                    MessageCount = safeMessages.Length,
                    Score = score + fairness
                };
            })
            .Where(item => item.MessageCount >= settings.MinimumMessagesForExtraTarget)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ZaloUserId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (ranked is null) return null;

        return new ZaloDailyBlameTarget(
            ranked.ZaloUserId,
            ranked.DisplayName.Trim(),
            "forensic audit toàn group: chỉ dùng log thật; ưu tiên mâu thuẫn, pattern lặp, tự gáy và reply/counter có bằng chứng");
    }

    internal static int ScoreExtraTargetForTest(
        string targetId,
        string localDate,
        IReadOnlyList<(string Content, DateTimeOffset SentAt)> messages,
        DateTimeOffset localNow) =>
        ScoreExtraTarget(
            targetId,
            localDate,
            messages.Select(item => new ExtraTargetMessage(targetId, item.Content, item.SentAt)).ToArray(),
            localNow);

    internal static int ScoreExtraTargetFairness(DateTimeOffset? lastRoastedAt, DateTimeOffset localNow)
    {
        if (lastRoastedAt is null) return 300;
        var age = localNow.ToUniversalTime() - lastRoastedAt.Value.ToUniversalTime();
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return Math.Min(240, (int)Math.Floor(age.TotalDays * 8));
    }

    private sealed record ExtraTargetMessage(string SenderId, string Content, DateTimeOffset SentAt);

    private static int ScoreExtraTarget(
        string targetId,
        string localDate,
        IReadOnlyList<ExtraTargetMessage> messages,
        DateTimeOffset localNow)
    {
        if (messages.Count == 0) return int.MinValue;
        var evidenceScore = messages
            .OrderByDescending(item => ScoreEvidence(item.Content))
            .Take(8)
            .Sum(item => Math.Min(10, ScoreEvidence(item.Content)));
        var newest = messages.Max(item => item.SentAt);
        var age = localNow.ToUniversalTime() - newest;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var recency = age <= TimeSpan.FromDays(1) ? 25 :
            age <= TimeSpan.FromDays(3) ? 18 :
            age <= TimeSpan.FromDays(7) ? 10 : 3;
        var activity = Math.Min(40, messages.Count * 2);
        var dailyTieBreak = StableDailyBucket(localDate, targetId, 17);
        return evidenceScore + recency + activity + dailyTieBreak;
    }

    private static int StableDailyBucket(string localDate, string targetId, int modulo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{localDate}:{targetId}:extra-target"));
        return (((int)bytes[0] << 8) | bytes[1]) % Math.Max(1, modulo);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ZaloDailyBlameEvidence>>> LoadEvidenceAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloDailyBlameTarget> targets,
        IReadOnlySet<string> usedEvidence,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays);
        var targetIds = targets.Select(item => item.ZaloUserId).ToArray();
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                !item.IsFromBot &&
                item.SentAt >= cutoff &&
                targetIds.Contains(item.SenderId))
            .OrderByDescending(item => item.SentAt)
            .Take(1200)
            .Select(item => new ZaloDailyBlameMessageRow(
                item.MessageId,
                item.SenderId,
                item.SenderName,
                item.Content,
                item.SentAt))
            .ToListAsync(cancellationToken);

        var earliest = rows.Count == 0 ? DateTimeOffset.UtcNow : rows.Min(item => item.SentAt).AddMinutes(-3);
        var recentGroup = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                !item.IsFromBot &&
                item.SentAt >= earliest)
            .OrderByDescending(item => item.SentAt)
            .Take(2500)
            .Select(item => new ZaloDailyBlameMessageRow(
                item.MessageId,
                item.SenderId,
                item.SenderName,
                item.Content,
                item.SentAt))
            .ToListAsync(cancellationToken);
        var replyRelations = await new ZaloMessageGraphStore(db)
            .LoadReplyRelationsAsync(connectionId, groupId, cancellationToken);

        var result = new Dictionary<string, IReadOnlyList<ZaloDailyBlameEvidence>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var candidates = rows
                .Where(item => string.Equals(item.SenderId, target.ZaloUserId, StringComparison.Ordinal))
                .Where(item => IsSafeEvidence(item.Content))
                .Select(item => new ZaloDailyBlameEvidence(
                    item.MessageId,
                    item.SenderId,
                    item.SenderName,
                    CleanContent(item.Content, 220),
                    item.SentAt,
                    ScoreEvidence(item.Content),
                    FindNearbyReplies(item.MessageId, item.SenderId, item.SentAt, recentGroup, replyRelations),
                    usedEvidence.Contains(item.MessageId)))
                .OrderBy(item => item.UsedRecently)
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.SentAt)
                .Take(settings.MaxEvidencePerTarget)
                .ToArray();
            result[target.ZaloUserId] = candidates;
        }

        return result;
    }

    private async Task<ZaloDailyBlameCandidate?> BuildCandidateAsync(
        DateTimeOffset localNow,
        ZaloDailyBlameTarget target,
        IReadOnlyList<ZaloDailyBlameEvidence> evidence,
        IReadOnlyList<string> previousRoasts,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var context = ZaloRoastContextBuilder.Build(target, evidence, previousRoasts, localNow);
        var generator = new ZaloRoastTextGenerator(aiGateway, logger);
        var section = await generator.GenerateAsync(context, cancellationToken);
        var rendered = section is null ? string.Empty : RenderTarget(section.Text, target);
        if (section is null || !IsSafeOutput(rendered))
        {
            logger.LogInformation(
                "Daily blame using grounded fallback Target={TargetName} Angle={AngleId} Kind={AngleKind} AiConfigured={AiConfigured}",
                target.DisplayName,
                context.SelectedAngle.Id,
                context.SelectedAngle.Kind,
                aiGateway.IsConfigured);
            section = BuildValidatedFallback(context, localNow);
            rendered = RenderTarget(section.Text, target);
        }

        var text = string.Join("\n\n",
            $"🚨 NPC DAILY BLAME — {localNow:dd/MM}",
            rendered,
            "📌 Hồ sơ hôm nay lấy từ log thật của chính nhân vật. NPC không bịa, NPC chỉ audit =))");
        var mentions = BuildMentions(text, [target]);
        if (mentions.Count != 1) return null;

        string? imageBase64 = null;
        string? imageContentType = null;
        string? imageFileName = null;
        if (ShouldUseMedia(localNow.ToString("yyyy-MM-dd"), target.ZaloUserId, "photo", settings.PhotoChancePercent))
        {
            var photo = await TryLoadAssignedPhotoAsync(connectionId, groupId, target.ZaloUserId, cancellationToken);
            if (photo is not null)
            {
                imageBase64 = Convert.ToBase64String(photo.Value.Data);
                imageContentType = photo.Value.ContentType;
                imageFileName = photo.Value.FileName;
            }
        }

        return new ZaloDailyBlameCandidate(
            target.ZaloUserId,
            text,
            mentions,
            section.EvidenceIds.Distinct(StringComparer.Ordinal).ToArray(),
            imageBase64,
            imageContentType,
            imageFileName);
    }

    private static ZaloDailyBlameSection BuildValidatedFallback(
        ZaloRoastContextPack context,
        DateTimeOffset localNow)
    {
        for (var index = 0; index < context.RankedAngles.Count; index++)
        {
            var angle = context.RankedAngles[index];
            var candidateContext = context with { SelectedAngle = angle };
            var section = BuildFallbackSection(context.Target, angle, localNow.AddDays(index));
            if (ZaloRoastValidator.Validate(section, candidateContext).Accepted)
                return section;
        }

        return BuildFallbackSection(
            context.Target,
            new ZaloRoastAngle(
                "low-evidence",
                ZaloRoastAngleKind.LowEvidence,
                1,
                "Log chưa đủ bằng chứng mạnh.",
                [],
                ["insufficient_evidence"]),
            localNow);
    }

    internal static ZaloDailyBlameSection BuildFallbackSection(
        ZaloDailyBlameTarget target,
        ZaloRoastAngle angle,
        DateTimeOffset localNow)
    {
        if (angle.Evidence.Count == 0 || angle.Kind == ZaloRoastAngleKind.LowEvidence)
        {
            return new ZaloDailyBlameSection(
                target.ZaloUserId,
                "[[TARGET]] hôm nay log chưa có đủ một pha tự gáy, mâu thuẫn hay phản dame nào đủ mạnh để roast cho đáng. NPC không ghép đại hai câu vô nghĩa chỉ để đủ bài. Ca này ghi nhận là thiếu chứng cứ, còn khi log có chuyện thật thì hồ sơ sẽ tự nóng lên =))",
                []);
        }

        var evidenceIds = angle.Evidence
            .Select(item => item.MessageId)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var first = angle.Evidence[0];
        var firstQuote = Quote(first.Content, 170);
        var text = angle.Kind switch
        {
            ZaloRoastAngleKind.Contradiction => BuildContradictionFallback(angle, firstQuote),
            ZaloRoastAngleKind.CounterReply => BuildCounterReplyFallback(first, firstQuote),
            ZaloRoastAngleKind.RepeatedPattern => BuildPatternFallback(angle),
            _ => $"[[TARGET]] tự nộp nguyên vật liệu bằng câu “{firstQuote}”. Câu này tự thân đã đủ làm biên bản nên NPC không cần ghép thêm log cho có vẻ sâu sắc. Hồ sơ hôm nay gọn: chính chủ phát biểu, timestamp đóng dấu, phần còn lại là cả group tự chấm điểm độ tự huỷ =))"
        };
        return new ZaloDailyBlameSection(
            target.ZaloUserId,
            text,
            evidenceIds);
    }

    private static string BuildContradictionFallback(ZaloRoastAngle angle, string firstQuote)
    {
        var second = angle.Evidence.Count > 1 ? angle.Evidence[1] : null;
        if (second is null)
            return $"[[TARGET]] tự để lại câu “{firstQuote}”. Log chưa đủ vế đối chiếu nên NPC chỉ ghi đúng một dòng, không chế thêm tình tiết. Nhưng riêng câu này cũng đã đủ mang mùi tự gáy để vào hồ sơ hôm nay =))";
        var secondQuote = Quote(second.Content, 170);
        return $"[[TARGET]] tự tạo một pha quay xe có timestamp: trước đó nói “{firstQuote}”, rồi log sau lại chốt “{secondQuote}”. Hai câu này có quan hệ rõ ràng chứ không phải NPC bốc đại hai tin nhắn đứng cạnh nhau. Phòng audit chỉ cần đặt vế A cạnh vế B là bản tự kiểm điểm tự viết xong; NPC còn chưa kịp thêm muối =))";
    }

    private static string BuildCounterReplyFallback(ZaloDailyBlameEvidence item, string firstQuote)
    {
        var exact = item.NearbyReplies
            .Where(reply => reply.StartsWith("REPLY ·", StringComparison.Ordinal))
            .Select(StripReplyPrefix)
            .Where(reply => reply.Length > 0)
            .Take(2)
            .ToArray();
        var nearby = item.NearbyReplies
            .Where(reply => reply.StartsWith("NEARBY ·", StringComparison.Ordinal))
            .Select(StripReplyPrefix)
            .Where(reply => reply.Length > 0)
            .Take(1)
            .ToArray();
        var counters = exact.Length > 0 ? exact : nearby;
        if (counters.Length == 0)
            return $"[[TARGET]] vừa phát hành thông cáo “{firstQuote}”. Không có reply đủ rõ để NPC gắn thêm lời người khác, nên hồ sơ dừng đúng ở câu chính chủ tự nói. Cũng may câu này tự tin đủ để tự đứng một mình trên bảng kiểm điểm =))";
        var counterText = string.Join(" / ", counters.Select(reply => $"“{Quote(reply, 150)}”"));
        var relation = exact.Length > 0 ? "reply trực tiếp" : "chat ngay sau đó";
        return $"[[TARGET]] vừa phát hành thông cáo “{firstQuote}”. Chưa kịp đóng dấu thành tích thì {relation} đã đáp {counterText}. Đây mới đúng nghĩa audit có đối chứng: một bên tự gáy, một bên phản hồi ngay trong cùng mạch chat. NPC không cần dựng punchline nữa vì phần phản dame đã được group viết hộ rồi =))";
    }

    private static string BuildPatternFallback(ZaloRoastAngle angle)
    {
        var samples = angle.Evidence
            .Take(3)
            .Select(item => $"“{Quote(item.Content, 125)}”")
            .ToArray();
        var joined = string.Join(" → ", samples);
        return $"[[TARGET]] không dính một câu lẻ, mà để lại hẳn một pattern: {joined}. Đây là chuỗi cùng một chủ đề lặp lại trong log, nên NPC roast vào thói quen thật chứ không ghép hai câu vô can. Một lần là tình cờ, lặp tới mức thành quy trình thì phòng audit xin phép gọi đó là nghiệp vụ =))";
    }

    private static string StripReplyPrefix(string value)
    {
        var separator = value.IndexOf(':');
        return separator >= 0 && separator + 1 < value.Length
            ? value[(separator + 1)..].Trim()
            : value.Trim();
    }

    private static string Quote(string? value, int maxLength) =>
        CleanContent(value, maxLength)
            .Replace("@", string.Empty, StringComparison.Ordinal)
            .Replace('“', '\'')
            .Replace('”', '\'');

    internal static IReadOnlyList<BridgeOutgoingMention> BuildMentions(
        string text,
        IReadOnlyList<ZaloDailyBlameTarget> targets)
    {
        var mentions = new List<BridgeOutgoingMention>();
        var searchStart = 0;
        foreach (var target in targets)
        {
            var label = $"@{target.DisplayName}";
            var position = text.IndexOf(label, searchStart, StringComparison.Ordinal);
            if (position < 0) continue;
            mentions.Add(new BridgeOutgoingMention(target.ZaloUserId, position, label.Length));
            searchStart = position + label.Length;
        }
        return mentions;
    }

    internal static HashSet<string> ExtractUsedEvidenceIds(
        IEnumerable<ZaloProactiveMessageHistoryData> history,
        DateTimeOffset cutoff)
    {
        const string prefix = "evidence:";
        return history
            .Where(item => string.Equals(item.Lane, ZaloProactiveLane.DailyBlame, StringComparison.Ordinal) && item.SentAt >= cutoff)
            .SelectMany(item => item.ContentKey?.StartsWith(prefix, StringComparison.Ordinal) == true
                ? item.ContentKey[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [])
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<(byte[] Data, string ContentType, string FileName)?> TryLoadAssignedPhotoAsync(
        string connectionId,
        string groupId,
        string zaloUserId,
        CancellationToken cancellationToken)
    {
        var photo = await db.ZaloMemberPhotoAssignments
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.ZaloUserId == zaloUserId &&
                item.ImageAsset.Size > 0 &&
                item.ImageAsset.Size <= 5_000_000)
            .Select(item => new
            {
                item.ImageAsset.Data,
                item.ImageAsset.ContentType,
                item.ImageAsset.FileName
            })
            .SingleOrDefaultAsync(cancellationToken);
        return photo is null
            ? null
            : (photo.Data, photo.ContentType, photo.FileName);
    }

    internal static bool ShouldUseMedia(string localDate, string targetId, string kind, int chancePercent)
    {
        chancePercent = Math.Clamp(chancePercent, 0, 100);
        if (chancePercent <= 0) return false;
        if (chancePercent >= 100) return true;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{localDate}:{targetId}:{kind}"));
        var bucket = (((int)bytes[0] << 8) | bytes[1]) % 100;
        return bucket < chancePercent;
    }

    internal static ZaloStickerReaction SelectStickerReaction(string targetId, string localDate)
    {
        ZaloStickerReaction[] reactions =
        [
            ZaloStickerReaction.Laugh,
            ZaloStickerReaction.Facepalm,
            ZaloStickerReaction.Wow,
            ZaloStickerReaction.Cheer
        ];
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{localDate}:{targetId}:reaction"));
        return reactions[bytes[0] % reactions.Length];
    }

    private static IReadOnlyList<string> FindNearbyReplies(
        string messageId,
        string senderId,
        DateTimeOffset sentAt,
        IReadOnlyList<ZaloDailyBlameMessageRow> rows,
        IReadOnlyDictionary<string, ZaloMessageGraphRelation> relations)
    {
        var result = new List<string>();
        var exactReplyIds = relations.Values
            .Where(relation =>
                string.Equals(relation.ToMessageId, messageId, StringComparison.Ordinal) &&
                string.Equals(relation.RelationType, "ReplyTo", StringComparison.Ordinal))
            .Select(relation => relation.FromMessageId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in rows.Where(item => exactReplyIds.Contains(item.MessageId)))
        {
            if (item.SenderId == senderId || !IsSafeEvidence(item.Content)) continue;
            result.Add($"REPLY · {CleanContent(item.SenderName, 60)}: {CleanContent(item.Content, 180)}");
            if (result.Count >= 2) return result;
        }

        foreach (var item in rows)
        {
            if (item.MessageId == messageId || item.SenderId == senderId) continue;
            if (item.SentAt < sentAt || item.SentAt - sentAt > TimeSpan.FromMinutes(3)) continue;
            if (!IsSafeEvidence(item.Content)) continue;
            var nearby = $"NEARBY · {CleanContent(item.SenderName, 60)}: {CleanContent(item.Content, 180)}";
            if (result.Contains(nearby, StringComparer.Ordinal)) continue;
            result.Add(nearby);
            if (result.Count >= 2) break;
        }
        return result;
    }

    private static int ScoreEvidence(string? content)
    {
        var text = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var score = Math.Min(4, text.Length / 35);
        string[] interesting =
        [
            "kpi", "block", "slot", "team", "vote", "share", "pass", "luoi", "bo", "bo bich",
            "di choi vs bo", "the hien tot hon", "de thuong", "hoc hanh", "dap", "thu tien", "sorry", "quay", "co ban"
        ];
        score += interesting.Count(token => ContainsEvidenceToken(text, token)) * 3;
        if (text.Contains(":))", StringComparison.Ordinal) || text.Contains("=))", StringComparison.Ordinal) || text.Contains("🤣", StringComparison.Ordinal)) score += 2;
        if (text.Contains('@')) score += 1;
        return score;
    }

    private static bool ContainsEvidenceToken(string normalizedText, string token)
    {
        var normalizedToken = ZaloBotIntelligence.Normalize(token);
        if (normalizedToken.Contains(' '))
            return normalizedText.Contains(normalizedToken, StringComparison.Ordinal);
        if (normalizedToken.Length >= 4)
            return normalizedText.Contains(normalizedToken, StringComparison.Ordinal);
        return $" {normalizedText} ".Contains($" {normalizedToken} ", StringComparison.Ordinal);
    }

    private static bool IsSafeEvidence(string? content)
    {
        var text = CleanContent(content, 220);
        if (text.Length < 4) return false;
        return ZaloSocialSafetyPolicy.IsSafeCandidate(text, SafeRoastPlan);
    }

    private static bool IsSafeOutput(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        content.Length <= 1200 &&
        ZaloSocialSafetyPolicy.IsSafeCandidate(content, SafeRoastPlan);

    private static string RenderTarget(string text, ZaloDailyBlameTarget target) =>
        text.Replace("[[TARGET]]", $"@{target.DisplayName}", StringComparison.Ordinal).Trim();

    private static string CleanContent(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
