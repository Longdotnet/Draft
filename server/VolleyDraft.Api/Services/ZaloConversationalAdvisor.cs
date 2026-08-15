using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public enum ZaloConversationalTarget
{
    Unknown,
    Bot,
    Group,
    AnotherMember
}

public enum ZaloConversationalSpeechAct
{
    Unknown,
    AskCapability,
    AskFeasibility,
    RequestPreview,
    RequestMutation,
    ClarificationAnswer,
    Confirm,
    Cancel
}

public sealed record ZaloConversationalAddressDecision(
    ZaloConversationalTarget Target,
    ZaloConversationalSpeechAct SpeechAct,
    double Confidence,
    string Reason);

public sealed record ZaloConversationalAdvisorSettings(bool Enabled, int ProposalTtlMinutes)
{
    public static ZaloConversationalAdvisorSettings FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue("ZaloBot:Ambient:ConversationalAdvisor:Enabled", false),
        Math.Clamp(configuration.GetValue("ZaloBot:Ambient:ConversationalAdvisor:ProposalTtlMinutes", 5), 2, 15));
}

public sealed record ZaloConversationalAdvisorReply(
    string Text,
    string Intent,
    string? SessionId = null,
    bool ProposalActive = false);

public static class ZaloConversationalAddressResolver
{
    private static readonly Regex BotReference = new(
        @"(?<![a-z0-9])(?:bot|npc|con\s+bot|thang\s+bot|cai\s+bot)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Capability = new(
        @"(?:xai|sai|dung).*(?:sao|the\s+nao)|lam\s+duoc\s+gi|co\s+(?:nhung\s+)?chuc\s+nang\s+gi|giup\s+duoc\s+gi|biet\s+lam\s+gi",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnotherMemberVocative = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}\s._-]{0,40}\s+oi\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ZaloConversationalAddressDecision Resolve(
        ZaloIncomingMessageEvent incoming,
        bool hasActiveProposal = false)
    {
        var text = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content ?? string.Empty);

        if (incoming.MentionedBot)
            return new(ZaloConversationalTarget.Bot, DetectSpeechAct(text, hasActiveProposal), 1, "explicit_address");
        if (quote.RepliesToBot)
            return new(ZaloConversationalTarget.Bot, DetectSpeechAct(text, hasActiveProposal), 1, "reply_to_bot");
        if (BotReference.IsMatch(text))
            return new(ZaloConversationalTarget.Bot, DetectSpeechAct(text, hasActiveProposal), .98, "explicit_bot_reference");
        if (hasActiveProposal && IsProposalFollowUp(text))
            return new(ZaloConversationalTarget.Bot, DetectSpeechAct(text, true), .96, "active_proposal_followup");
        if (AnotherMemberVocative.IsMatch(text))
            return new(ZaloConversationalTarget.AnotherMember, ZaloConversationalSpeechAct.Unknown, .95, "member_vocative");

        var teamPreference = ZaloNaturalCommandParser.TryParseTeamPreference(incoming.Content ?? string.Empty, out _);
        if (teamPreference && LooksLikeBotFeasibilityQuestion(text))
            return new(ZaloConversationalTarget.Bot, ZaloConversationalSpeechAct.AskFeasibility, .91, "team_preference_bot_question");

        return new(ZaloConversationalTarget.Unknown, DetectSpeechAct(text, hasActiveProposal), 0, "not_addressed");
    }

    private static ZaloConversationalSpeechAct DetectSpeechAct(string text, bool hasActiveProposal)
    {
        if (ZaloBotIntelligence.IsCancel(text)) return ZaloConversationalSpeechAct.Cancel;
        if (ZaloBotIntelligence.IsConfirmation(text)) return ZaloConversationalSpeechAct.Confirm;
        if (Capability.IsMatch(text)) return ZaloConversationalSpeechAct.AskCapability;
        if (LooksLikeBotFeasibilityQuestion(text)) return ZaloConversationalSpeechAct.AskFeasibility;
        if (ZaloNaturalCommandParser.TryParseTeamPreference(text, out _))
            return Regex.IsMatch(text, @"(?:xep|cho).*(?:di|nha|nhe|luon)$", RegexOptions.CultureInvariant)
                ? ZaloConversationalSpeechAct.RequestMutation
                : ZaloConversationalSpeechAct.RequestPreview;
        if (hasActiveProposal && IsProposalFollowUp(text)) return ZaloConversationalSpeechAct.ClarificationAnswer;
        return ZaloConversationalSpeechAct.Unknown;
    }

    private static bool LooksLikeBotFeasibilityQuestion(string text) =>
        Regex.IsMatch(text, @"(?:ban|bot|npc).*(?:xep|lam).*(?:duoc\s*(?:khong|ko)|duoc\s*k)|(?:xep|lam).*(?:duoc\s*(?:khong|ko)|duoc\s*k)", RegexOptions.CultureInvariant);

    private static bool IsProposalFollowUp(string text) =>
        ZaloBotIntelligence.IsConfirmation(text) ||
        ZaloBotIntelligence.IsCancel(text) ||
        Regex.IsMatch(text, @"^(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?:\s+.*)?$", RegexOptions.CultureInvariant);
}

