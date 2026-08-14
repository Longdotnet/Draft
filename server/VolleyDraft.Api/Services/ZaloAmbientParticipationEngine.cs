using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public enum ZaloAmbientParticipationKind
{
    None,
    Fact,
    Social,
    Action
}

public sealed record ZaloAmbientSettings(
    bool Enabled,
    bool ShadowMode,
    int WouldReplyThreshold,
    int RecentWindowMinutes,
    int MaxRecentMessages,
    int BotCooldownSeconds,
    int BusyGroupMessagesPerTwoMinutes)
{
    public static ZaloAmbientSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:Enabled", true),
        ShadowMode: configuration.GetValue("ZaloBot:Ambient:ShadowMode", true),
        WouldReplyThreshold: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:WouldReplyThreshold", 65), 40, 95),
        RecentWindowMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:RecentWindowMinutes", 5), 1, 30),
        MaxRecentMessages: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:MaxRecentMessages", 40), 5, 100),
        BotCooldownSeconds: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:BotCooldownSeconds", 20), 0, 300),
        BusyGroupMessagesPerTwoMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:BusyGroupMessagesPerTwoMinutes", 8), 3, 50));
}

public sealed record ZaloAmbientGroupSituation(
    int RecentMessageCount,
    int RecentTwoMinuteMessageCount,
    int DistinctParticipantCount,
    int RecentBotMessageCount,
    DateTimeOffset? LastBotMessageAt,
    IReadOnlyList<string> RecentMessageIds);

public sealed record ZaloAmbientParticipationDecision(
    bool WouldReply,
    int Score,
    ZaloAmbientParticipationKind Kind,
    string Intent,
    double IntentConfidence,
    IReadOnlyList<string> Signals,
    ZaloAmbientGroupSituation Situation);

/// <summary>
/// Pure deterministic policy for deciding whether an unaddressed group message is
/// interesting enough that a future ambient bot would participate. Phase 1 only
/// records this decision in shadow mode; it never sends a message or mutates domain data.
/// </summary>
public static class ZaloAmbientParticipationEngine
{
    private static readonly Regex QuestionPattern = new(
        @"\?|(?<![a-z0-9])(?:ai|bao nhieu|may|chua|sao|dau|nao|gi|du chua|con .* khong|co .* khong)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionPattern = new(
        @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s+(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s+nhat)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DomainPattern = new(
        @"(?<![a-z0-9])(?:vote|poll|slot|draft|team|doi|roster|danh\s+sach|san|tran|keo|waitlist|cho\s+slot)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Acknowledgements = new(StringComparer.Ordinal)
    {
        "ok", "oke", "okay", "uh", "uhm", "um", "roi", "duoc", "chuan", "ngon",
        "haha", "hehe", "hihi", "kk", "kkk", "cam on", "thanks", "thank you", "yes", "yep"
    };

    public static ZaloAmbientParticipationDecision Evaluate(
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientGroupSituation situation,
        ZaloAmbientSettings settings,
        DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var normalized = ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty);
        var signals = new List<string>();

        if (incoming.MentionedBot)
        {
            return new ZaloAmbientParticipationDecision(
                false,
                0,
                ZaloAmbientParticipationKind.None,
                ZaloBotIntent.Unknown.ToString(),
                0,
                ["explicit_address_uses_normal_router"],
                situation);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ZaloAmbientParticipationDecision(
                false,
                0,
                ZaloAmbientParticipationKind.None,
                ZaloBotIntent.Unknown.ToString(),
                0,
                ["empty_message"],
                situation);
        }

        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(incoming.Content);
        var factIntent = IsFactIntent(deterministic.Intent);
        var operationalIntent = deterministic.Intent is not ZaloBotIntent.Unknown
            and not ZaloBotIntent.GeneralChat
            and not ZaloBotIntent.Help;
        var actionIntent = operationalIntent && !factIntent;
        var question = QuestionPattern.IsMatch(normalized);
        var hasSession = SessionPattern.IsMatch(normalized);
        var hasDomainWords = DomainPattern.IsMatch(normalized);
        var acknowledgement = IsAcknowledgementOrEmojiOnly(normalized);
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);

        var kind = factIntent
            ? ZaloAmbientParticipationKind.Fact
            : actionIntent
                ? ZaloAmbientParticipationKind.Action
                : question
                    ? ZaloAmbientParticipationKind.Social
                    : ZaloAmbientParticipationKind.None;

        var score = 0;
        if (factIntent)
        {
            score += 55;
            signals.Add("fact_intent");
        }
        else if (actionIntent)
        {
            // An ambient participant may notice an operational action request, but
            // action/mutation execution always requires the normal explicitly-addressed path.
            score += 20;
            signals.Add("action_requires_address");
        }

        if (question)
        {
            score += 25;
            signals.Add("question");
        }
        if (hasSession)
        {
            score += 15;
            signals.Add("session_reference");
        }
        if (hasDomainWords)
        {
            score += 15;
            signals.Add("volley_domain_language");
        }

        if (quote.HasQuote && !quote.RepliesToBot)
        {
            score -= 15;
            signals.Add("reply_to_member");
        }

        if (acknowledgement)
        {
            score -= 60;
            signals.Add("ack_or_emoji_only");
        }

        if (settings.BotCooldownSeconds > 0 &&
            situation.LastBotMessageAt is { } lastBot &&
            current - lastBot < TimeSpan.FromSeconds(settings.BotCooldownSeconds))
        {
            score -= 30;
            signals.Add("bot_cooldown");
        }

        if (situation.RecentTwoMinuteMessageCount >= settings.BusyGroupMessagesPerTwoMinutes)
        {
            score -= 20;
            signals.Add("busy_group");
        }
        else if (situation.RecentTwoMinuteMessageCount <= 2)
        {
            score += 5;
            signals.Add("quiet_group");
        }

        score = Math.Clamp(score, 0, 100);
        var wouldReply = !acknowledgement &&
                         kind is ZaloAmbientParticipationKind.Fact or ZaloAmbientParticipationKind.Social &&
                         score >= settings.WouldReplyThreshold;

        return new ZaloAmbientParticipationDecision(
            wouldReply,
            score,
            kind,
            deterministic.Intent.ToString(),
            deterministic.Confidence,
            signals.Distinct(StringComparer.Ordinal).ToArray(),
            situation);
    }

