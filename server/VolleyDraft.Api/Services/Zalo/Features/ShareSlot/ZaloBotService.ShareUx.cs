using System.Text.RegularExpressions;
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

    /// <summary>
    /// Share-slot is a mutation-shaped flow, so stale/started sessions must never win
    /// identity ranking. When the parsed command carries an explicit session selector
    /// (today/tomorrow, weekday, calendar date or a concrete session name), scope to
    /// that selector before comparing roster identities. This keeps a strong match in
    /// an old session from overriding the user's requested future match.
    /// </summary>
    internal static IReadOnlyList<string> ScopeShareSessionCandidateIds(
        IReadOnlyList<ZaloSessionReference> candidates,
        string? sessionReference,
        DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var actionable = candidates
            .Where(candidate => candidate.StartTime is null || candidate.StartTime > current)
            .ToList();
        if (string.IsNullOrWhiteSpace(sessionReference))
            return actionable.Select(candidate => candidate.Id).ToList();

        var selector = NormalizeText(sessionReference);
        if (selector.Length == 0)
            return actionable.Select(candidate => candidate.Id).ToList();

        return actionable
            .Where(candidate => ShareSessionReferenceMatches(candidate, selector, current))
            .Select(candidate => candidate.Id)
            .ToList();
    }

    private static bool ShareSessionReferenceMatches(
        ZaloSessionReference session,
        string selector,
        DateTimeOffset now)
    {
        var normalizedName = NormalizeText(session.Name);
        if (normalizedName.Length > 0 && selector.Contains(normalizedName, StringComparison.Ordinal))
            return true;
        if (session.StartTime is null) return false;

        var local = session.StartTime.Value.ToOffset(VietnamOffset);
        var today = now.ToOffset(VietnamOffset).Date;
        if (HasAny(selector, "hom nay", "bua nay") && local.Date == today) return true;
        if (HasAny(selector, "ngay mai", "mai nay") && local.Date == today.AddDays(1)) return true;

        foreach (Match dateMatch in Regex.Matches(
                     selector,
                     @"(?<!\d)(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?(?!\d)"))
        {
            if (!int.TryParse(dateMatch.Groups[1].Value, out var day) ||
                !int.TryParse(dateMatch.Groups[2].Value, out var month) ||
                day != local.Day || month != local.Month)
                continue;
            if (!dateMatch.Groups[3].Success) return true;
            if (!int.TryParse(dateMatch.Groups[3].Value, out var year)) continue;
            if (year < 100) year += 2000;
            if (year == local.Year) return true;
        }

        var dayTokens = local.DayOfWeek switch
        {
            DayOfWeek.Monday => new[] { "t2", "thu 2", "thu hai" },
            DayOfWeek.Tuesday => new[] { "t3", "thu 3", "thu ba" },
            DayOfWeek.Wednesday => new[] { "t4", "thu 4", "thu tu" },
            DayOfWeek.Thursday => new[] { "t5", "thu 5", "thu nam" },
            DayOfWeek.Friday => new[] { "t6", "thu 6", "thu sau" },
            DayOfWeek.Saturday => new[] { "t7", "thu 7", "thu bay" },
            _ => new[] { "cn", "chu nhat" }
        };
        return dayTokens.Any(token => ContainsToken(selector, token));
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
        var scopedIds = ScopeShareSessionCandidateIds(
                candidates.Select(session => new ZaloSessionReference(session.Id, session.Name, session.StartTime)).ToList(),
                command.SessionReference)
            .ToHashSet(StringComparer.Ordinal);
        var scopedCandidates = candidates
            .Where(session => scopedIds.Contains(session.Id))
            .ToList();

        var scored = scopedCandidates.Select(session =>
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
