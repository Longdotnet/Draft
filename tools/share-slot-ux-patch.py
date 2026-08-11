from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one match, found {count}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


parser = "server/VolleyDraft.Api/Services/ZaloNaturalCommandParser.cs"
replace_once(
    parser,
    '''        if (!match.Success) return false;

        var partnerValue = RemoveTrailingSessionReference(match.Groups["partners"].Value, out var sessionReference);
''',
    '''        if (!match.Success)
        {
            match = Regex.Match(
                value,
                @"^(?<anchor>.+?)\\s+(?:với|voi|và|va)\\s+(?<partners>.+?)\\s+(?:(?:muốn|muon|xin)\\s+)?(?:share|chung|đánh\\s+chung|danh\\s+chung|chơi\\s+chung|choi\\s+chung)\\s*(?:một\\s+|mot\\s+)?slot(?:\\s+.*)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success)
        {
            match = Regex.Match(
                value,
                @"^(?<anchor>.+?)\\s+(?:muốn\\s+|muon\\s+|xin\\s+)?share\\s+(?:slot\\s+)?(?:(?:với|voi)\\s+)?(?<partners>@.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success) return false;

        var partnerValue = RemoveTrailingSessionReference(match.Groups["partners"].Value, out var sessionReference);
''',
)

replace_once(
    parser,
    '''            var basis = IsCompleteShareSlotCommand(deterministicCommand)
                ? deterministicCommand!
                : currentCommand;
            if (!IsCompleteShareSlotCommand(basis)) return currentCommand;

            var mention = mentionedUsers[0];
            var mentionName = mention.DisplayName.Trim().TrimStart('@');
            if (mentionName.Length == 0) return currentCommand;
''',
    '''            var basis = IsCompleteShareSlotCommand(deterministicCommand)
                ? deterministicCommand!
                : currentCommand;
            var mention = mentionedUsers[0];
            var mentionName = mention.DisplayName.Trim().TrimStart('@');
            if (mentionName.Length == 0) return currentCommand;
            if (!IsCompleteShareSlotCommand(basis))
            {
                return new ZaloShareSlotCommand(
                    "tui",
                    [mentionName],
                    1,
                    deterministicCommand?.SessionReference ?? currentCommand?.SessionReference,
                    PartnerZaloUserIds: [mention.ZaloUserId]);
            }
''',
)

intelligence = "server/VolleyDraft.Api/Services/ZaloBotIntelligence.cs"
replace_once(
    intelligence,
    '''            "khong danh chung slot", "khong choi chung slot", "khong thay phien",
            "huy share", "bo share", "tach share", "tach slot",
            "share nua", "chung slot nua", "thay phien nua");
''',
    '''            "khong danh chung slot", "khong choi chung slot", "khong thay phien",
            "huy share", "bo share", "tach share", "tach slot");
''',
)

service = "server/VolleyDraft.Api/Services/ZaloBotService.cs"
replace_once(
    service,
    '''        if (command is null)
            return new BotAnswer("Mình chưa nhận ra người chính và người chơi chung. Ví dụ: @bot Nick Tran muốn share slot với An; hoặc @bot Nick Tran xin +2 cho An và Bình.", null, decision.Intent, aiCalled);
''',
    '''        if (command is null)
            return new BotAnswer("Bạn chỉ cần nhắn kiểu: @Npc tui share slot với @Tên. Nếu có nhiều trận mình sẽ hỏi lại ngày; không cần gõ đúng một mẫu lệnh cố định.", null, decision.Intent, aiCalled);
''',
)

