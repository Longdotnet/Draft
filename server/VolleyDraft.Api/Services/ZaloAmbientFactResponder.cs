using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientFactReply(
    ZaloBotIntent Intent,
    string Text,
    string? SessionId = null);

/// <summary>
/// Builds ambient answers only from current application data. This responder is
/// intentionally isolated from ZaloBotService.BuildAnswerAsync so ambient chat can
/// never consume legacy pending confirmations or enter mutation handlers.
/// </summary>
public sealed class ZaloAmbientFactResponder(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly HashSet<ZaloBotIntent> AllowedIntents = new()
    {
        ZaloBotIntent.SessionSchedule,
        ZaloBotIntent.LocationParking,
        ZaloBotIntent.MissingSlots,
        ZaloBotIntent.UpcomingSessions,
        ZaloBotIntent.Roster,
        ZaloBotIntent.Help,
        ZaloBotIntent.TeamPreference
    };

    public static bool IsAllowedIntent(ZaloBotIntent intent) => AllowedIntents.Contains(intent);

    public async Task<ZaloAmbientFactReply?> TryBuildAsync(
        string accountId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        int minimumScore,
        CancellationToken cancellationToken = default)
    {
        if (!decision.WouldReply ||
            decision.Kind != ZaloAmbientParticipationKind.Fact ||
            decision.Score < Math.Clamp(minimumScore, 65, 100) ||
            !Enum.TryParse<ZaloBotIntent>(decision.Intent, out var intent) ||
            !AllowedIntents.Contains(intent))
            return null;

        accountId = NormalizeId(accountId);
        groupId = NormalizeId(groupId);
        if (accountId.Length == 0 || groupId.Length == 0) return null;

        if (intent is ZaloBotIntent.Help or ZaloBotIntent.TeamPreference)
        {
            // Participation policy already proved this ambient turn is aimed at the bot.
            // Preserve that addressing fact while the advisor re-classifies the speech act,
            // including chat shorthand such as "đc/ko" that should not depend on a second
            // fragile address heuristic.
            var advisorIncoming = incoming with { MentionedBot = true };
            var advisor = await new ZaloConversationalAdvisor(db).TryBuildAsync(
                accountId,
                groupId,
                advisorIncoming,
                proposalTtlMinutes: 5,
                cancellationToken);
            return advisor is null
                ? null
                : new ZaloAmbientFactReply(intent, advisor.Text, advisor.SessionId);
        }

        // Keep stable scalar predicates in SQL, then evaluate/order DateTimeOffset in
        // memory so SQLite and PostgreSQL follow the same semantics. Group session
        // cardinality is bounded by the bot-enabled group scope before taking 30.
        var sessionRows = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Include(item => item.Players)
            .Where(item => item.BotEnabled &&
                           item.ZaloGroupId == groupId &&
                           item.ZaloConnection != null &&
                           item.ZaloConnection.AccountZaloId == accountId &&
                           item.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var sessions = sessionRows
            .OrderBy(item => item.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(30)
            .ToList();
        if (sessions.Count == 0) return null;

        if (intent == ZaloBotIntent.UpcomingSessions)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-4);
            var upcoming = sessions
                .Where(item => item.Status != SessionStatus.Finished &&
                               (item.StartTime is null || item.StartTime >= cutoff))
                .Take(5)
                .ToList();
            if (upcoming.Count == 0) return null;
            var lines = upcoming.Select(item =>
            {
                var count = item.Players.Count(player => player.IsPresent);
                var schedule = item.StartTime is null ? "chưa chốt giờ" : FormatVietnamTime(item.StartTime.Value);
                var location = string.IsNullOrWhiteSpace(item.Location) ? "chưa chốt sân" : item.Location.Trim();
                return $"- {item.Name}: {schedule}, {location}, {count}/{Capacity(item)} slot";
            });
            return new ZaloAmbientFactReply(
                intent,
                "Các kèo sắp tới:\n" + string.Join("\n", lines));
        }

        var session = ResolveSingleSession(incoming.Content, sessions);
        if (session is null) return null;

        var playerCount = session.Players.Count(player => player.IsPresent);
        var capacity = Capacity(session);
        return intent switch
        {
            ZaloBotIntent.SessionSchedule => new ZaloAmbientFactReply(
                intent,
                session.StartTime is null
                    ? $"{session.Name} chưa chốt giờ."
                    : $"{session.Name} diễn ra lúc {FormatVietnamTime(session.StartTime.Value)}{LocationSuffix(session)}.",
                session.Id),

            ZaloBotIntent.LocationParking => new ZaloAmbientFactReply(
                intent,
                BuildLocationAnswer(session),
                session.Id),

            ZaloBotIntent.MissingSlots => new ZaloAmbientFactReply(
                intent,
                playerCount >= capacity
                    ? $"{session.Name} đã đủ {capacity} slot."
                    : $"{session.Name} đang có {playerCount}/{capacity}, còn thiếu {capacity - playerCount} slot.",
                session.Id),

            ZaloBotIntent.Roster => new ZaloAmbientFactReply(
                intent,
                BuildRosterAnswer(session, playerCount, capacity),
                session.Id),

            _ => null
        };
    }

    private static MatchSession? ResolveSingleSession(string question, IReadOnlyList<MatchSession> sessions)
    {
        var references = sessions
            .Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime))
            .ToList();
        var operationalIds = ZaloBotIntelligence
            .SelectOperationalSessionCandidateIds(question, references)
            .ToHashSet(StringComparer.Ordinal);
        var operational = sessions.Where(item => operationalIds.Contains(item.Id)).ToList();
        if (operational.Count == 0) return null;

        var matchedIds = ZaloBotIntelligence.ResolveSessionReference(
            question,
            operational.Select(item => new ZaloSessionReference(item.Id, item.Name, item.StartTime)).ToList());
        var matched = operational.Where(item => matchedIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
        if (matched.Count == 1) return matched[0];
        return matched.Count == 0 && operational.Count == 1 ? operational[0] : null;
    }

    private static string BuildLocationAnswer(MatchSession session)
    {
        var parts = new List<string> { session.Name };
        parts.Add(string.IsNullOrWhiteSpace(session.Location)
            ? "địa điểm: chưa chốt"
            : $"địa điểm: {session.Location.Trim()}");
        if (!string.IsNullOrWhiteSpace(session.ParkingInstructions))
            parts.Add($"gửi xe: {session.ParkingInstructions.Trim()}");
        return string.Join(" — ", parts) + ".";
    }

    private static string BuildRosterAnswer(MatchSession session, int playerCount, int capacity)
    {
        var names = session.Players
            .Where(player => player.IsPresent)
            .OrderBy(player => player.DisplayName)
            .Select(player => player.DisplayName.Trim())
            .Where(name => name.Length > 0)
            .Take(30)
            .ToList();
        if (names.Count == 0) return $"{session.Name} hiện chưa có ai trong danh sách.";
        return $"Danh sách {session.Name} ({playerCount}/{capacity}):\n" +
               string.Join("\n", names.Select((name, index) => $"{index + 1}. {name}"));
    }

    private static int Capacity(MatchSession session) => Math.Max(0, session.TeamCount * session.TeamSize);

    private static string FormatVietnamTime(DateTimeOffset value) =>
        value.ToOffset(VietnamOffset).ToString("dd/MM HH:mm");

    private static string LocationSuffix(MatchSession session) =>
        string.IsNullOrWhiteSpace(session.Location) ? string.Empty : $", tại {session.Location.Trim()}";

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();
}
