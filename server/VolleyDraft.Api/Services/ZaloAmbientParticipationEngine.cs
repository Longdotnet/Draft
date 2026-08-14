using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public enum ZaloAmbientParticipationKind
{
    None,
    Fact,
    Social,
    Action
}

public sealed record ZaloAmbientSettings(
    bool Enabled,
    bool ShadowMode,
    int WouldReplyThreshold,
    int RecentWindowMinutes,
    int MaxRecentMessages,
    int BotCooldownSeconds,
    int BusyGroupMessagesPerTwoMinutes)
{
    public static ZaloAmbientSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:Enabled", true),
        ShadowMode: configuration.GetValue("ZaloBot:Ambient:ShadowMode", true),
        WouldReplyThreshold: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:WouldReplyThreshold", 65), 40, 95),
        RecentWindowMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:RecentWindowMinutes", 5), 1, 30),
        MaxRecentMessages: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:MaxRecentMessages", 40), 5, 100),
        BotCooldownSeconds: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:BotCooldownSeconds", 20), 0, 300),
        BusyGroupMessagesPerTwoMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:BusyGroupMessagesPerTwoMinutes", 8), 3, 50));
}

public sealed record ZaloAmbientGroupSituation(
    int RecentMessageCount,
    int RecentTwoMinuteMessageCount,
    int DistinctParticipantCount,
    int RecentBotMessageCount,
    DateTimeOffset? LastBotMessageAt,
    IReadOnlyList<string> RecentMessageIds);

public sealed record ZaloAmbientParticipationDecision(
    bool WouldReply,
    int Score,
    ZaloAmbientParticipationKind Kind,
    string Intent,
    double IntentConfidence,
    IReadOnlyList<string> Signals,
    ZaloAmbientGroupSituation Situation);