replace_once(
    service,
    '''        var matchingSessions = sessions
            .Where(session => (anchorZaloUserId.Length > 0 &&
                               session.PlayerNamesByZaloUserId.ContainsKey(anchorZaloUserId)) ||
                              ResolvePlayerReference(rawAnchor, session.PlayerNames) is not null ||
                              (requestedOwnSlot && session.SenderIsListed))
            .ToList();
        var operationalCandidateIds = ZaloBotIntelligence.SelectOperationalSessionCandidateIds(
            selector,
            sessions.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList());
        var operationalSessions = sessions
            .Where(session => operationalCandidateIds.Contains(session.Id, StringComparer.Ordinal))
            .ToList();
        var relevantMatchingSessions = matchingSessions
            .Where(session => operationalCandidateIds.Contains(session.Id, StringComparer.Ordinal))
            .ToList();
        var selected = relevantMatchingSessions.Count == 1
            ? new SessionSelection(relevantMatchingSessions[0], null)
            : relevantMatchingSessions.Count > 1
                ? SelectSession(relevantMatchingSessions, selector)
                : SelectSession(sessions, selector);
''',
    '''        var operationalCandidateIds = ZaloBotIntelligence.SelectOperationalSessionCandidateIds(
            selector,
            sessions.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList());
        var operationalSessions = sessions
            .Where(session => operationalCandidateIds.Contains(session.Id, StringComparer.Ordinal))
            .ToList();
        var relevantMatchingSessions = RankShareSessionCandidates(
            operationalSessions,
            rawAnchor,
            anchorZaloUserId,
            requestedOwnSlot,
            partners,
            command,
            mentionedUsers);
        var selected = relevantMatchingSessions.Count == 1
            ? new SessionSelection(relevantMatchingSessions[0], null)
            : relevantMatchingSessions.Count > 1
                ? SelectSession(relevantMatchingSessions, selector)
                : SelectSession(sessions, selector);
''',
)

replace_once(
    service,
    '''            return new BotAnswer(
                selected.Clarification + " Bạn chỉ cần trả lời ngày hoặc tên trận; bot vẫn nhớ yêu cầu share slot này.",
                null,
                decision.Intent,
                aiCalled);
''',
    '''            return new BotAnswer(
                FormatShareSessionClarification(incoming.SenderName, partners, selectionCandidates, selected.Clarification),
                null,
                decision.Intent,
                aiCalled);
''',
)

replace_once(
    service,
    '''        var senderIsCurrentPollVoter = requestedOwnSlot &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (requestedOwnSlot && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                decision.Intent,
                aiCalled);
            if (operatorDenial is not null)
            {
                var message = session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection
                    ? string.IsNullOrWhiteSpace(session.LatestPoll)
                        ? $"Mình chưa có poll/option đã liên kết để xác minh vote hiện tại của {session.Name}. Thành viên thường chỉ tự share slot khi chính UID của mình đang vote option của trận này."
                        : $"Mình đã đồng bộ vote hiện tại của {session.Name} nhưng UID của bạn không nằm trong option đang liên kết. Thành viên thường chỉ tự share slot khi chính mình đang vote option của trận này. Hãy vote đúng option rồi thử lại."
                    : $"{session.Name} đã bắt đầu draft hoặc draft xong nên mình không thể xác minh vote hiện tại an toàn cho self-service. Hãy nhờ trưởng nhóm, phó nhóm hoặc operator thực hiện.";
                return new BotAnswer(
                    message,
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
''',
    '''        var isPostDraft = session.Status == SessionStatus.Finished;
        var senderIsCurrentPollVoter = requestedOwnSlot && !isPostDraft &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (requestedOwnSlot && !isPostDraft && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                decision.Intent,
                aiCalled);
            if (operatorDenial is not null)
            {
                var message = session.Status == SessionStatus.Drafting
                    ? $"{session.Name} đang trong lúc draft. Chờ draft xong rồi gửi lại yêu cầu share slot; lúc đó mình sẽ kiểm tra slot của chính bạn."
                    : string.IsNullOrWhiteSpace(session.LatestPoll)
                        ? $"Mình chưa có poll đã liên kết để kiểm tra bạn có tham gia {session.Name}. Nhờ admin liên kết/import đúng poll rồi thử lại."
                        : $"Mình đã đồng bộ poll nhưng chưa thấy bạn trong lượt vote của {session.Name}. Bạn vote đúng option rồi thử lại nhé.";
                return new BotAnswer(
                    message,
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
''',
)

replace_once(
    service,
    '''        var selfService = senderIsCurrentPollVoter &&
                          session.SenderIsListed &&
                          !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                          resolvedAnchor is not null &&
                          NormalizeText(resolvedAnchor) == NormalizeText(session.SenderPlayerName);
''',
    '''        var selfService = IsShareSelfServiceAllowed(
            session.Status,
            senderIsCurrentPollVoter,
            session.SenderIsListed,
            session.SenderPlayerName,
            resolvedAnchor);
''',
)

