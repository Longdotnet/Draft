using System.Globalization;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public enum ZaloTrashTalkLevel
{
    Normal = 0,
    Tease = 1,
    Friend = 2,
    Street = 3,
    Combat = 4
}

internal sealed record ZaloSocialHistoryMessage(string Content, DateTimeOffset SentAt);

internal sealed record ZaloSocialVibeProfile(
    ZaloTrashTalkLevel TrashTalkComfort,
    bool UsesProfanity,
    string EmojiStyle,
    IReadOnlyList<string> SlangTokens,
    int SampleCount);

internal sealed record ZaloInsideJokeHint(string Text, DateTimeOffset SentAt);

internal sealed record ZaloSocialSituation(
    bool DirectToBot,
    bool HumanTargeted,
    bool PileOnRisk,
    int DistinctRecentSpeakers,
    int RecentPlayfulSignals);

internal sealed record ZaloTrashTalkPlan(
    bool CanRoastBack,
    ZaloTrashTalkLevel Level,
    bool AllowProfanity,
    bool AllowHardRoast,
    bool PileOnRisk,
    string Reason);

internal static class ZaloSocialVibeProfileBuilder
{
    private static readonly string[] SlangCatalog =
    [
        "dm", "dmm", "vcl", "vl", "clm", "cc", "deo", "dit", "lon", "loz",
        "ngu", "ga", "phe", "cut", "mom", "cha noi", "thang nay", "=))", ":))", "kkk"
    ];

