using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloMemberAssistSettings(bool Enabled)
{
    public static ZaloMemberAssistSettings FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetValue("ZaloBot:Ambient:MemberAssist:Enabled", true));
}

public enum ZaloMemberAssistKind
{
    None,
    PassSlotHelp
}

public sealed record ZaloMemberAssistReply(
    ZaloMemberAssistKind Kind,
    string Text,
    string? SessionId = null);

/// <summary>
/// High-precision, read-only helper for ordinary group chatter where a member is
/// clearly asking for help even though they did not address the bot. It never writes
/// roster/team/slot state; it only offers the next useful step. Domain mutation keeps
/// using the normal explicit/self-service confirmation path.
/// </summary>
public sealed class ZaloMemberAssistService(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly Regex PassSlotPattern = new(
        @"(?<![a-z0-9])(?:pass|nhuong|tra|bo)\s+(?:slot|suat|cho|si\s+lot|xi\s+lot)(?![a-z0-9])|(?<![a-z0-9])pass\s+(?:cai\s+)?(?:ve|keo)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitDatePattern = new(
        @"(?<!\d)(?<day>\d{1,2})[/-](?<month>\d{1,2})(?:[/-](?<year>\d{2,4}))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsPassSlotHelpOpportunity(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized.Length > 0 && PassSlotPattern.IsMatch(normalized);
    }

    public async Task<ZaloMemberAssistReply?> TryBuildAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!IsPassSlotHelpOpportunity(incoming.Content)) return null;

        // If the message explicitly points at another human, do not guess that the
        // sender is offering their own slot. Delegated actions stay explicit.
        if (incoming.Mentions.Any(mention =>
                CleanId(mention.Uid).Length > 0 &&
                !string.Equals(CleanId(mention.Uid), CleanId(incoming.BotId), StringComparison.Ordinal)))
            return null;

        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        var senderId = CleanId(incoming.SenderId);
        var senderName = (incoming.SenderName ?? string.Empty).Trim();
        if (connectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return null;

        var cutoff = DateTimeOffset.UtcNow.AddHours(-4);
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.Players)
                .ThenInclude(player => player.PlayerProfile)
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled &&
                              (session.StartTime == null || session.StartTime >= cutoff))
            .ToListAsync(cancellationToken);

        var owned = sessions
            .Where(session => session.Players.Any(player =>
                player.IsPresent &&
                ((player.PlayerProfile != null && CleanId(player.PlayerProfile.ZaloUserId) == senderId) ||
                 (CleanId(player.PlayerProfile?.ZaloUserId).Length == 0 &&
                  SameName(player.DisplayName, senderName)))))
            .OrderBy(session => session.StartTime ?? DateTimeOffset.MaxValue)
            .ToList();
        if (owned.Count == 0) return null;

        var explicitMatches = owned.Where(session => MatchesExplicitSession(incoming.Content, session)).ToList();
        if (explicitMatches.Count == 1)
            return BuildSingle(incoming.SenderName, explicitMatches[0]);
        if (explicitMatches.Count > 1)
            owned = explicitMatches;

        if (owned.Count == 1)
            return BuildSingle(incoming.SenderName, owned[0]);

        var choices = string.Join(" với ", owned.Take(4).Select(session => session.Name));
        var who = FriendlyName(incoming.SenderName);
        return new ZaloMemberAssistReply(
            ZaloMemberAssistKind.PassSlotHelp,
            $"Pass kèo nào á {who} 😆 Tui thấy bạn có slot {choices}; nói T6/CN hoặc tên kèo là tui phụ tiếp nha.");
    }

    private static ZaloMemberAssistReply BuildSingle(string? senderName, MatchSession session)
    {
        var who = FriendlyName(senderName);
        return new ZaloMemberAssistReply(
            ZaloMemberAssistKind.PassSlotHelp,
            $"{who} pass slot {session.Name} hả 🥲 Ai nhận thì nói tên người nhận, tui phụ chuyển slot tiếp nha.",
            session.Id);
    }

    private static bool MatchesExplicitSession(string? content, MatchSession session)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        var normalizedName = ZaloBotIntelligence.Normalize(session.Name);
        if (normalizedName.Length > 0 && ContainsPhrase(normalized, normalizedName)) return true;
        if (session.StartTime is null) return false;

        var local = session.StartTime.Value.ToOffset(VietnamOffset);
        foreach (Match match in ExplicitDatePattern.Matches(normalized))
        {
            if (!int.TryParse(match.Groups["day"].Value, out var day) ||
                !int.TryParse(match.Groups["month"].Value, out var month) ||
                day != local.Day || month != local.Month)
                continue;
            if (!match.Groups["year"].Success) return true;
            if (!int.TryParse(match.Groups["year"].Value, out var year)) continue;
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
        return dayTokens.Any(token => ContainsPhrase(normalized, token));
    }

    private static bool ContainsPhrase(string value, string phrase) =>
        Regex.IsMatch(value, $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])", RegexOptions.CultureInvariant);

    private static string FriendlyName(string? value)
    {
        var parts = (value ?? string.Empty)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "bạn" : parts[^1];
    }

    private static bool SameName(string? left, string? right) =>
        NormalizeName(left) == NormalizeName(right) && NormalizeName(left).Length > 0;

    private static string NormalizeName(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch == 'đ' ? 'd' : ch);
        }
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