public static class ZaloBotCapabilityRegistry
{
    public static string BuildOverview() =>
        "Tui là bot hỗ trợ kèo của nhóm 😄. Bạn cứ hỏi tự nhiên, không nhất thiết phải @bot khi đang nói trực tiếp về tui. " +
        "Tui có thể xem lịch/sân, slot và roster, waitlist, thông tin vote, draft/cân bằng team, nhắc lịch và hỗ trợ yêu cầu chơi chung team. " +
        "Các câu chỉ hỏi thông tin hoặc hỏi 'làm được không' thì tui có thể giải thích/preview; việc làm thay đổi team, slot hay dữ liệu sẽ không tự chạy âm thầm — tui sẽ yêu cầu xác nhận rõ trước.";
}

public sealed class ZaloConversationalAdvisor(VolleyDraftDbContext db)
{
    private const string ProposalIntent = "AmbientTeamPreferenceProposal";

    public async Task<ZaloConversationalAdvisorReply?> TryBuildAsync(
        string accountId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        int proposalTtlMinutes,
        CancellationToken cancellationToken = default)
    {
        var senderId = Clean(incoming.SenderId, 100);
        if (senderId.Length == 0) return null;

        var stateStore = new ZaloConversationStateV2Store(db);
        var active = await stateStore.LoadActiveAsync(groupId, senderId, cancellationToken);
        var hasProposal = string.Equals(active?.Intent, ProposalIntent, StringComparison.Ordinal);
        var address = ZaloConversationalAddressResolver.Resolve(incoming, hasProposal);
        if (address.Target != ZaloConversationalTarget.Bot) return null;

        if (address.SpeechAct == ZaloConversationalSpeechAct.AskCapability)
            return new ZaloConversationalAdvisorReply(ZaloBotCapabilityRegistry.BuildOverview(), "BotCapabilityInquiry");

        if (hasProposal)
        {
            var continued = await ContinueProposalAsync(accountId, groupId, incoming, active!, stateStore, proposalTtlMinutes, cancellationToken);
            if (continued is not null) return continued;
        }

        if (address.SpeechAct is not (ZaloConversationalSpeechAct.AskFeasibility or ZaloConversationalSpeechAct.RequestPreview or ZaloConversationalSpeechAct.RequestMutation))
            return null;

        if (!TryExtractTeamPreference(incoming.Content ?? string.Empty, out var requesterReference, out var partnerReference, out var sessionReference))
            return null;

        var resolver = new ZaloIdentityResolver(db);
        var requester = await resolver.ResolveAsync(
            groupId,
            requesterReference,
            currentSenderZaloUserId: senderId,
            cancellationToken: cancellationToken);
        var partner = await resolver.ResolveAsync(
            groupId,
            partnerReference,
            currentSenderZaloUserId: senderId,
            quotedContext: ZaloQuotedContextResolver.Resolve(incoming, incoming.Content ?? string.Empty),
            cancellationToken: cancellationToken);

        if (requester.Status != ZaloIdentityResolutionStatus.Resolved)
            return new("Tui chưa xác định được bạn trong danh sách thành viên Zalo của nhóm.", "TeamPreferenceAdvisor");
        if (partner.Status == ZaloIdentityResolutionStatus.Ambiguous)
        {
            var names = string.Join(", ", partner.Candidates.Take(5).Select(item => item.DisplayName));
            return new($"Tui thấy nhiều người có thể là '{partnerReference}': {names}. Bạn @mention đúng người giúp tui nhé, tui không chọn đại.", "TeamPreferenceAdvisor");
        }
        if (partner.Status != ZaloIdentityResolutionStatus.Resolved)
            return new($"Tui chưa xác định chắc '{partnerReference}' là ai. Bạn @mention người muốn chơi chung giúp tui nhé.", "TeamPreferenceAdvisor");
        if (requester.ZaloUserId == partner.ZaloUserId)
            return new("Hai người trong yêu cầu đang resolve thành cùng một tài khoản nên tui chưa thể tạo đề xuất chung team.", "TeamPreferenceAdvisor");

        var sessions = await LoadSessionsAsync(accountId, groupId, cancellationToken);
        if (sessions.Count == 0)
            return new("Nhóm hiện chưa có kèo đang bật bot để tui kiểm tra.", "TeamPreferenceAdvisor");

        MatchSession? selected = null;
        if (!string.IsNullOrWhiteSpace(sessionReference))
            selected = ResolveSingleSession(sessionReference, sessions);

        if (selected is null)
        {
            var eligible = sessions.Where(session => BothPresent(session, requester.ZaloUserId!, partner.ZaloUserId!)).ToList();
            if (eligible.Count == 1)
            {
                selected = eligible[0];
            }
            else if (eligible.Count > 1)
            {
                await SaveProposalAsync(stateStore, groupId, senderId, incoming.MessageId, requester, partner, null, eligible, proposalTtlMinutes, cancellationToken);
                var choices = string.Join(" hoặc ", eligible.Take(4).Select(item => item.Name));
                return new($"Được. Tui hiểu là {requester.DisplayName} muốn chơi chung team với {partner.DisplayName}. Cả hai đang có mặt ở nhiều kèo: {choices}. Bạn chọn kèo nào?", "TeamPreferenceAdvisor", ProposalActive: true);
            }
        }

        if (selected is null)
            return new($"Tui hiểu yêu cầu chơi chung của {requester.DisplayName} và {partner.DisplayName}, nhưng chưa xác định được một kèo duy nhất. Nói thêm T6/CN/ngày cụ thể giúp tui nhé.", "TeamPreferenceAdvisor", ProposalActive: true);

        return await BuildSelectedSessionReplyAsync(
            stateStore, groupId, senderId, incoming.MessageId, requester, partner, selected, proposalTtlMinutes, cancellationToken);
    }

