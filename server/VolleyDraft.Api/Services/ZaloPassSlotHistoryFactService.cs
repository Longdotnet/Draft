using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloPassSlotHistoryScope
{
    EventToday,
    SessionToday,
    CurrentOpen,
    SpecificSession
}

internal sealed record ZaloPassSlotHistoryRow(
    string OfferId,
    string OwnerZaloUserId,
    string OwnerDisplayName,
    string SessionId,
    string SessionName,
    string Status,
    string? ClaimantDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Read-only pass-slot facts backed by ZaloOpenSlotOffers. Chat/AI may decide that a
/// user is asking about pass-slot history, but counts, names and statuses always come
/// from durable coordination state rather than re-reading chat text.
/// </summary>
internal sealed class ZaloPassSlotHistoryFactService(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly Regex SummaryQuestionPattern = new(
        @"(?:bao\s+nhieu|may\s+nguoi|co\s+may|co\s+bao\s+nhieu|ai|danh\s+sach|list).{0,45}(?:pass|nhuong)|(?:pass|nhuong).{0,45}(?:bao\s+nhieu|may\s+nguoi|co\s+may|ai|danh\s+sach|list)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrentOpenPattern = new(
        @"(?:slot|suat|keo).{0,35}(?:dang\s+mo|con\s+mo|chua\s+ai\s+nhan|chua\s+co\s+nguoi\s+nhan|can\s+nguoi|ai\s+hot)|(?:con|dang).{0,25}(?:slot|suat).{0,25}(?:pass|mo)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionTodayPattern = new(
        @"(?:keo|tran|buoi|session|san)\s+(?:hom\s+nay|hnay)|(?:hom\s+nay|hnay)\s+(?:co\s+)?(?:keo|tran|buoi|session|san)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TodayPattern = new(
        @"(?<![a-z0-9])(?:hom\s+nay|hnay|today)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionReferencePattern = new(
        @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool LooksLikeQuery(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length == 0) return false;
        return SummaryQuestionPattern.IsMatch(normalized) || CurrentOpenPattern.IsMatch(normalized);
    }

    public async Task<ZaloMemberAssistReply?> TryBuildAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!LooksLikeQuery(incoming.Content)) return null;

        connectionId = Clean(connectionId);
        groupId = Clean(groupId);
        if (connectionId.Length == 0 || groupId.Length == 0) return null;

        var normalized = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
        var scope = ResolveScope(normalized);
        var referenceNow = (now ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);

        var rows = await LoadRowsAsync(connectionId, groupId, cancellationToken);
        if (rows.Count == 0)
            return Reply(scope, "Tui chưa ghi nhận lượt pass slot nào trong dữ liệu của nhóm này nha.");

        var sessionStarts = await LoadSessionStartsAsync(
            connectionId,
            groupId,
            rows.Select(row => row.SessionId).Distinct(StringComparer.Ordinal).ToArray(),
            cancellationToken);

        IEnumerable<ZaloPassSlotHistoryRow> filtered = rows;
        filtered = scope switch
        {
            ZaloPassSlotHistoryScope.CurrentOpen => filtered.Where(row => row.Status == nameof(ZaloOpenSlotOfferStatus.Open)),
            ZaloPassSlotHistoryScope.SessionToday => filtered.Where(row =>
                sessionStarts.TryGetValue(row.SessionId, out var start) &&
                start is not null &&
                start.Value.ToOffset(VietnamOffset).Date == referenceNow.Date),
            ZaloPassSlotHistoryScope.EventToday => filtered.Where(row =>
                row.CreatedAt.ToOffset(VietnamOffset).Date == referenceNow.Date),
            _ => filtered
        };

        if (SessionReferencePattern.IsMatch(normalized))
            filtered = filtered.Where(row => MatchesSessionReference(normalized, row.SessionName, sessionStarts.GetValueOrDefault(row.SessionId)));

        var selected = filtered
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.OwnerDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            return Reply(scope, EmptyText(scope));

        var peopleCount = selected
            .Select(row => Clean(row.OwnerZaloUserId).Length > 0
                ? "uid:" + Clean(row.OwnerZaloUserId)
                : "name:" + NormalizeName(row.OwnerDisplayName))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var offerCount = selected.Select(row => row.OfferId).Distinct(StringComparer.Ordinal).Count();
        var openCount = selected.Count(row => row.Status == nameof(ZaloOpenSlotOfferStatus.Open));
        var claimCount = selected.Count(row => row.Status is nameof(ZaloOpenSlotOfferStatus.ClaimPending) or nameof(ZaloOpenSlotOfferStatus.Applying));
        var completedCount = selected.Count(row => row.Status == nameof(ZaloOpenSlotOfferStatus.Completed));

        var builder = new StringBuilder();
        builder.Append(scope switch
        {
            ZaloPassSlotHistoryScope.CurrentOpen => $"Hiện còn {offerCount} slot đang mở từ {peopleCount} người pass nha:",
            ZaloPassSlotHistoryScope.SessionToday => $"Kèo hôm nay có {peopleCount} người từng báo pass, tổng {offerCount} slot:",
            ZaloPassSlotHistoryScope.SpecificSession => $"Kèo này có {peopleCount} người từng báo pass, tổng {offerCount} slot:",
            _ => $"Hôm nay có {peopleCount} người báo pass, tổng {offerCount} slot:"
        });

        foreach (var row in selected.Take(8))
        {
            builder.Append("\n• ")
                .Append(DisplayName(row.OwnerDisplayName))
                .Append(" — ")
                .Append(row.SessionName)
                .Append(" (")
                .Append(StatusLabel(row));
            builder.Append(')');
        }

        if (selected.Count > 8)
            builder.Append($"\n… còn {selected.Count - 8} slot nữa.");

        if (scope != ZaloPassSlotHistoryScope.CurrentOpen && selected.Count > 1)
        {
            builder.Append($"\nTóm lại: {openCount} đang mở, {claimCount} đang có người giữ/chốt, {completedCount} đã hoàn tất.");
        }

        return Reply(scope, builder.ToString());
    }

    private async Task<IReadOnlyList<ZaloPassSlotHistoryRow>> LoadRowsAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        // ListClaimableAsync guarantees the raw coordination table exists on both
        // SQLite tests and PostgreSQL production before the history SELECT below.
        _ = await new ZaloOpenSlotOfferStore(db)
            .ListClaimableAsync(connectionId, groupId, "__readonly_history__", cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "Status", "ClaimantDisplayName", "CreatedAt", "UpdatedAt"
            FROM "ZaloOpenSlotOffers"
            WHERE "ConnectionId" = @connectionId AND "GroupId" = @groupId
            ORDER BY "UpdatedAt" DESC;
            """;
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);

        var rows = new List<ZaloPassSlotHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ZaloPassSlotHistoryRow(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadString(reader, 4),
                ReadString(reader, 5),
                ReadNullableString(reader, 6),
                ReadDateTimeOffset(reader, 7),
                ReadDateTimeOffset(reader, 8)));
        }
        return rows;
    }

    private async Task<Dictionary<string, DateTimeOffset?>> LoadSessionStartsAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0) return new(StringComparer.Ordinal);
        return await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId == connectionId &&
                              session.ZaloGroupId == groupId &&
                              sessionIds.Contains(session.Id))
            .Select(session => new { session.Id, session.StartTime })
            .ToDictionaryAsync(item => item.Id, item => item.StartTime, StringComparer.Ordinal, cancellationToken);
    }

    private static ZaloPassSlotHistoryScope ResolveScope(string normalized)
    {
        if (CurrentOpenPattern.IsMatch(normalized)) return ZaloPassSlotHistoryScope.CurrentOpen;
        if (SessionTodayPattern.IsMatch(normalized)) return ZaloPassSlotHistoryScope.SessionToday;
        if (TodayPattern.IsMatch(normalized)) return ZaloPassSlotHistoryScope.EventToday;
        if (SessionReferencePattern.IsMatch(normalized)) return ZaloPassSlotHistoryScope.SpecificSession;
        return ZaloPassSlotHistoryScope.CurrentOpen;
    }

    private static bool MatchesSessionReference(string normalized, string sessionName, DateTimeOffset? start)
    {
        var normalizedName = ZaloBotIntelligence.Normalize(sessionName);
        if (normalizedName.Length > 0 && ContainsPhrase(normalized, normalizedName)) return true;
        if (start is null) return false;
        var local = start.Value.ToOffset(VietnamOffset);
        var tokens = local.DayOfWeek switch
        {
            DayOfWeek.Monday => new[] { "t2", "thu 2", "thu hai" },
            DayOfWeek.Tuesday => new[] { "t3", "thu 3", "thu ba" },
            DayOfWeek.Wednesday => new[] { "t4", "thu 4", "thu tu" },
            DayOfWeek.Thursday => new[] { "t5", "thu 5", "thu nam" },
            DayOfWeek.Friday => new[] { "t6", "thu 6", "thu sau" },
            DayOfWeek.Saturday => new[] { "t7", "thu 7", "thu bay" },
            _ => new[] { "cn", "chu nhat" }
        };
        return tokens.Any(token => ContainsPhrase(normalized, token));
    }

    private static string EmptyText(ZaloPassSlotHistoryScope scope) => scope switch
    {
        ZaloPassSlotHistoryScope.CurrentOpen => "Hiện không còn slot pass nào đang mở nha 👌",
        ZaloPassSlotHistoryScope.SessionToday => "Tui chưa thấy ai báo pass ở các kèo diễn ra hôm nay nha.",
        ZaloPassSlotHistoryScope.SpecificSession => "Tui chưa thấy ai báo pass ở kèo đó nha.",
        _ => "Hôm nay tui chưa ghi nhận ai báo pass slot nha."
    };

    private static string StatusLabel(ZaloPassSlotHistoryRow row)
    {
        var claimant = DisplayName(row.ClaimantDisplayName, string.Empty);
        return row.Status switch
        {
            nameof(ZaloOpenSlotOfferStatus.Open) => "đang mở",
            nameof(ZaloOpenSlotOfferStatus.ClaimPending) => claimant.Length > 0 ? $"{claimant} đang giữ" : "đang có người giữ",
            nameof(ZaloOpenSlotOfferStatus.Applying) => claimant.Length > 0 ? $"đang chốt cho {claimant}" : "đang chốt",
            nameof(ZaloOpenSlotOfferStatus.Completed) => claimant.Length > 0 ? $"đã chuyển cho {claimant}" : "đã hoàn tất",
            nameof(ZaloOpenSlotOfferStatus.Cancelled) => "đã huỷ pass",
            nameof(ZaloOpenSlotOfferStatus.Expired) => "đã hết hạn",
            _ => row.Status
        };
    }

    private static ZaloMemberAssistReply Reply(ZaloPassSlotHistoryScope scope, string text) =>
        new(ZaloMemberAssistKind.PassSlotSummary, text, null);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string ReadString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return DateTimeOffset.MinValue;
        var value = reader.GetValue(ordinal);
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(dt);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static bool ContainsPhrase(string value, string phrase) =>
        Regex.IsMatch(value, $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])", RegexOptions.CultureInvariant);

    private static string DisplayName(string? value, string fallback = "bạn")
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    private static string NormalizeName(string? value) =>
        Regex.Replace(ZaloBotIntelligence.Normalize(value ?? string.Empty), @"\s+", " ").Trim();

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