    private static bool IsFactIntent(ZaloBotIntent intent) => intent is
        ZaloBotIntent.SessionSchedule or
        ZaloBotIntent.SelfMembership or
        ZaloBotIntent.LocationParking or
        ZaloBotIntent.MissingSlots or
        ZaloBotIntent.UpcomingSessions or
        ZaloBotIntent.Roster or
        ZaloBotIntent.WeeklySessionCount or
        ZaloBotIntent.ModelInfo or
        ZaloBotIntent.TeamLineup or
        ZaloBotIntent.ReminderStatus or
        ZaloBotIntent.WaitlistStatus or
        ZaloBotIntent.ActionHistory or
        ZaloBotIntent.ListMembersWithoutRecentVote or
        ZaloBotIntent.ListMembersWithoutRecentMessage or
        ZaloBotIntent.GetMemberLastActivity or
        ZaloBotIntent.GetMemberLastVote or
        ZaloBotIntent.GetMemberLastMessage or
        ZaloBotIntent.AnalyzeMemberVoteActivity or
        ZaloBotIntent.AnalyzeMemberMessageActivity or
        ZaloBotIntent.AnalyzeGroupEngagement or
        ZaloBotIntent.ListMostInactiveMembers or
        ZaloBotIntent.ListAtRiskMembers or
        ZaloBotIntent.GetActivitySyncStatus;

    private static bool IsAcknowledgementOrEmojiOnly(string normalized)
    {
        var words = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ").Trim();
        if (words.Length == 0) return true;
        return Acknowledgements.Contains(words);
    }
}

/// <summary>
/// Persists an unaddressed realtime message as observational group context, builds a
/// bounded group situation and writes an idempotent shadow participation trace.
/// No outbound send exists in this service by design.
/// </summary>
public sealed class ZaloAmbientObserver(VolleyDraftDbContext db)
{
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
            // Duplicate webhook delivery may race another request. The unique provider
            // message ID is the idempotency boundary, so keep the already stored row.
            db.Entry(message).State = EntityState.Detached;
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
        command.CommandText = """
            SELECT "MessageId", "SenderId", "IsFromBot", "ReceivedAt"
            FROM "ZaloGroupMessages"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId
            ORDER BY "ReceivedAt" DESC
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
        var recent = rows.Where(item => item.ReceivedAt >= windowStart).ToList();
        var twoMinuteStart = now.AddMinutes(-2);
        var botRows = recent.Where(item => item.IsFromBot).ToList();
        var ids = recent
            .OrderBy(item => item.ReceivedAt)
            .Select(item => item.MessageId)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ZaloAmbientGroupSituation(
            RecentMessageCount: recent.Count,
            RecentTwoMinuteMessageCount: recent.Count(item => item.ReceivedAt >= twoMinuteStart),
            DistinctParticipantCount: recent
                .Where(item => !item.IsFromBot && item.SenderId.Length > 0)
                .Select(item => item.SenderId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            RecentBotMessageCount: botRows.Count,
            LastBotMessageAt: botRows.Count == 0 ? null : botRows.Max(item => item.ReceivedAt),
            RecentMessageIds: ids);
    }

    private async Task WriteTraceOnceAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision decision,
        CancellationToken cancellationToken)
    {
        const string source = "AmbientShadow";
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

    private sealed record RecentMessage(string MessageId, string SenderId, bool IsFromBot, DateTimeOffset ReceivedAt);
}