    public static ZaloSocialVibeProfile Build(IEnumerable<string> messages)
    {
        var rows = messages
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Take(60)
            .ToArray();
        if (rows.Length == 0)
            return new(ZaloTrashTalkLevel.Tease, false, "none", [], 0);

        var normalized = rows.Select(ZaloBotIntelligence.Normalize).ToArray();
        var slangHits = SlangCatalog
            .Where(token => normalized.Any(text => text.Contains(token, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var profanityCount = normalized.Count(ZaloTrashTalkPolicy.ContainsProfanityOrInsult);
        var ratio = (double)profanityCount / rows.Length;
        var comfort = ratio switch
        {
            >= .60 => ZaloTrashTalkLevel.Combat,
            >= .35 => ZaloTrashTalkLevel.Street,
            >= .15 => ZaloTrashTalkLevel.Friend,
            _ => ZaloTrashTalkLevel.Tease
        };
        var emojiStyle = rows.Count(text => text.Contains("=))", StringComparison.Ordinal) || text.Contains(":))", StringComparison.Ordinal)) >= 2
            ? "ascii-laugh"
            : rows.Any(text => text.Any(char.IsSurrogate))
                ? "emoji"
                : "plain";
        return new(comfort, profanityCount > 0, emojiStyle, slangHits, rows.Length);
    }
}

internal static class ZaloSocialSituationEngine
{
    private static readonly Regex HumanVocative = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}\s._-]{0,40}\s+(?:oi|ơi)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ZaloSocialSituation Analyze(
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<ZaloAmbientSocialContextMessage> recent,
        ZaloConversationalAddressDecision address)
    {
        var directToBot = address.Target == ZaloConversationalTarget.Bot && address.Confidence >= .9;
        var normalized = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
        var humanTargeted = !directToBot && HumanVocative.IsMatch(incoming.Content ?? string.Empty);
        var playful = recent.Count(item => ZaloTrashTalkPolicy.ContainsProfanityOrInsult(ZaloBotIntelligence.Normalize(item.Content)));
        var distinct = recent
            .Where(item => !item.IsFromBot)
            .Select(item => item.SenderId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var pileOnRisk = !directToBot && playful >= 3 && distinct >= 3;
        return new(directToBot, humanTargeted, pileOnRisk, distinct, playful + (ZaloTrashTalkPolicy.ContainsProfanityOrInsult(normalized) ? 1 : 0));
    }
}

internal static class ZaloTrashTalkPolicy
{
    private static readonly Regex ProfanityOrInsult = new(
        @"(?<![a-z0-9])(?:dm|dmm|vcl|vl|clm|cc|deo|dit|lon|loz|ngu|ga|phe|cut|mom|oc\s+cho)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StrongProfanity = new(
        @"(?<![a-z0-9])(?:dm|dmm|vcl|clm|lon|loz|oc\s+cho)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ContainsProfanityOrInsult(string? normalized) =>
        !string.IsNullOrWhiteSpace(normalized) && ProfanityOrInsult.IsMatch(normalized);

    public static bool LooksLikeDirectTrashTalk(
        string? content,
        ZaloConversationalAddressDecision address,
        bool leaseTurn)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (!ContainsProfanityOrInsult(normalized)) return false;
        return leaseTurn || (address.Target == ZaloConversationalTarget.Bot && address.Confidence >= .9);
    }

    public static ZaloTrashTalkPlan Decide(
        string? content,
        ZaloSocialVibeProfile profile,
        ZaloSocialSituation situation,
        bool leaseTurn,
        int maxLevel,
        bool allowProfanity,
        bool allowHardRoast)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var direct = situation.DirectToBot || leaseTurn;
        var initiated = direct && ContainsProfanityOrInsult(normalized);
        if (!initiated)
            return new(false, ZaloTrashTalkLevel.Tease, false, false, situation.PileOnRisk, "not_direct_user_initiated_banter");
        if (situation.PileOnRisk || situation.HumanTargeted)
            return new(false, ZaloTrashTalkLevel.Tease, false, false, situation.PileOnRisk, "human_target_or_pile_on");

        var severity = StrongProfanity.IsMatch(normalized)
            ? ZaloTrashTalkLevel.Street
            : ZaloTrashTalkLevel.Friend;
        var requested = (ZaloTrashTalkLevel)Math.Max((int)severity, (int)profile.TrashTalkComfort);
        var configuredMax = (ZaloTrashTalkLevel)Math.Clamp(maxLevel, 0, 4);
        var effective = (ZaloTrashTalkLevel)Math.Min((int)requested, (int)configuredMax);
        if (!allowProfanity && effective > ZaloTrashTalkLevel.Friend)
            effective = ZaloTrashTalkLevel.Friend;
        if (!allowHardRoast && effective > ZaloTrashTalkLevel.Street)
            effective = ZaloTrashTalkLevel.Street;
        return new(true, effective, allowProfanity && effective >= ZaloTrashTalkLevel.Street,
            allowHardRoast && effective == ZaloTrashTalkLevel.Combat, false, "direct_user_initiated_trash_talk");
    }
}

internal static class ZaloInsideJokeRetriever
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "nay", "mai", "hom", "qua", "roi", "nha", "nhe", "thi", "la", "ma", "voi", "cho", "cai",
        "con", "thang", "bot", "npc", "tui", "toi", "may", "ong", "anh", "em", "di", "duoc", "khong"
    };

    public static IReadOnlyList<ZaloInsideJokeHint> FindHints(
        string? current,
        IEnumerable<ZaloSocialHistoryMessage> history,
        int maxHints = 2)
    {
        var currentTokens = Tokens(current);
        if (currentTokens.Count < 2) return [];

        return history
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .Where(item => ZaloSocialSafetyPolicy.IsSafeHistoricalCallback(item.Content))
            .Select(item => new { Item = item, Score = Similarity(currentTokens, Tokens(item.Content)) })
            .Where(item => item.Score >= .55)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.SentAt)
            .Take(Math.Clamp(maxHints, 0, 3))
            .Select(item => new ZaloInsideJokeHint(Trim(item.Item.Content, 120), item.Item.SentAt))
            .ToArray();
    }

    private static HashSet<string> Tokens(string? text)
    {
        var normalized = ZaloBotIntelligence.Normalize(text ?? string.Empty);
        return Regex.Replace(normalized, @"[^a-z0-9\s]", " ", RegexOptions.CultureInvariant)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double Similarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

internal static class ZaloSocialSafetyPolicy
{
    private static readonly Regex ThreatOrSelfHarm = new(
        @"(?:tự\s*tử|tu\s*tu|chết\s*đi|chet\s*di|giết|giet|đập\s*chết|dap\s*chet|xử\s*mày|xu\s*may|tìm\s*tới\s*nhà|tim\s*toi\s*nha)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FamilyAttack = new(
        @"(?:mẹ\s*mày|me\s*may|bố\s*mày|bo\s*may|cha\s*mày|cha\s*may|gia\s*đình|gia\s*dinh)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveTraitAttack = new(
        @"(?:bê\s*đê|be\s*de|pê\s*đê|pe\s*de|da\s*đen|khuyết\s*tật|khuyet\s*tat|tật\s*nguyền|tat\s*nguyen|hồi\s*giáo|hoi\s*giao|công\s*giáo|cong\s*giao|lesbian|\bgay\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppearanceAttack = new(
        @"(?:béo|beo|mập|map|xấu|xau|lùn|lun|mặt\s*xấu|mat\s*xau)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PersonalData = new(
        @"(?:địa\s*chỉ|dia\s*chi|số\s*điện\s*thoại|so\s*dien\s*thoai|cccd|cmnd)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSafeCandidate(string? candidate, ZaloTrashTalkPlan plan)
    {
        var text = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ThreatOrSelfHarm.IsMatch(text) || FamilyAttack.IsMatch(text) || SensitiveTraitAttack.IsMatch(text) ||
            AppearanceAttack.IsMatch(text) || PersonalData.IsMatch(text))
            return false;
        if (!plan.AllowProfanity && ZaloTrashTalkPolicy.ContainsProfanityOrInsult(ZaloBotIntelligence.Normalize(text)))
            return false;
        return true;
    }

    public static bool IsSafeHistoricalCallback(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160) return false;
        return !ThreatOrSelfHarm.IsMatch(value) && !FamilyAttack.IsMatch(value) &&
               !SensitiveTraitAttack.IsMatch(value) && !PersonalData.IsMatch(value);
    }
}
