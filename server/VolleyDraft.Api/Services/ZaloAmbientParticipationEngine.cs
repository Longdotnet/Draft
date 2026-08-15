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
/// interesting enough that a future ambient bot would participate. Conversational
/// turns that explicitly talk about/to the bot may be treated as read-only Fact turns;
/// mutation requests remain Action and are never ambient-authorized.
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

    private static readonly Regex ConversationalTeamFeasibilityPattern = new(
        @"(?:ban|bot|npc).*(?:xep|lam).*(?:(?:duoc|dc)\s*(?:khong|ko|k)|co\s+the)",
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
        DateTimeOffset? now = null,
        bool hasActiveProposal = false)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var content = incoming.Content ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(content);
        var signals = new List<string>();

        if (incoming.MentionedBot)
        {
            return new ZaloAmbientParticipationDecision(
                false, 0, ZaloAmbientParticipationKind.None, ZaloBotIntent.Unknown.ToString(), 0,
                ["explicit_address_uses_normal_router"], situation);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ZaloAmbientParticipationDecision(
                false, 0, ZaloAmbientParticipationKind.None, ZaloBotIntent.Unknown.ToString(), 0,
                ["empty_message"], situation);
        }

        var address = ZaloConversationalAddressResolver.Resolve(incoming, hasActiveProposal);
        var capabilityTurn = address.Target == ZaloConversationalTarget.Bot &&
                             address.SpeechAct == ZaloConversationalSpeechAct.AskCapability;

        var shorthandTeamFeasibility = address.Target != ZaloConversationalTarget.AnotherMember &&
                                       ZaloNaturalCommandParser.TryParseTeamPreference(content, out _) &&
                                       ConversationalTeamFeasibilityPattern.IsMatch(normalized);
        var advisorTurn = shorthandTeamFeasibility ||
                          (address.Target == ZaloConversationalTarget.Bot &&
                           address.SpeechAct is ZaloConversationalSpeechAct.AskFeasibility or
                               ZaloConversationalSpeechAct.RequestPreview or
                               ZaloConversationalSpeechAct.ClarificationAnswer or
                               ZaloConversationalSpeechAct.Confirm or
                               ZaloConversationalSpeechAct.Cancel);
        var conversationalReadOnly = capabilityTurn || advisorTurn;

        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(content);
        var effectiveIntent = capabilityTurn
            ? ZaloBotIntent.Help
            : advisorTurn
                ? ZaloBotIntent.TeamPreference
                : deterministic.Intent;
        var factIntent = conversationalReadOnly || IsFactIntent(effectiveIntent);
        var operationalIntent = effectiveIntent is not ZaloBotIntent.Unknown
            and not ZaloBotIntent.GeneralChat
            and not ZaloBotIntent.Help;
        var actionIntent = operationalIntent && !factIntent;
        var question = QuestionPattern.IsMatch(normalized);
        var hasSession = SessionPattern.IsMatch(normalized);
        var hasDomainWords = DomainPattern.IsMatch(normalized);
        var acknowledgement = IsAcknowledgementOrEmojiOnly(normalized) && !hasActiveProposal;
        var quote = ZaloQuotedContextResolver.Resolve(incoming, content);
        var repliesToMember = quote.HasQuote && !quote.RepliesToBot;
        var botCooldown = settings.BotCooldownSeconds > 0 &&
                          situation.LastBotMessageAt is { } lastBot &&
                          current - lastBot < TimeSpan.FromSeconds(settings.BotCooldownSeconds);

        var kind = factIntent
            ? ZaloAmbientParticipationKind.Fact
            : actionIntent
                ? ZaloAmbientParticipationKind.Action
                : question
                    ? ZaloAmbientParticipationKind.Social
                    : ZaloAmbientParticipationKind.None;

        var score = 0;
        if (conversationalReadOnly)
        {
            // Once a turn is deterministically identified as talking to/about the bot,
            // make it independently pass the high-confidence Fact pilot floor (85).
            // This does not relax ordinary ambient traffic or write/action requests.
            score += 90;
            signals.Add(capabilityTurn ? "bot_capability_inquiry" : "conversational_action_advisor");
            signals.Add(shorthandTeamFeasibility ? "team_preference_bot_question_shorthand" : address.Reason);
        }
        else if (factIntent)
        {
            score += 55;
            signals.Add("fact_intent");
        }
        else if (actionIntent)
        {
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

        if (repliesToMember)
        {
            score -= conversationalReadOnly ? 0 : 15;
            signals.Add("reply_to_member");
        }
        if (acknowledgement)
        {
            score -= 60;
            signals.Add("ack_or_emoji_only");
        }
        if (botCooldown)
        {
            score -= conversationalReadOnly ? 0 : 30;
            signals.Add("bot_cooldown");
        }
        if (situation.RecentTwoMinuteMessageCount >= settings.BusyGroupMessagesPerTwoMinutes)
        {
            score -= conversationalReadOnly ? 0 : 20;
            signals.Add("busy_group");
        }
        else if (situation.RecentTwoMinuteMessageCount <= 2)
        {
            score += 5;
            signals.Add("quiet_group");
        }

        score = Math.Clamp(score, 0, 100);
        var hardSuppressed = acknowledgement ||
                             (repliesToMember && !conversationalReadOnly) ||
                             (botCooldown && !conversationalReadOnly) ||
                             actionIntent;
        var wouldReply = !hardSuppressed &&
                         kind is ZaloAmbientParticipationKind.Fact or ZaloAmbientParticipationKind.Social &&
                         score >= settings.WouldReplyThreshold;

        var conversationalConfidence = shorthandTeamFeasibility ? .91 : address.Confidence;
        return new ZaloAmbientParticipationDecision(
            wouldReply,
            score,
            kind,
            effectiveIntent.ToString(),
            conversationalReadOnly ? conversationalConfidence : deterministic.Confidence,
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
