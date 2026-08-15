using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Read-only rollout telemetry for the ambient group agent. The service aggregates
/// routing metadata only; it never reads raw message content and never mutates domain state.
/// </summary>
public sealed class ZaloAmbientShadowMetricsService(
    VolleyDraftDbContext db,
    IConfiguration configuration)
{
    private static readonly HashSet<string> SuppressionSignals = new(StringComparer.Ordinal)
    {
        "ack_or_emoji_only",
        "bot_cooldown",
        "busy_group",
        "reply_to_member",
        "action_requires_address"
    };

    public async Task<ServiceResult<ZaloAmbientShadowMetricsResponse>> GetForSessionAsync(
        string adminUserId,
        string sessionId,
        int hours = 24,
        CancellationToken cancellationToken = default)
    {
        hours = Math.Clamp(hours, 1, 168);
        var session = await db.MatchSessions
            .AsNoTracking()
            .Where(item => item.Id == sessionId && item.AdminUserId == adminUserId)
            .Select(item => new
            {
                item.Id,
                item.ZaloConnectionId,
                item.ZaloGroupId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
            return ServiceResult<ZaloAmbientShadowMetricsResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy session.");
        if (string.IsNullOrWhiteSpace(session.ZaloConnectionId) || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ServiceResult<ZaloAmbientShadowMetricsResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Session chưa liên kết group Zalo.");

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-hours);
        var rows = await ReadRowsAsync(session.ZaloGroupId, cutoff, cancellationToken);
        var settings = ZaloAmbientSettings.FromConfiguration(configuration);

        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ZaloAmbientParticipationKind.Fact.ToString()] = 0,
            [ZaloAmbientParticipationKind.Social.ToString()] = 0,
            [ZaloAmbientParticipationKind.Action.ToString()] = 0,
            [ZaloAmbientParticipationKind.None.ToString()] = 0,
            ["Unknown"] = 0
        };
        var intents = new Dictionary<string, int>(StringComparer.Ordinal);
        var suppressions = new Dictionary<string, int>(StringComparer.Ordinal);
        var wouldReply = 0;
        var highConfidenceFact = 0;
        var totalScore = 0d;

        foreach (var row in rows)
        {
            var tokens = ParseMetadata(row.Metadata);
            var kind = tokens.Kind ?? "Unknown";
            if (!kindCounts.ContainsKey(kind)) kind = "Unknown";
            kindCounts[kind] += 1;

            var intent = string.IsNullOrWhiteSpace(row.Intent) ? "Unknown" : row.Intent!;
            intents[intent] = intents.GetValueOrDefault(intent) + 1;

            foreach (var signal in tokens.Signals.Where(SuppressionSignals.Contains))
                suppressions[signal] = suppressions.GetValueOrDefault(signal) + 1;

            var score = Math.Clamp((row.Confidence ?? 0d) * 100d, 0d, 100d);
            totalScore += score;
            var rowWouldReply = string.Equals(row.AddressReason, "AmbientShadowWouldReply", StringComparison.Ordinal);
            if (rowWouldReply) wouldReply += 1;
            if (rowWouldReply && string.Equals(kind, ZaloAmbientParticipationKind.Fact.ToString(), StringComparison.Ordinal))
                highConfidenceFact += 1;
        }

        var observed = rows.Count;
        return ServiceResult<ZaloAmbientShadowMetricsResponse>.Success(new ZaloAmbientShadowMetricsResponse(
            session.Id,
            session.ZaloGroupId,
            cutoff,
            now,
            settings.Enabled,
            settings.ShadowMode,
            settings.WouldReplyThreshold,
            observed,
            wouldReply,
            observed == 0 ? 0 : Math.Round((double)wouldReply / observed, 4),
            observed == 0 ? 0 : Math.Round(totalScore / observed, 2),
            highConfidenceFact,
            kindCounts,
            intents
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(10)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            suppressions
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
    }

    private async Task<List<TraceRow>> ReadRowsAsync(
        string groupId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var store = new ZaloBotTraceStore(db);
        await store.EnsureReadyAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "AddressReason", "Intent", "Confidence", "FallbackReason"
            FROM "ZaloBotTraces"
            WHERE "GroupId" = @groupId
              AND "IntentSource" = 'AmbientShadow'
              AND "CreatedAt" >= @cutoff
            ORDER BY "CreatedAt" DESC;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@cutoff", cutoff);

        var rows = new List<TraceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TraceRow(
                reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty,
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3))));
        }
        return rows;
    }

    private static ParsedMetadata ParseMetadata(string? metadata)
    {
        var kind = (string?)null;
        var signals = new List<string>();
        foreach (var raw in (metadata ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("kind:", StringComparison.Ordinal))
            {
                kind = raw[5..];
                continue;
            }
            signals.Add(raw);
        }
        return new ParsedMetadata(kind, signals);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record TraceRow(string AddressReason, string? Intent, double? Confidence, string? Metadata);
    private sealed record ParsedMetadata(string? Kind, IReadOnlyList<string> Signals);
}