/// <summary>
/// Pure deterministic policy for deciding whether an unaddressed group message is
/// interesting enough that a future ambient bot would participate. Phase 1 only
/// records this decision in shadow mode; it never sends a message or mutates domain data.
/// </summary>
public static class ZaloAmbientParticipationEngine
{
    private static readonly Regex QuestionPattern = new(
        @"\?|(?<![a-z0-9])(?:ai|bao nhieu|may|chua|sao|dau|nao|gi|du chua|con .* khong|co .* khong)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionPattern = new(
        @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DomainPattern = new(
        @"(?<![a-z0-9])(?:vote|poll|slot|draft|team|doi|roster|danh\s+sach|san|tran|keo|waitlist|cho\s+slot)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Acknowledgements = new(StringComparer.Ordinal)
    {
        "ok", "oke", "okay", "uh", "uhm", "um", "roi", "duoc", "chuan", "ngon",
        "haha", "hehe", "hihi", "kk", "kkk", "cam on", "thanks", "thank you", "yes", "yep"
    };

    public static ZaloAmbientParticipationDecision Evaluate(
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientGroupSituation situation,
        ZaloAmbientSettings settings,
        DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var content = incoming.Content ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(content);
        var signals = new List<string>();

        if (incoming.MentionedBot)
        {
            return new ZaloAmbientParticipationDecision(
                false,
                0,
                ZaloAmbientParticipationKind.None,
                ZaloBotIntent.Unknown.ToString(),
                0,
                ["explicit_address_uses_normal_router"],
                situation);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ZaloAmbientParticipationDecision(
                false,
                0,
                ZaloAmbientParticipationKind.None,
                ZaloBotIntent.Unknown.ToString(),
                0,
                ["empty_message"],
                situation);
        }

        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(content);
        var factIntent = IsFactIntent(deterministic.Intent);
        var operationalIntent = deterministic.Intent is not ZaloBotIntent.Unknown
            and not ZaloBotIntent.GeneralChat
            and not ZaloBotIntent.Help;
        var actionIntent = operationalIntent && !factIntent;
        var question = QuestionPattern.IsMatch(normalized);
        var hasSession = SessionPattern.IsMatch(normalized);
        var hasDomainWords = DomainPattern.IsMatch(normalized);
        var acknowledgement = IsAcknowledgementOrEmojiOnly(normalized);
        var quote = ZaloQuotedContextResolver.Resolve(incoming, content);

        var kind = factIntent
            ? ZaloAmbientParticipationKind.Fact
            : actionIntent
                ? ZaloAmbientParticipationKind.Action
                : question
                    ? ZaloAmbientParticipationKind.Social
                    : ZaloAmbientParticipationKind.None;

        var score = 0;
        if (factIntent)
        {
            score += 55;
            signals.Add("fact_intent");
        }
        else if (actionIntent)
        {
            // An ambient participant may notice an operational action request, but
            // action/mutation execution always requires the normal explicitly-addressed path.
            score += 20;
            signals.Add("action_requires_address");
        }

        if (question)
        {
            score += 25;
            signals.Add("question");
        }
        if (hasSession)
        {
            score += 15;
            signals.Add("session_reference");
        }
        if (hasDomainWords)
        {
            score += 15;
            signals.Add("volley_domain_language");
        }

        if (quote.HasQuote && !quote.RepliesToBot)
        {
            score -= 15;
            signals.Add("reply_to_member");
        }

        if (acknowledgement)
        {
            score -= 60;
            signals.Add("ack_or_emoji_only");
        }

        if (settings.BotCooldownSeconds > 0 &&
            situation.LastBotMessageAt is { } lastBot &&
            current - lastBot < TimeSpan.FromSeconds(settings.BotCooldownSeconds))
        {
            score -= 30;
            signals.Add("bot_cooldown");
        }

        if (situation.RecentTwoMinuteMessageCount >= settings.BusyGroupMessagesPerTwoMinutes)
        {
            score -= 20;
            signals.Add("busy_group");
        }
        else if (situation.RecentTwoMinuteMessageCount <= 2)
        {
            score += 5;
            signals.Add("quiet_group");
        }

        score = Math.Clamp(score, 0, 100);
        var wouldReply = !acknowledgement &&
                         kind is ZaloAmbientParticipationKind.Fact or ZaloAmbientParticipationKind.Social &&
                         score >= settings.WouldReplyThreshold;

        return new ZaloAmbientParticipationDecision(
            wouldReply,
            score,
            kind,
            deterministic.Intent.ToString(),
            deterministic.Confidence,
            signals.Distinct(StringComparer.Ordinal).ToArray(),
            situation);
    }

    private static bool IsFactIntent(ZaloBotIntent intent) => intent is
        ZaloBotIntent.SessionSchedule or
        ZaloBotIntent.SelfMembership or
        ZaloBotIntent.LocationParking or
        ZaloBotIntent.MissingSlots or
        ZaloBotIntent.UpcomingSessions or
        ZaloBotIntent.Roster or
        ZaloBotIntent.WeeklySessionCount or
        ZaloBotIntent.ModelInfo or
        ZaloBotIntent.TeamLineup or
        ZaloBotIntent.ReminderStatus or
        ZaloBotIntent.WaitlistStatus or
        ZaloBotIntent.ActionHistory or
        ZaloBotIntent.ListMembersWithoutRecentVote or
        ZaloBotIntent.ListMembersWithoutRecentMessage or
        ZaloBotIntent.GetMemberLastActivity or
        ZaloBotIntent.GetMemberLastVote or
        ZaloBotIntent.GetMemberLastMessage or
        ZaloBotIntent.AnalyzeMemberVoteActivity or
        ZaloBotIntent.AnalyzeMemberMessageActivity or
        ZaloBotIntent.AnalyzeGroupEngagement or
        ZaloBotIntent.ListMostInactiveMembers or
        ZaloBotIntent.ListAtRiskMembers or
        ZaloBotIntent.GetActivitySyncStatus;

    private static bool IsAcknowledgementOrEmojiOnly(string normalized)
    {
        var words = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ").Trim();
        if (words.Length == 0) return true;
        return Acknowledgements.Contains(words);
    }
}