replace_once(
    service,
    '''        var senderIsCurrentPollVoter = plan.SelfService &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (plan.SelfService && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                ZaloBotIntent.ShareSlotConfirm,
                aiCalled);
            if (operatorDenial is not null)
            {
                return new BotAnswer(
                    $"Bạn không còn nằm trong vote hiện tại của option đang liên kết cho {session.Name}, nên mình không áp dụng share slot self-service. Hãy vote lại đúng option rồi gửi lại yêu cầu; trưởng/phó nhóm hoặc operator vẫn có thể xử lý thay.",
                    null,
                    ZaloBotIntent.ShareSlotConfirm,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
        var selfStillValid = plan.SelfService &&
                             senderIsCurrentPollVoter &&
                             session.SenderIsListed &&
                             !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                             NormalizeText(plan.AnchorPlayerName) == NormalizeText(session.SenderPlayerName);
''',
    '''        if (plan.IsPostDraft && session.Status != SessionStatus.Finished)
            return new BotAnswer("Trạng thái buổi đã thay đổi sau lúc xem trước. Hãy gửi lại yêu cầu share slot để mình kiểm tra lại.", null, ZaloBotIntent.ShareSlotConfirm, aiCalled);

        var senderIsCurrentPollVoter = plan.SelfService && !plan.IsPostDraft &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (plan.SelfService && !plan.IsPostDraft && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                ZaloBotIntent.ShareSlotConfirm,
                aiCalled);
            if (operatorDenial is not null)
            {
                return new BotAnswer(
                    $"Mình vừa kiểm tra lại và không còn thấy bạn trong lượt vote của {session.Name}. Bạn vote đúng option rồi gửi lại yêu cầu nhé.",
                    null,
                    ZaloBotIntent.ShareSlotConfirm,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
        var selfStillValid = plan.SelfService && IsShareSelfServiceAllowed(
            session.Status,
            senderIsCurrentPollVoter,
            session.SenderIsListed,
            session.SenderPlayerName,
            plan.AnchorPlayerName);
''',
)

Path("server/VolleyDraft.Api/Services/ZaloBotService.ShareUx.cs").write_text(
    '''using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    internal static bool IsShareSelfServiceAllowed(
        SessionStatus status,
        bool senderIsCurrentPollVoter,
        bool senderIsListed,
        string? senderPlayerName,
        string? resolvedAnchor)
    {
        var statusAllowsSelfService = status == SessionStatus.Finished ||
                                      (status is SessionStatus.Setup or SessionStatus.CaptainSelection && senderIsCurrentPollVoter);
        return statusAllowsSelfService &&
               senderIsListed &&
               !string.IsNullOrWhiteSpace(senderPlayerName) &&
               !string.IsNullOrWhiteSpace(resolvedAnchor) &&
               NormalizeText(senderPlayerName) == NormalizeText(resolvedAnchor);
    }

    private static List<SessionSnapshot> RankShareSessionCandidates(
        IReadOnlyList<SessionSnapshot> candidates,
        string rawAnchor,
        string anchorZaloUserId,
        bool requestedOwnSlot,
        IReadOnlyList<string> partners,
        ZaloShareSlotCommand command,
        IReadOnlyList<ZaloMentionedUser> mentionedUsers)
    {
        var scored = candidates.Select(session =>
        {
            var anchorMatches = (anchorZaloUserId.Length > 0 &&
                                 session.PlayerNamesByZaloUserId.ContainsKey(anchorZaloUserId)) ||
                                ResolvePlayerReference(rawAnchor, session.PlayerNames) is not null ||
                                (requestedOwnSlot && session.SenderIsListed);
            if (!anchorMatches) return (Session: session, Score: 0);

            var score = requestedOwnSlot && session.SenderIsListed ? 120 : 100;
            for (var index = 0; index < partners.Count; index += 1)
            {
                var commandPartnerId = command.PartnerZaloUserIds is { Count: > 0 } && index < command.PartnerZaloUserIds.Count
                    ? command.PartnerZaloUserIds[index]
                    : null;
                var mention = FindMentionedUser(partners[index], mentionedUsers);
                var partnerId = NormalizeId(commandPartnerId ?? mention?.ZaloUserId ?? string.Empty);
                var partnerMatches = (partnerId.Length > 0 && session.PlayerNamesByZaloUserId.ContainsKey(partnerId)) ||
                                     ResolvePlayerReference(partners[index], session.PlayerNames) is not null;
                if (partnerMatches) score += 60;
            }
            return (Session: session, Score: score);
        }).Where(item => item.Score > 0).ToList();

        if (scored.Count == 0) return [];
        var bestScore = scored.Max(item => item.Score);
        return scored.Where(item => item.Score == bestScore).Select(item => item.Session).ToList();
    }

    private static string FormatShareSessionClarification(
        string senderName,
        IReadOnlyList<string> partners,
        IReadOnlyList<SessionSnapshot> candidates,
        string fallback)
    {
        if (candidates.Count == 0)
            return fallback + " Trả lời ngày hoặc tên trận là được; mình vẫn nhớ yêu cầu share slot này.";

        var partnerText = string.Join(" và ", partners);
        var options = string.Join(" hay ", candidates.Take(4).Select(session => session.Name));
        return $"{senderName} muốn share slot với {partnerText} ở trận nào: {options}? Trả lời ngày hoặc tên trận là được; mình vẫn nhớ yêu cầu này.";
    }
}
''',
    encoding="utf-8",
)

