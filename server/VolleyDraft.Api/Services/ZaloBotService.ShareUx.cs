using VolleyDraft.Api.Models;

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