    private async Task<ZaloConversationalAdvisorReply?> ContinueProposalAsync(
        string accountId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloConversationStateV2Snapshot active,
        ZaloConversationStateV2Store store,
        int ttlMinutes,
        CancellationToken cancellationToken)
    {
        if (ZaloBotIntelligence.IsCancel(incoming.Content ?? string.Empty))
        {
            await store.CancelAsync(groupId, incoming.SenderId, cancellationToken);
            return new("Ok, tui bỏ đề xuất chơi chung team vừa rồi.", "TeamPreferenceProposalCancelled");
        }

        TeamPreferenceProposalData? proposal;
        try { proposal = JsonSerializer.Deserialize<TeamPreferenceProposalData>(active.CollectedArgumentsJson); }
        catch (JsonException) { proposal = null; }
        if (proposal is null) return null;

        if (ZaloBotIntelligence.IsConfirmation(incoming.Content ?? string.Empty))
        {
            return new(
                $"Tui đang giữ đề xuất {proposal.RequesterName} + {proposal.PartnerName} chung team" +
                (proposal.SessionName is null ? "." : $" ở {proposal.SessionName}.") +
                " Để thay đổi đội hình thật, hãy reply tin bot này hoặc @bot và gửi lệnh xác nhận/chơi chung team; ambient advisor không tự ghi dữ liệu.",
                "TeamPreferenceProposalConfirmationRequired",
                proposal.SessionId,
                true);
        }

        var sessions = await LoadSessionsAsync(accountId, groupId, cancellationToken);
        var selected = ResolveSingleSession(incoming.Content ?? string.Empty, sessions);
        if (selected is null) return null;

        var requester = await new ZaloIdentityResolver(db).ResolveAsync(groupId, "tui", currentSenderZaloUserId: proposal.RequesterZaloUserId, cancellationToken: cancellationToken);
        var partner = await new ZaloIdentityResolver(db).ResolveAsync(groupId, proposal.PartnerName, explicitZaloUserId: proposal.PartnerZaloUserId, cancellationToken: cancellationToken);
        if (requester.Status != ZaloIdentityResolutionStatus.Resolved || partner.Status != ZaloIdentityResolutionStatus.Resolved) return null;

        return await BuildSelectedSessionReplyAsync(store, groupId, incoming.SenderId, incoming.MessageId, requester, partner, selected, ttlMinutes, cancellationToken);
    }

