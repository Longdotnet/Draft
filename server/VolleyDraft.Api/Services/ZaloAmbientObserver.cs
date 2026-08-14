using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Persists an unaddressed realtime message as observational group context, builds a
/// bounded group situation and writes an idempotent shadow participation trace.
/// No outbound send exists in this service by design.
/// </summary>
public sealed class ZaloAmbientObserver(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim TraceGate = new(1, 1);

    public async Task<ZaloAmbientParticipationDecision> ObserveAsync(
        string zaloConnectionId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientSettings settings,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        var groupId = Clean(incoming.GroupId, 100);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0)
            throw new ArgumentException("Connection and group are required for ambient observation.");

        await EnsureIncomingObservedAsync(zaloConnectionId, groupId, incoming, cancellationToken);
        var situation = await LoadSituationAsync(zaloConnectionId, groupId, settings, cancellationToken);
        var decision = ZaloAmbientParticipationEngine.Evaluate(incoming, situation, settings);
        await WriteTraceOnceAsync(groupId, incoming, decision, cancellationToken);
        return decision;
    }

    private async Task EnsureIncomingObservedAsync(
        string zaloConnectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var messageId = Clean(incoming.MessageId, 160);
        if (messageId.Length == 0) return;
        if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                item.ZaloConnectionId == zaloConnectionId && item.MessageId == messageId,
                cancellationToken))
            return;

        var now = DateTimeOffset.UtcNow;
        var message = new ZaloGroupMessage
        {
            ZaloConnectionId = zaloConnectionId,
            GroupId = groupId,
            MessageId = messageId,
            SenderId = Clean(incoming.SenderId, 100),
            SenderName = Trim(incoming.SenderName, 160, "Thành viên Zalo"),
            Content = Trim(incoming.Content, 4000, string.Empty),
            MessageType = "chat",
            ObservationSource = "AmbientShadow",
            IsFromBot = false,
            SentAt = SafeTimestamp(incoming.SentAtUnixMs),
            ReceivedAt = now,
            FirstObservedAt = now,
            LastObservedAt = now,
            AiCalled = false
        };
        db.ZaloGroupMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Only swallow a genuine duplicate race. Other database failures must
            // bubble to the caller, where ambient observation fails open with a log.
            db.Entry(message).State = EntityState.Detached;
            var duplicateExists = await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                item.ZaloConnectionId == zaloConnectionId && item.MessageId == messageId,
                cancellationToken);
            if (!duplicateExists) throw;
        }
    }

    private async Task<ZaloAmbientGroupSituation> LoadSituationAsync(
        string zaloConnectionId,
        string groupId,
        ZaloAmbientSettings settings,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // ZaloGroupMessages already has an index on (connection, group, SentAt).
        // Use that indexed timestamp instead of sorting by ReceivedAt on every chat.
        command.CommandText = """
            SELECT "MessageId", "SenderId", "IsFromBot", "SentAt"
            FROM "ZaloGroupMessages"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId
            ORDER BY "SentAt" DESC
            LIMIT @limit;
            """;
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@limit", settings.MaxRecentMessages);

        var rows = new List<RecentMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecentMessage(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                Convert.ToBoolean(reader.GetValue(2)),
                Timestamp(reader.GetValue(3))));
        }

        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddMinutes(-settings.RecentWindowMinutes);
        var recent = rows.Where(item => item.SentAt >= windowStart && item.SentAt <= now.AddMinutes(1)).ToList();
        var twoMinuteStart = now.AddMinutes(-2);
        var botRows = recent.Where(item => item.IsFromBot).ToList();
        var ids = recent
            .OrderBy(item => item.SentAt)
            .Select(item => item.MessageId)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ZaloAmbientGroupSituation(
            RecentMessageCount: recent.Count,
            RecentTwoMinuteMessageCount: recent.Count(item => item.SentAt >= twoMinuteStart),
            DistinctParticipantCount: recent
                .Where(item => !item.IsFromBot && item.SenderId.Length > 0)
                .Select(item => item.SenderId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            RecentBotMessageCount: botRows.Count,
            LastBotMessageAt: botRows.Count == 0 ? null : botRows.Max(item => item.SentAt),
            RecentMessageIds: ids);
    }

    private async Task WriteTraceOnceAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        CancellationToken cancellationToken)
    {
        const string source = "AmbientShadow";
        await TraceGate.WaitAsync(cancellationToken);
        try
        {
            var traceStore = new ZaloBotTraceStore(db);
            // Central trace store owns additive schema creation. UnixEpoch cannot delete
            // normal rows and gives this observer a provider-independent schema probe.
            await traceStore.DeleteOlderThanAsync(DateTimeOffset.UnixEpoch, cancellationToken);
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
            if (await TraceExistsAsync(connection, groupId, incoming.MessageId, source, cancellationToken)) return;

            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            await traceStore.WriteAsync(new ZaloBotTraceEntry(
                MessageId: Clean(incoming.MessageId, 160),
                GroupId: groupId,
                SenderZaloUserId: Clean(incoming.SenderId, 100),
                AddressReason: decision.WouldReply ? "AmbientShadowWouldReply" : "AmbientShadowObserve",
                IntentSource: source,
                Intent: decision.Intent,
                Confidence: decision.Score / 100d,
                ContextMessageIdsJson: JsonSerializer.Serialize(decision.Situation.RecentMessageIds.Take(12)),
                QuotedMessageId: quote.MessageId,
                AiCalled: false,
                FallbackReason: string.Join('|', decision.Signals.Take(12))), cancellationToken);
        }
        finally
        {
            TraceGate.Release();
        }
    }

    private static async Task<bool> TraceExistsAsync(
        DbConnection connection,
        string groupId,
        string messageId,
        string source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM "ZaloBotTraces"
            WHERE "GroupId" = @groupId AND "MessageId" = @messageId AND "IntentSource" = @source
            LIMIT 1;
            """;
        Add(command, "@groupId", Clean(groupId, 100));
        Add(command, "@messageId", Clean(messageId, 160));
        Add(command, "@source", source);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static DateTimeOffset SafeTimestamp(long unixMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static DateTimeOffset Timestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string Trim(string? value, int maxLength, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) text = fallback;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record RecentMessage(string MessageId, string SenderId, bool IsFromBot, DateTimeOffset SentAt);
}