Path("server/VolleyDraft.Api.Tests/ZaloShareNaturalUxTests.cs").write_text(
    '''using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareNaturalUxTests
{
    [Theory]
    [InlineData("tui với @Nguyễn Minh Huy share slot", "tui", "Nguyễn Minh Huy")]
    [InlineData("tui share slot @Nguyễn Minh Huy", "tui", "Nguyễn Minh Huy")]
    [InlineData("tui share @Nguyễn Minh Huy", "tui", "Nguyễn Minh Huy")]
    [InlineData("em với @Nguyễn Minh Huy chung slot", "em", "Nguyễn Minh Huy")]
    public void Share_parser_accepts_natural_self_share_variants(string question, string anchor, string partner)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(question, out var command));
        Assert.Equal(anchor, command.Anchor);
        Assert.Equal([partner], command.Partners);
        Assert.Equal(1, command.RequestedPartnerCount);
        Assert.False(ZaloBotIntelligence.IsUnshareSlotRequest(question));
    }

    [Fact]
    public void One_explicit_partner_mention_defaults_anchor_to_sender_alias()
    {
        var command = ZaloNaturalCommandParser.BindExplicitShareMentions(
            [new ZaloMentionedUser("huy-id", "Nguyễn Minh Huy")],
            null);

        Assert.NotNull(command);
        Assert.Equal("tui", command!.Anchor);
        Assert.Equal(["Nguyễn Minh Huy"], command.Partners);
        Assert.Equal(["huy-id"], command.PartnerZaloUserIds);
    }

    [Theory]
    [InlineData("tui không share slot với Huy nữa")]
    [InlineData("tui huỷ share slot với Huy")]
    [InlineData("tách share slot của tui với Huy")]
    public void Explicit_unshare_language_still_routes_to_unshare(string question)
    {
        Assert.True(ZaloBotIntelligence.IsUnshareSlotRequest(question));
    }

    [Fact]
    public void Finished_session_allows_owner_self_service_without_current_poll_vote()
    {
        Assert.True(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Finished,
            senderIsCurrentPollVoter: false,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }

    [Fact]
    public void Drafting_session_does_not_allow_member_self_service()
    {
        Assert.False(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Drafting,
            senderIsCurrentPollVoter: true,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }

    [Fact]
    public void Predraft_self_service_still_requires_current_vote()
    {
        Assert.False(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Setup,
            senderIsCurrentPollVoter: false,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));

        Assert.True(ZaloBotService.IsShareSelfServiceAllowed(
            SessionStatus.Setup,
            senderIsCurrentPollVoter: true,
            senderIsListed: true,
            senderPlayerName: "Vivian",
            resolvedAnchor: "Vivian"));
    }
}
''',
    encoding="utf-8",
)