    private async Task<ZaloConversationalAdvisorReply> BuildSelectedSessionReplyAsync(
        ZaloConversationStateV2Store store,
        string groupId,
        string senderId,
        string messageId,
        ZaloIdentityResolution requester,
        ZaloIdentityResolution partner,
        MatchSession session,
        int ttlMinutes,
        CancellationToken cancellationToken)
    {
        var requesterPresent = IsPresent(session, requester.ZaloUserId!);
        var partnerPresent = IsPresent(session, partner.ZaloUserId!);
        if (!requesterPresent || !partnerPresent)
        {
            var missing = new List<string>();
            if (!requesterPresent) missing.Add(requester.DisplayName ?? "bạn");
            if (!partnerPresent) missing.Add(partner.DisplayName ?? "người chơi kia");
            return new(
                $"Tui xếp được preference chung team, nhưng {string.Join(" và ", missing)} chưa có trong roster {session.Name}. Registration vẫn lấy từ poll/DB nên tui chưa coi câu chat này là đăng ký.",
                "TeamPreferenceAdvisor",
                session.Id);
        }

        await SaveProposalAsync(store, groupId, senderId, messageId, requester, partner, session, [session], ttlMinutes, cancellationToken);
        return new(
            $"Được. Tui hiểu là {requester.DisplayName} + {partner.DisplayName} muốn chung team ở {session.Name}. Cả hai đều đang có trong roster. Tui có thể giữ yêu cầu này làm preference khi xếp team; để áp dụng thay đổi thật, hãy reply bot hoặc @bot xác nhận rõ.",
            "TeamPreferenceAdvisor",
            session.Id,
            true);
    }

