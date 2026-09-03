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
        WouldReplyThreshold: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:WouldReplyThreshold", 60), 40, 95),
        RecentWindowMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:RecentWindowMinutes", 5), 1, 30),
        MaxRecentMessages: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:MaxRecentMessages", 40), 5, 100),
        BotCooldownSeconds: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:BotCooldownSeconds", 2), 0, 300),
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
        bool hasActiveProposal = false,
        bool hasActiveLease = false)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var content = incoming.Content ?? string.Empty;
        var normalized = ZaloBotIntelligence.Normalize(content);
        var signals = new List<string>();

        if (incoming.MentionedBot)
            return new(false, 0, ZaloAmbientParticipationKind.None, ZaloBotIntent.Unknown.ToString(), 0,
                ["explicit_address_uses_normal_router"], situation);
        if (string.IsNullOrWhiteSpace(normalized))
            return new(false, 0, ZaloAmbientParticipationKind.None, ZaloBotIntent.Unknown.ToString(), 0,
                ["empty_message"], situation);

        var quote = ZaloQuotedContextResolver.Resolve(incoming, content);
        var repliesToMember = quote.HasQuote && !quote.RepliesToBot;
        var address = ZaloConversationalAddressResolver.Resolve(incoming, hasActiveProposal);
        var leaseEligible = hasActiveLease && !repliesToMember &&
                            address.Target != ZaloConversationalTarget.AnotherMember;
        var wakeTurn = address.Target == ZaloConversationalTarget.Bot && ZaloAmbientWakePhrase.IsMatch(content);
        var capabilityTurn = address.Target == ZaloConversationalTarget.Bot &&
                             address.SpeechAct == ZaloConversationalSpeechAct.AskCapability;

        var parsedTeamPreference = ZaloNaturalCommandParser.TryParseTeamPreference(content, out _);
        var genericFeasibilityTurn = address.Target == ZaloConversationalTarget.Bot &&
                                     address.SpeechAct == ZaloConversationalSpeechAct.AskFeasibility &&
                                     !parsedTeamPreference;
        var shorthandTeamFeasibility = address.Target != ZaloConversationalTarget.AnotherMember &&
                                       parsedTeamPreference && ConversationalTeamFeasibilityPattern.IsMatch(normalized);
        var leaseTeamAdvisor = leaseEligible && parsedTeamPreference;
        var advisorTurn = shorthandTeamFeasibility || leaseTeamAdvisor ||
                          (address.Target == ZaloConversationalTarget.Bot &&
                           ((address.SpeechAct == ZaloConversationalSpeechAct.AskFeasibility && parsedTeamPreference) ||
                            address.SpeechAct is ZaloConversationalSpeechAct.RequestPreview or
                                ZaloConversationalSpeechAct.ClarificationAnswer or
                                ZaloConversationalSpeechAct.Confirm or
                                ZaloConversationalSpeechAct.Cancel));

        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(content);
        var naturalReadOnlyTurn = ZaloAmbientReadOnlyNaturalIntentResolver.TryResolve(
            content,
            out var naturalReadOnlyIntent);
        var leaseInferredIntent = leaseEligible && deterministic.Intent == ZaloBotIntent.Unknown
            ? InferLeaseFactIntent(normalized)
            : ZaloBotIntent.Unknown;
        var effectiveIntent = wakeTurn || capabilityTurn || genericFeasibilityTurn
            ? ZaloBotIntent.Help
            : advisorTurn
                ? ZaloBotIntent.TeamPreference
                : naturalReadOnlyTurn
                    ? naturalReadOnlyIntent
                    : leaseInferredIntent != ZaloBotIntent.Unknown
                        ? leaseInferredIntent
                        : deterministic.Intent;
        var leaseFactFollowUp = leaseEligible && IsFactIntent(effectiveIntent);
        var conversationalReadOnly = wakeTurn || capabilityTurn || genericFeasibilityTurn || advisorTurn || leaseFactFollowUp;
        var factIntent = conversationalReadOnly || naturalReadOnlyTurn || IsFactIntent(effectiveIntent);
        var operationalIntent = effectiveIntent is not ZaloBotIntent.Unknown
            and not ZaloBotIntent.GeneralChat
            and not ZaloBotIntent.Help;
        var actionIntent = operationalIntent && !factIntent;
        var leaseSocialFollowUp = leaseEligible && !actionIntent && !factIntent &&
                                  effectiveIntent is (ZaloBotIntent.Unknown or ZaloBotIntent.GeneralChat);
        var directConversationalTurn = conversationalReadOnly || leaseSocialFollowUp;
        var question = QuestionPattern.IsMatch(normalized);
        var hasSession = SessionPattern.IsMatch(normalized);
        var hasDomainWords = DomainPattern.IsMatch(normalized);
        var acknowledgement = IsAcknowledgementOrEmojiOnly(normalized) && !hasActiveProposal;
        var botCooldown = settings.BotCooldownSeconds > 0 && situation.LastBotMessageAt is { } lastBot &&
                          current - lastBot < TimeSpan.FromSeconds(settings.BotCooldownSeconds);

        var kind = factIntent ? ZaloAmbientParticipationKind.Fact
            : actionIntent ? ZaloAmbientParticipationKind.Action
            : leaseSocialFollowUp || question ? ZaloAmbientParticipationKind.Social
            : ZaloAmbientParticipationKind.None;

        var score = 0;
        if (conversationalReadOnly)
        {
            score += 90;
            signals.Add(wakeTurn ? "bot_plain_text_wake"
                : capabilityTurn ? "bot_capability_inquiry"
                : genericFeasibilityTurn ? "bot_feasibility_clarification"
                : advisorTurn ? "conversational_action_advisor"
                : "lease_fact_followup");
            if (leaseFactFollowUp || leaseTeamAdvisor)
                signals.Add("active_conversation_lease");
            else
                signals.Add(shorthandTeamFeasibility ? "team_preference_bot_question_shorthand" : address.Reason);
        }
        else if (leaseSocialFollowUp)
        {
            // A recent successful reply to this exact sender in this exact group is
            // enough addressing context for natural conversation. It does not grant
            // domain authority: Action was rejected above and Fact stays on the
            // authoritative responder path.
            score += 90;
            signals.Add("active_conversation_lease");
            signals.Add("lease_social_followup");
        }
        else if (naturalReadOnlyTurn)
        {
            // Natural status language is deterministic and read-only, but unlike a
            // direct bot address it still obeys human-thread/cooldown suppression.
            score += 70;
            signals.Add("natural_readonly_status");
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

        if (leaseInferredIntent != ZaloBotIntent.Unknown) signals.Add("lease_inferred_fact_intent");
        if (question) { score += 25; signals.Add("question"); }
        if (hasSession) { score += 15; signals.Add("session_reference"); }
        if (hasDomainWords) { score += 15; signals.Add("volley_domain_language"); }
        if (repliesToMember) { score -= directConversationalTurn ? 0 : 15; signals.Add("reply_to_member"); }
        if (acknowledgement) { score -= 60; signals.Add("ack_or_emoji_only"); }
        if (botCooldown) { score -= directConversationalTurn ? 0 : 30; signals.Add("bot_cooldown"); }
        if (situation.RecentTwoMinuteMessageCount >= settings.BusyGroupMessagesPerTwoMinutes)
        {
            score -= directConversationalTurn ? 0 : 20;
            signals.Add("busy_group");
        }
        else if (situation.RecentTwoMinuteMessageCount <= 2)
        {
            score += 5;
            signals.Add("quiet_group");
        }

        score = Math.Clamp(score, 0, 100);
        var hardSuppressed = acknowledgement ||
                             (repliesToMember && !directConversationalTurn) ||
                             (botCooldown && !directConversationalTurn) ||
                             actionIntent;
        var wouldReply = !hardSuppressed &&
                         kind is ZaloAmbientParticipationKind.Fact or ZaloAmbientParticipationKind.Social &&
                         score >= settings.WouldReplyThreshold;

        var conversationalConfidence = wakeTurn ? .99
            : genericFeasibilityTurn ? .97
            : leaseFactFollowUp || leaseTeamAdvisor || leaseSocialFollowUp ? .96
            : shorthandTeamFeasibility ? .91
            : address.Confidence;
        var effectiveConfidence = directConversationalTurn
            ? conversationalConfidence
            : naturalReadOnlyTurn
                ? .95
                : deterministic.Confidence;
        return new ZaloAmbientParticipationDecision(
            wouldReply,
            score,
            kind,
            effectiveIntent.ToString(),
            effectiveConfidence,
            signals.Distinct(StringComparer.Ordinal).ToArray(),
            situation);
    }

    private static ZaloBotIntent InferLeaseFactIntent(string normalized)
    {
        if (!SessionPattern.IsMatch(normalized)) return ZaloBotIntent.Unknown;
        var slotSubject = Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:slot|nguoi|cho)(?![a-z0-9])", RegexOptions.CultureInvariant);
        var slotState = Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:con|nhieu|it|thieu|du|het|day|trong)(?![a-z0-9])", RegexOptions.CultureInvariant);
        if (slotSubject && slotState) return ZaloBotIntent.MissingSlots;
        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:may\s+gio|gio|luc\s+nao|khi\s+nao|thoi\s+gian)(?![a-z0-9])", RegexOptions.CultureInvariant))
            return ZaloBotIntent.SessionSchedule;
        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:danh\s+sach|roster|ai\s+danh|ai\s+choi|co\s+ai)(?![a-z0-9])", RegexOptions.CultureInvariant))
            return ZaloBotIntent.Roster;
        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:san|dia\s+diem|gui\s+xe|bai\s+xe)(?![a-z0-9])", RegexOptions.CultureInvariant))
            return ZaloBotIntent.LocationParking;
        return ZaloBotIntent.Unknown;
    }

    private static bool IsFactIntent(ZaloBotIntent intent) => intent is
        ZaloBotIntent.SessionSchedule or ZaloBotIntent.SelfMembership or ZaloBotIntent.LocationParking or
        ZaloBotIntent.MissingSlots or ZaloBotIntent.UpcomingSessions or ZaloBotIntent.Roster or
        ZaloBotIntent.WeeklySessionCount or ZaloBotIntent.ModelInfo or ZaloBotIntent.TeamLineup or
        ZaloBotIntent.ReminderStatus or ZaloBotIntent.WaitlistStatus or ZaloBotIntent.ActionHistory or
        ZaloBotIntent.ListMembersWithoutRecentVote or ZaloBotIntent.ListMembersWithoutRecentMessage or
        ZaloBotIntent.GetMemberLastActivity or ZaloBotIntent.GetMemberLastVote or
        ZaloBotIntent.GetMemberLastMessage or ZaloBotIntent.AnalyzeMemberVoteActivity or
        ZaloBotIntent.AnalyzeMemberMessageActivity or ZaloBotIntent.AnalyzeGroupEngagement or
        ZaloBotIntent.ListMostInactiveMembers or ZaloBotIntent.ListAtRiskMembers or
        ZaloBotIntent.GetActivitySyncStatus;

    private static bool IsAcknowledgementOrEmojiOnly(string normalized)
    {
        var words = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ").Trim();
        return words.Length == 0 || Acknowledgements.Contains(words);
    }
}
