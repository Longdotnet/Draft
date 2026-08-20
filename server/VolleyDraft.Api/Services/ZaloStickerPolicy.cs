using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Services;

internal enum ZaloStickerReaction
{
    Laugh,
    Cheer,
    Love,
    Wow,
    Sad,
    Sorry,
    Facepalm,
    GoodJob,
    Bye
}

internal static class ZaloStickerPolicy
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSentByGroup = new(StringComparer.Ordinal);

    public static bool TryPlan(
        string accountId,
        string groupId,
        string message,
        string? imageUrl,
        string? idempotencyKey,
        IConfiguration configuration,
        DateTimeOffset now,
        out ZaloStickerReaction reaction)
    {
        reaction = default;
        if (!configuration.GetValue("ZaloBot:Sticker:Enabled", true)) return false;
        if (!string.IsNullOrWhiteSpace(imageUrl)) return false;
        if (ZaloBridgeClient.ParseParentMessageId(accountId, idempotencyKey) is null) return false;

        var clean = (message ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > 260 || clean.Contains('\n') || clean.Contains('\r')) return false;
        if (LooksOperational(clean)) return false;

        var inferred = InferReaction(clean);
        if (inferred is null) return false;

        var chancePercent = Math.Clamp(configuration.GetValue("ZaloBot:Sticker:ChancePercent", 65), 0, 100);
        if (chancePercent <= 0) return false;
        if (chancePercent < 100 && StableBucket(idempotencyKey!, inferred.Value) >= chancePercent) return false;

        var cooldownSeconds = Math.Clamp(configuration.GetValue("ZaloBot:Sticker:CooldownSeconds", 90), 0, 3600);
        var groupKey = $"{accountId.Trim()}:{groupId.Trim()}";
        while (true)
        {
            if (LastSentByGroup.TryGetValue(groupKey, out var lastSent))
            {
                if (now - lastSent < TimeSpan.FromSeconds(cooldownSeconds)) return false;
                if (LastSentByGroup.TryUpdate(groupKey, now, lastSent)) break;
                continue;
            }

            if (LastSentByGroup.TryAdd(groupKey, now)) break;
        }

        reaction = inferred.Value;
        return true;
    }

    internal static ZaloStickerReaction? InferReaction(string message)
    {
        var raw = (message ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0) return null;
        var normalized = ZaloBotIntelligence.Normalize(raw);

        if (raw.Contains('🙏') || HasAny(normalized, "xin loi", "sorry", "tha loi"))
            return ZaloStickerReaction.Sorry;
        if (raw.Contains('🤦') || HasAny(normalized, "bo tay", "chiu luon", "facepalm"))
            return ZaloStickerReaction.Facepalm;
        if (raw.Contains('😂') || raw.Contains('🤣') || raw.Contains('😆') ||
            raw.Contains("=))", StringComparison.Ordinal) || HasAny(normalized, "haha", "hehe", "cuoi"))
            return ZaloStickerReaction.Laugh;
        if (raw.Contains('🥳') || raw.Contains('🎉') || raw.Contains('🔥') ||
            HasAny(normalized, "qua dinh", "chay qua", "yay", "an mung"))
            return ZaloStickerReaction.Cheer;
        if (raw.Contains('❤') || raw.Contains('🫶') || raw.Contains('🥰') || HasAny(normalized, "thuong", "love", "yeu qua"))
            return ZaloStickerReaction.Love;
        if (raw.Contains('😱') || raw.Contains('🤯') || raw.Contains('😮') || HasAny(normalized, "wow", "ghe vay", "ao vay"))
            return ZaloStickerReaction.Wow;
        if (raw.Contains('😭') || raw.Contains('🥲') || raw.Contains('😢') || HasAny(normalized, "buon", "khoc"))
            return ZaloStickerReaction.Sad;
        if (raw.Contains('👏') || raw.Contains('💪') || HasAny(normalized, "good job", "hay lam", "danh ngon", "lam ngon"))
            return ZaloStickerReaction.GoodJob;
        if (raw.Contains('👋') || HasAny(normalized, "bye", "tam biet", "ngu ngon", "good night"))
            return ZaloStickerReaction.Bye;

        return null;
    }

    internal static string ToWireValue(ZaloStickerReaction reaction) => reaction switch
    {
        ZaloStickerReaction.Laugh => "laugh",
        ZaloStickerReaction.Cheer => "cheer",
        ZaloStickerReaction.Love => "love",
        ZaloStickerReaction.Wow => "wow",
        ZaloStickerReaction.Sad => "sad",
        ZaloStickerReaction.Sorry => "sorry",
        ZaloStickerReaction.Facepalm => "facepalm",
        ZaloStickerReaction.GoodJob => "good_job",
        ZaloStickerReaction.Bye => "bye",
        _ => throw new ArgumentOutOfRangeException(nameof(reaction))
    };

    private static bool LooksOperational(string message)
    {
        var normalized = ZaloBotIntelligence.Normalize(message);
        string[] markers =
        [
            " slot", "qr ", " poll", " draft", " waitlist", "xac nhan",
            "thanh toan", "danh sach", "lich nhac", "gui xe", "dia diem",
            "con thieu", "da du", "doi hinh", "dong bo"
        ];
        return markers.Any(normalized.Contains);
    }

    private static bool HasAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static int StableBucket(string idempotencyKey, ZaloStickerReaction reaction)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{idempotencyKey}:{reaction}"));
        return (((int)bytes[0] << 8) | bytes[1]) % 100;
    }
}