    private async Task SaveProposalAsync(
        ZaloConversationStateV2Store store,
        string groupId,
        string senderId,
        string messageId,
        ZaloIdentityResolution requester,
        ZaloIdentityResolution partner,
        MatchSession? session,
        IReadOnlyList<MatchSession> candidates,
        int ttlMinutes,
        CancellationToken cancellationToken)
    {
        var data = new TeamPreferenceProposalData(
            requester.ZaloUserId!, requester.DisplayName ?? "Bạn",
            partner.ZaloUserId!, partner.DisplayName ?? "Người chơi",
            session?.Id, session?.Name);
        var missing = session is null ? new[] { "sessionReference" } : Array.Empty<string>();
        var candidateJson = JsonSerializer.Serialize(candidates.Take(10).Select(item => new { type = "session", id = item.Id, name = item.Name }));
        await store.SaveActiveAsync(
            groupId,
            senderId,
            ProposalIntent,
            JsonSerializer.Serialize(data),
            JsonSerializer.Serialize(missing),
            candidateJson,
            messageId,
            messageId,
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(ttlMinutes, 2, 15)),
            cancellationToken);
    }

    private async Task<List<MatchSession>> LoadSessionsAsync(string accountId, string groupId, CancellationToken cancellationToken)
    {
        var rows = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Include(item => item.Players)
                .ThenInclude(player => player.PlayerProfile)
            .Where(item => item.BotEnabled &&
                           item.ZaloGroupId == groupId &&
                           item.ZaloConnection != null &&
                           item.ZaloConnection.AccountZaloId == accountId &&
                           item.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-4);
        return rows
            .Where(item => item.Status != SessionStatus.Finished && (item.StartTime is null || item.StartTime >= cutoff))
            .OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(30)
            .ToList();
    }

    private static MatchSession? ResolveSingleSession(string reference, IReadOnlyList<MatchSession> sessions)
    {
        if (sessions.Count == 1 && string.IsNullOrWhiteSpace(reference)) return sessions[0];
        var refs = sessions.Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime)).ToList();
        var operationalIds = ZaloBotIntelligence.SelectOperationalSessionCandidateIds(reference, refs).ToHashSet(StringComparer.Ordinal);
        var operational = refs.Where(item => operationalIds.Contains(item.Id)).ToList();
        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(reference, operational);
        var matched = sessions.Where(item => matchedIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
        return matched.Count == 1 ? matched[0] : null;
    }

    private static bool BothPresent(MatchSession session, string firstUid, string secondUid) =>
        IsPresent(session, firstUid) && IsPresent(session, secondUid);

    private static bool IsPresent(MatchSession session, string uid) => session.Players.Any(player =>
        player.IsPresent && player.PlayerProfile != null && string.Equals(player.PlayerProfile.ZaloUserId, uid, StringComparison.Ordinal));

    internal static bool TryExtractTeamPreference(
        string text,
        out string requester,
        out string partner,
        out string? sessionReference)
    {
        requester = string.Empty;
        partner = string.Empty;
        sessionReference = null;
        if (!ZaloNaturalCommandParser.TryParseTeamPreference(text, out var parsed) || parsed.PlayerReferences.Count < 2)
            return false;

        requester = CleanPartner(parsed.PlayerReferences[0]);
        partner = CleanPartner(parsed.PlayerReferences[1]);
        sessionReference = parsed.SessionReference;
        if (IsSelf(requester) && partner.Length > 0) return true;

        // Common conversational form: "tui muốn chơi chung với To An thì bạn xếp được ko".
        var match = Regex.Match(
            text,
            @"^(?<self>tui|toi|mình|minh|em|anh|chị|chi)\s+(?:muốn|muon|xin)?\s*(?:chơi|choi|đánh|danh)?\s*(?:chung|cùng|cung)(?:\s+(?:team|đội|doi))?\s+(?:với|voi)\s+(?<partner>.+?)(?=\s+(?:thì|thi)\s+(?:bạn|ban|bot|npc)\b|\s+(?:được|duoc)\s*(?:không|khong|ko)\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return requester.Length > 0 && partner.Length > 0;
        requester = match.Groups["self"].Value.Trim();
        partner = CleanPartner(match.Groups["partner"].Value);
        return partner.Length > 0;
    }

    private static string CleanPartner(string value)
    {
        var cleaned = Regex.Replace(
            value.Trim().Trim(',', '.', '?', '!', ':', ';'),
            @"\s+(?:(?:thì|thi)\s+)?(?:bạn|ban|bot|npc)\s+(?:xếp|xep|làm|lam|có|co|được|duoc).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+(?:được|duoc)\s*(?:không|khong|ko|k)\??$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return cleaned;
    }

    private static bool IsSelf(string value) => ZaloBotIntelligence.Normalize(value) is "tui" or "toi" or "minh" or "em" or "anh" or "chi";
    private static string Clean(string? value, int max) { var text = (value ?? string.Empty).Trim(); return text.Length <= max ? text : text[..max]; }

    private sealed record TeamPreferenceProposalData(
        string RequesterZaloUserId,
        string RequesterName,
        string PartnerZaloUserId,
        string PartnerName,
        string? SessionId,
        string? SessionName);
}
