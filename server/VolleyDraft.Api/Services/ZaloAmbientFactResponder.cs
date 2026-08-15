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
        ZaloBotIntent.SelfMembership,
        ZaloBotIntent.LocationParking,
        ZaloBotIntent.MissingSlots,
        ZaloBotIntent.UpcomingSessions,
        ZaloBotIntent.Roster,
        ZaloBotIntent.WeeklySessionCount,
        ZaloBotIntent.ReminderStatus,
        ZaloBotIntent.WaitlistStatus,
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
            decision.Score < Math.Clamp(minimumScore, 60, 100) ||
            !Enum.TryParse<ZaloBotIntent>(decision.Intent, out var intent) ||
            !AllowedIntents.Contains(intent))
            return null;

        accountId = NormalizeId(accountId);
        groupId = NormalizeId(groupId);
        if (accountId.Length == 0 || groupId.Length == 0) return null;

        if (intent == ZaloBotIntent.Help && ZaloAmbientWakePhrase.IsMatch(incoming.Content))
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.Help,
                ZaloAmbientWakePhrase.BuildReply(incoming.SenderName));
        }

        if (intent is ZaloBotIntent.Help or ZaloBotIntent.TeamPreference)
        {
            var advisorIncoming = incoming with { MentionedBot = true };
            var advisor = await new ZaloConversationalAdvisor(db).TryBuildAsync(
                accountId,
                groupId,
                advisorIncoming,
                proposalTtlMinutes: 5,
                cancellationToken);
            if (advisor is not null)
                return new ZaloAmbientFactReply(intent, advisor.Text, advisor.SessionId);

            if (intent == ZaloBotIntent.Help)
            {
                var name = string.IsNullOrWhiteSpace(incoming.SenderName)
                    ? "Bạn"
                    : incoming.SenderName.Trim();
                return new ZaloAmbientFactReply(
                    ZaloBotIntent.Help,
                    $"{name} ơi, được gì á 😄? Nói rõ giúp tui nha — xếp team, coi slot, lịch/sân hay muốn chơi chung với ai?");
            }

            return null;
        }

        var sessionRows = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Include(item => item.Players)
                .ThenInclude(player => player.PlayerProfile)
            .Include(item => item.WaitlistEntries)
            .Include(item => item.ReminderSchedules)
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

        if (intent == ZaloBotIntent.WeeklySessionCount)
            return BuildWeeklySessionCountAnswer(sessions);

        if (intent == ZaloBotIntent.SelfMembership)
            return BuildSelfMembershipAnswer(incoming, sessions);

        if (intent == ZaloBotIntent.ReminderStatus)
            return BuildReminderStatusAnswer(incoming, sessions);

        var session = ResolveSingleSession(incoming.Content, sessions);
        if (session is null) return null;

        if (intent == ZaloBotIntent.WaitlistStatus)
            return BuildWaitlistStatusAnswer(incoming, session);

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

    private static ZaloAmbientFactReply BuildWeeklySessionCountAnswer(IReadOnlyList<MatchSession> sessions)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var nextMonday = monday.AddDays(7);
        var thisWeek = sessions
            .Where(session => session.StartTime is not null)
            .Where(session =>
            {
                var local = session.StartTime!.Value.ToOffset(VietnamOffset);
                return local.Date >= monday && local.Date < nextMonday;
            })
            .OrderBy(session => session.StartTime)
            .ToList();

        if (thisWeek.Count == 0)
            return new ZaloAmbientFactReply(
                ZaloBotIntent.WeeklySessionCount,
                "Tuần này nhóm chưa có kèo nào được cấu hình.");

        var lines = thisWeek.Select(session => $"- {session.Name}: {FormatVietnamTime(session.StartTime!.Value)}");
        return new ZaloAmbientFactReply(
            ZaloBotIntent.WeeklySessionCount,
            $"Tuần này nhóm có {thisWeek.Count} kèo:\n{string.Join("\n", lines)}");
    }

    private static ZaloAmbientFactReply? BuildSelfMembershipAnswer(
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<MatchSession> sessions)
    {
        var senderId = NormalizeId(incoming.SenderId);
        if (senderId.Length == 0) return null;

        var selected = ResolveSingleSession(incoming.Content, sessions);
        if (selected is not null)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.SelfMembership,
                IsSenderPresent(selected, senderId)
                    ? $"Bạn đang có tên trong {selected.Name}."
                    : $"Tui chưa thấy bạn trong danh sách {selected.Name}.",
                selected.Id);
        }

        var cutoff = DateTimeOffset.UtcNow.AddHours(-4);
        var upcoming = sessions
            .Where(session => session.Status != SessionStatus.Finished &&
                              (session.StartTime is null || session.StartTime >= cutoff))
            .Take(4)
            .ToList();
        if (upcoming.Count == 0) return null;

        var lines = upcoming.Select(session =>
            $"- {session.Name}: {(IsSenderPresent(session, senderId) ? "đã có tên" : "chưa có tên")}");
        return new ZaloAmbientFactReply(
            ZaloBotIntent.SelfMembership,
            "Trạng thái của bạn ở các kèo sắp tới:\n" + string.Join("\n", lines));
    }

    private static ZaloAmbientFactReply BuildReminderStatusAnswer(
        ZaloIncomingMessageEvent incoming,
        IReadOnlyList<MatchSession> sessions)
    {
        var selected = ResolveExplicitReminderSession(incoming.Content, sessions);
        if (selected is not null)
        {
            var enabled = selected.ReminderSchedules
                .Where(schedule => schedule.Enabled)
                .OrderBy(schedule => schedule.NextRunAt)
                .ToList();
            if (enabled.Count == 0)
            {
                return new ZaloAmbientFactReply(
                    ZaloBotIntent.ReminderStatus,
                    $"{selected.Name} hiện không có lịch nhắc đang bật.",
                    selected.Id);
            }

            var lines = enabled.Take(8).Select(schedule => FormatReminderSchedule(schedule));
            return new ZaloAmbientFactReply(
                ZaloBotIntent.ReminderStatus,
                $"Lịch nhắc {selected.Name}:\n{string.Join("\n", lines)}",
                selected.Id);
        }

        var schedules = sessions
            .SelectMany(session => session.ReminderSchedules
                .Where(schedule => schedule.Enabled)
                .Select(schedule => new { Session = session, Schedule = schedule }))
            .OrderBy(item => item.Schedule.NextRunAt)
            .Take(10)
            .ToList();
        if (schedules.Count == 0)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.ReminderStatus,
                "Nhóm hiện không có lịch nhắc nào đang bật.");
        }

        var summary = schedules.Select(item =>
            $"- {item.Session.Name}: {FormatReminderSchedule(item.Schedule, includeBullet: false)}");
        return new ZaloAmbientFactReply(
            ZaloBotIntent.ReminderStatus,
            "Các lịch nhắc đang bật:\n" + string.Join("\n", summary));
    }

    private static MatchSession? ResolveExplicitReminderSession(
        string question,
        IReadOnlyList<MatchSession> sessions)
    {
        static string NormalizeWords(string value)
        {
            var normalized = ZaloBotIntelligence.Normalize(value);
            return new string(normalized
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Aggregate(string.Empty, (current, word) => current.Length == 0 ? word : $"{current} {word}");
        }

        var normalized = NormalizeWords(question);
        if (normalized.Length == 0) return null;
        var padded = $" {normalized} ";

        var explicitByName = sessions.Any(session =>
        {
            var name = NormalizeWords(session.Name);
            return name.Length > 0 && padded.Contains($" {name} ", StringComparison.Ordinal);
        });
        var dayTokens = new[]
        {
            "t2", "t3", "t4", "t5", "t6", "t7", "cn",
            "thu 2", "thu 3", "thu 4", "thu 5", "thu 6", "thu 7", "chu nhat"
        };
        var explicitDay = dayTokens.Any(token => padded.Contains($" {token} ", StringComparison.Ordinal));
        if (!explicitByName && !explicitDay) return null;

        return ResolveSingleSession(question, sessions);
    }

    private static string FormatReminderSchedule(ZaloReminderSchedule schedule, bool includeBullet = true)
    {
        var when = schedule.NextRunAt.ToOffset(VietnamOffset).ToString("dd/MM HH:mm");
        var repeat = schedule.Repeats
            ? schedule.IntervalMinutes is > 0
                ? $", lặp mỗi {schedule.IntervalMinutes} phút"
                : ", có lặp"
            : ", một lần";
        var condition = schedule.OnlyIfMissingSlots ? ", chỉ khi còn thiếu slot" : string.Empty;
        return $"{(includeBullet ? "- " : string.Empty)}{when}{repeat}{condition}";
    }

    private static ZaloAmbientFactReply BuildWaitlistStatusAnswer(
        ZaloIncomingMessageEvent incoming,
        MatchSession session)
    {
        var active = session.WaitlistEntries
            .Where(entry => entry.Status is SessionWaitlistStatus.Waiting or SessionWaitlistStatus.Invited)
            .OrderBy(entry => entry.Status == SessionWaitlistStatus.Invited ? 0 : 1)
            .ThenBy(entry => entry.CreatedAt)
            .ToList();
        if (active.Count == 0)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.WaitlistStatus,
                $"{session.Name} hiện không có ai trong waitlist.",
                session.Id);
        }

        var senderId = NormalizeId(incoming.SenderId);
        var senderEntry = senderId.Length == 0
            ? null
            : active.FirstOrDefault(entry => string.Equals(entry.ZaloUserId, senderId, StringComparison.Ordinal));
        var prefix = senderEntry switch
        {
            { Status: SessionWaitlistStatus.Invited } => "Bạn đang được mời nhận slot. ",
            { Status: SessionWaitlistStatus.Waiting } =>
                $"Bạn đang chờ ở vị trí {active.Where(entry => entry.Status == SessionWaitlistStatus.Waiting).TakeWhile(entry => entry.Id != senderEntry.Id).Count() + 1}. ",
            _ => string.Empty
        };

        var lines = active.Take(12).Select((entry, index) =>
            $"{index + 1}. {entry.DisplayName} — {(entry.Status == SessionWaitlistStatus.Invited ? "đã được mời" : "đang chờ")}");
        return new ZaloAmbientFactReply(
            ZaloBotIntent.WaitlistStatus,
            $"{prefix}Waitlist {session.Name} hiện có {active.Count} người:\n{string.Join("\n", lines)}",
            session.Id);
    }

    private static bool IsSenderPresent(MatchSession session, string senderZaloUserId) =>
        session.Players.Any(player =>
            player.IsPresent &&
            player.PlayerProfile is not null &&
            string.Equals(player.PlayerProfile.ZaloUserId, senderZaloUserId, StringComparison.Ordinal));

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
