using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Read-only rollout metrics for the authoritative domain-event narrator.
/// It reads metadata-only trace rows and never changes pilot settings or domain state.
/// </summary>
public sealed class ZaloDomainEventPilotMetricsService(
    VolleyDraftDbContext db,
    IConfiguration configuration)
{
    public async Task<ServiceResult<ZaloDomainEventPilotReadinessResponse>> GetForSessionAsync(
        string adminUserId,
        string sessionId,
        int hours = 168,
        CancellationToken cancellationToken = default)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var session = await db.MatchSessions
            .AsNoTracking()
            .Where(item => item.Id == sessionId && item.AdminUserId == adminUserId)
            .Select(item => new { item.Id, item.ZaloGroupId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
            return ServiceResult<ZaloDomainEventPilotReadinessResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy session.");
        if (string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ServiceResult<ZaloDomainEventPilotReadinessResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Session chưa liên kết group Zalo.");

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-hours);
        var rows = await ReadRowsAsync(session.ZaloGroupId, session.Id, cutoff, cancellationToken);

        var eventKinds = rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Intent) ? "Unknown" : row.Intent!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var sent = rows.Count(row => string.Equals(row.AddressReason, "DomainEventNarratorSent", StringComparison.Ordinal));
        var suppressed = rows.Count(row => string.Equals(row.AddressReason, "DomainEventNarratorSuppressed", StringComparison.Ordinal));
        var notEligible = rows.Count(row => string.Equals(row.AddressReason, "DomainEventNarratorNotEligible", StringComparison.Ordinal));
        var narratable = rows.Count(row =>
            string.Equals(row.Intent, "RosterFilled", StringComparison.Ordinal) ||
            string.Equals(row.Intent, "RosterReopened", StringComparison.Ordinal));
        var suppressionReasons = rows
            .Where(row => string.Equals(row.AddressReason, "DomainEventNarratorSuppressed", StringComparison.Ordinal))
            .Select(row => ParseToken(row.Metadata, "reason"))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .GroupBy(reason => reason!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var pilotEnabled = configuration.GetValue<bool>("ZaloBot:Ambient:DomainEventPilot:Enabled");
        var sendEnabled = configuration.GetValue<bool>("ZaloBot:Ambient:DomainEventPilot:SendEnabled");
        var shadowMode = configuration.GetValue<bool>("ZaloBot:Ambient:ShadowMode", true);
        var blockers = BuildReadinessBlockers(rows.Count, narratable, sent, suppressed);

        return ServiceResult<ZaloDomainEventPilotReadinessResponse>.Success(new(
            session.Id,
            session.ZaloGroupId,
            cutoff,
            now,
            pilotEnabled,
            sendEnabled,
            shadowMode,
            rows.Count,
            narratable,
            sent,
            suppressed,
            notEligible,
            eventKinds,
            suppressionReasons,
            blockers.Count == 0,
            blockers));
    }

    internal static IReadOnlyList<string> BuildReadinessBlockers(
        int observedCount,
        int narratableCount,
        int sentCount,
        int suppressedCount)
    {
        var blockers = new List<string>();
        if (observedCount < 10)
            blockers.Add("Cần ít nhất 10 domain-event observations trước khi review bật live.");
        if (narratableCount < 3)
            blockers.Add("Cần ít nhất 3 sự kiện RosterFilled/RosterReopened để đánh giá chất lượng narration.");
        if (sentCount > 0)
            blockers.Add("Đã có outbound send trong cửa sổ đo; cần xác nhận đây là canary có chủ đích trước khi mở rộng.");
        if (suppressedCount == 0 && sentCount == 0)
            blockers.Add("Chưa có narration candidate đi qua rollout gates để kiểm chứng suppression telemetry.");
        return blockers;
    }

    private async Task<List<TraceRow>> ReadRowsAsync(
        string groupId,
        string sessionId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await new ZaloBotTraceStore(db).EnsureReadyAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "AddressReason", "Intent", "FallbackReason"
            FROM "ZaloBotTraces"
            WHERE "GroupId" = @groupId
              AND "ResolvedSessionId" = @sessionId
              AND "IntentSource" = 'AmbientDomainEventNarrator'
              AND "CreatedAt" >= @cutoff
            ORDER BY "CreatedAt" DESC;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@sessionId", sessionId);
        Add(command, "@cutoff", cutoff);

        var rows = new List<TraceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TraceRow(
                reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty,
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2))));
        }
        return rows;
    }

    private static string? ParseToken(string? metadata, string key)
    {
        var prefix = key + ":";
        foreach (var token in (metadata ?? string.Empty)
                     .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
                return token[prefix.Length..];
        }
        return null;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record TraceRow(string AddressReason, string? Intent, string? Metadata);
}
