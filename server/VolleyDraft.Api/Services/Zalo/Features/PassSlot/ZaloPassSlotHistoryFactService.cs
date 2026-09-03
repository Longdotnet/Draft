using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services.Zalo.Conversation;
using VolleyDraft.Api.Services.Zalo.Features.PassSlot;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloPassSlotHistoryRow(
    string OfferId,
    string OwnerZaloUserId,
    string OwnerDisplayName,
    string SessionId,
    string SessionName,
    string Status,
    string? ClaimantDisplayName,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Read-only PassSlot feature service backed by ZaloOpenSlotOffers.
/// Natural-language scope belongs to PassSlotQueryPolicy; this class owns fact loading,
/// deterministic filtering and response rendering only.
/// </summary>
internal sealed class ZaloPassSlotHistoryFactService(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    internal static bool LooksLikeQuery(string? content) =>
        ZaloPassSlotQueryPolicy.LooksLikeQuery(content);

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

        var normalized = ZaloTextNormalizer.Normalize(incoming.Content);
        var scope = ZaloPassSlotQueryPolicy.ResolveScope(normalized);
        var referenceNow = (now ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);

        var rows = await LoadRowsAsync(connectionId, groupId, cancellationToken);
        if (rows.Count == 0)
            return Reply(scope, "Tui chưa ghi nhận lượt pass slot nào trong dữ liệu của nhóm này nha.");

        var sessionStarts = await LoadSessionStartsAsync(
            connectionId,
            groupId,
            rows.Select(row => row.SessionId).Distinct(StringComparer.Ordinal).ToArray(),
            cancellationToken);

        var sessionReferences = rows
            .GroupBy(row => row.SessionId, StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group.OrderByDescending(row => row.UpdatedAt).First();
                return new ZaloSessionReference(
                    representative.SessionId,
                    representative.SessionName,
                    sessionStarts.GetValueOrDefault(representative.SessionId));
            })
            .ToList();

        var hasSessionSelector = ZaloSessionResolver.LooksLikeSelector(normalized) ||
                                 sessionReferences.Any(reference =>
                                 {
                                     var name = ZaloTextNormalizer.Normalize(reference.Name);
                                     return name.Length >= 3 && normalized.Contains(name, StringComparison.Ordinal);
                                 });

        var resolvedSessionIds = hasSessionSelector
            ? ZaloSessionResolver.Resolve(normalized, sessionReferences, referenceNow)
                .CandidateIds
                .ToHashSet(StringComparer.Ordinal)
            : [];

        IEnumerable<ZaloPassSlotHistoryRow> filtered = rows;
        filtered = scope switch
        {
            ZaloPassSlotHistoryScope.CurrentOpen => filtered.Where(row =>
                IsCurrentlyOpen(row, sessionStarts.GetValueOrDefault(row.SessionId), referenceNow)),
            ZaloPassSlotHistoryScope.SessionCurrentOpen => filtered.Where(row =>
                resolvedSessionIds.Contains(row.SessionId) &&
                IsCurrentlyOpen(row, sessionStarts.GetValueOrDefault(row.SessionId), referenceNow)),
            ZaloPassSlotHistoryScope.SessionToday => filtered.Where(row =>
                sessionStarts.TryGetValue(row.SessionId, out var start) &&
                start is not null &&
                start.Value.ToOffset(VietnamOffset).Date == referenceNow.Date),
            ZaloPassSlotHistoryScope.EventToday => filtered.Where(row =>
                row.CreatedAt.ToOffset(VietnamOffset).Date == referenceNow.Date),
            ZaloPassSlotHistoryScope.SpecificSession => filtered.Where(row =>
                resolvedSessionIds.Contains(row.SessionId)),
            _ => filtered
        };

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
            ZaloPassSlotHistoryScope.SessionCurrentOpen => $"Kèo này hiện còn {offerCount} slot pass đang mở từ {peopleCount} người:",
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
                .Append(StatusLabel(row))
                .Append(')');
        }

        if (selected.Count > 8)
            builder.Append($"\n… còn {selected.Count - 8} slot nữa.");

        if (scope is not (ZaloPassSlotHistoryScope.CurrentOpen or ZaloPassSlotHistoryScope.SessionCurrentOpen) && selected.Count > 1)
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
        _ = await new ZaloOpenSlotOfferStore(db)
            .ListClaimableAsync(connectionId, groupId, "__readonly_history__", cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "OwnerZaloUserId", "OwnerDisplayName", "SessionId", "SessionName",
                   "Status", "ClaimantDisplayName", "ExpiresAt", "CreatedAt", "UpdatedAt"
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
                ReadDateTimeOffset(reader, 8),
                ReadDateTimeOffset(reader, 9)));
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

    private static bool IsCurrentlyOpen(
        ZaloPassSlotHistoryRow row,
        DateTimeOffset? sessionStart,
        DateTimeOffset referenceNow)
    {
        if (row.Status != nameof(ZaloOpenSlotOfferStatus.Open)) return false;
        if (row.ExpiresAt <= referenceNow) return false;
        return sessionStart is null || sessionStart.Value > referenceNow;
    }

    private static string EmptyText(ZaloPassSlotHistoryScope scope) => scope switch
    {
        ZaloPassSlotHistoryScope.CurrentOpen => "Hiện không còn slot pass nào đang mở nha 👌",
        ZaloPassSlotHistoryScope.SessionCurrentOpen => "Kèo đó hiện không còn slot pass nào đang mở nha 👌",
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
        reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

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

    private static string DisplayName(string? value, string fallback = "bạn")
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    private static string NormalizeName(string? value) =>
        ZaloTextNormalizer.Normalize(value);

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
