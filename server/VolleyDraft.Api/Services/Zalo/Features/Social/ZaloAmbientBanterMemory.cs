using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloBanterAffinitySnapshot(
    string ZaloConnectionId,
    string GroupId,
    string SenderId,
    int BanterCount,
    string? LastSourceMessageId,
    string LastReason,
    DateTimeOffset FirstBanterAt,
    DateTimeOffset LastBanterAt,
    DateTimeOffset UpdatedAt);

internal sealed record ZaloAmbientBanterMemoryContext(
    ZaloBanterAffinitySnapshot? Affinity,
    IReadOnlyList<ZaloAmbientSocialContextMessage> EvidenceMessages)
{
    public bool HasStrongAffinity => Affinity is not null;
    public bool HasAnyEvidence => HasStrongAffinity || EvidenceMessages.Count > 0;
}

internal static class ZaloBanterAffinityPolicy
{
    private static readonly Regex PlayfulMarker = new(
        @"(?:[:=;xX][-^']*\)+|\b(?:haha+|hehe+|hihi+|kkk+|khia|ca\s+khia|hon|lao|nong|choc|troll|ghe|du|met\s+do|xam)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool LooksLikeDirectBanter(ZaloIncomingMessageEvent incoming)
    {
        var address = ZaloConversationalAddressResolver.Resolve(incoming, hasActiveProposal: false);
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        var addressedToBot = quote.RepliesToBot ||
                             (address.Target == ZaloConversationalTarget.Bot && address.Confidence >= .9);
        if (!addressedToBot) return false;

        if (ZaloTrashTalkPolicy.LooksLikeDirectTrashTalk(incoming.Content, address, leaseTurn: false))
            return true;

        return PlayfulMarker.IsMatch(ZaloBotIntelligence.Normalize(incoming.Content ?? string.Empty));
    }
}

/// <summary>
/// Durable per-group/per-member memory for people who have already been confidently
/// classified as bantering with the bot. The memory survives the short conversational
/// lease and is intentionally separate from user facts/preferences.
/// </summary>
internal sealed class ZaloBanterAffinityStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly Regex BotReference = new(
        @"(?<![a-z0-9])(?:bot|bott|npc|con\s+bot|thang\s+bot|cai\s+bot|nho\s+bot)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ZaloBanterAffinitySnapshot> RememberAsync(
        string zaloConnectionId,
        string groupId,
        string senderId,
        string? sourceMessageId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        senderId = Clean(senderId, 100);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        reason = Clean(reason, 160);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            throw new ArgumentException("Banter affinity requires connection, group and sender IDs.");
        if (reason.Length == 0) reason = "confirmed_bot_banter";

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO "ZaloBanterAffinities" (
                    "ZaloConnectionId", "GroupId", "SenderZaloUserId", "BanterCount", "LastSourceMessageId",
                    "LastReason", "FirstBanterAt", "LastBanterAt", "UpdatedAt")
                VALUES (@connectionId, @groupId, @senderId, 1, @sourceMessageId, @reason, @now, @now, @now)
                ON CONFLICT ("ZaloConnectionId", "GroupId", "SenderZaloUserId") DO UPDATE SET
                    "BanterCount" = "ZaloBanterAffinities"."BanterCount" + 1,
                    "LastSourceMessageId" = COALESCE(@sourceMessageId, "ZaloBanterAffinities"."LastSourceMessageId"),
                    "LastReason" = @reason,
                    "LastBanterAt" = @now,
                    "UpdatedAt" = @now;
                """;
            Add(command, "@connectionId", zaloConnectionId);
            Add(command, "@groupId", groupId);
            Add(command, "@senderId", senderId);
            Add(command, "@sourceMessageId", sourceMessageId);
            Add(command, "@reason", reason);
            Add(command, "@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await LoadAsync(zaloConnectionId, groupId, senderId, cancellationToken)
               ?? throw new InvalidOperationException("Remembered banter affinity could not be reloaded.");
    }

    public async Task<ZaloBanterAffinitySnapshot?> LoadAsync(
        string zaloConnectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        senderId = Clean(senderId, 100);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return null;

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ZaloConnectionId", "GroupId", "SenderZaloUserId", "BanterCount", "LastSourceMessageId",
                   "LastReason", "FirstBanterAt", "LastBanterAt", "UpdatedAt"
            FROM "ZaloBanterAffinities"
            WHERE "ZaloConnectionId" = @connectionId
              AND "GroupId" = @groupId
              AND "SenderZaloUserId" = @senderId
            LIMIT 1;
            """;
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@senderId", senderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloBanterAffinitySnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Convert.ToInt32(reader.GetValue(3)),
            reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4)),
            reader.GetString(5),
            Timestamp(reader.GetValue(6)),
            Timestamp(reader.GetValue(7)),
            Timestamp(reader.GetValue(8)));
    }

    public async Task<ZaloAmbientBanterMemoryContext> LoadContextAsync(
        string zaloConnectionId,
        string groupId,
        string senderId,
        string botId,
        string currentMessageId,
        int memoryDays,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        var affinity = await LoadAsync(zaloConnectionId, groupId, senderId, cancellationToken);
        memoryDays = Math.Clamp(memoryDays, 1, 365);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-memoryDays);
        var cleanSender = Clean(senderId, 100);
        var cleanCurrentMessage = Clean(currentMessageId, 160);
        if (cleanSender.Length == 0)
            return new ZaloAmbientBanterMemoryContext(affinity, []);

        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == zaloConnectionId &&
                           item.GroupId == groupId &&
                           item.SenderId == cleanSender &&
                           item.MessageId != cleanCurrentMessage &&
                           !item.IsFromBot &&
                           item.SentAt >= cutoff)
            .OrderByDescending(item => item.SentAt)
            .Take(50)
            .Select(item => new { item.MessageId, item.SenderName, item.Content, item.SentAt })
            .ToListAsync(cancellationToken);

        var replyRelations = await new ZaloMessageGraphStore(db)
            .LoadReplyRelationsForSenderAsync(zaloConnectionId, groupId, cleanSender, cancellationToken);
        var cleanBotId = Clean(botId, 100);
        var evidence = rows
            .Where(item =>
            {
                if (BotReference.IsMatch(ZaloBotIntelligence.Normalize(item.Content))) return true;
                return replyRelations.TryGetValue(item.MessageId, out var relation) &&
                       relation.QuotedSenderId is not null &&
                       cleanBotId.Length > 0 &&
                       string.Equals(relation.QuotedSenderId, cleanBotId, StringComparison.Ordinal);
            })
            .Take(3)
            .Select(item => new ZaloAmbientSocialContextMessage(
                cleanSender,
                Clean(item.SenderName, 80),
                Clean(item.Content, 400),
                false,
                Clean(item.MessageId, 160),
                item.SentAt,
                "banter_memory"))
            .ToArray();

        return new ZaloAmbientBanterMemoryContext(affinity, evidence);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        if (!isSqlite && !isPostgres) return;

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = isPostgres ? "timestamp with time zone" : "TEXT";
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE IF NOT EXISTS "ZaloBanterAffinities" (
                    "ZaloConnectionId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "SenderZaloUserId" TEXT NOT NULL,
                    "BanterCount" INTEGER NOT NULL DEFAULT 0,
                    "LastSourceMessageId" TEXT NULL,
                    "LastReason" TEXT NOT NULL,
                    "FirstBanterAt" {timestamp} NOT NULL,
                    "LastBanterAt" {timestamp} NOT NULL,
                    "UpdatedAt" {timestamp} NOT NULL,
                    CONSTRAINT "PK_ZaloBanterAffinities" PRIMARY KEY ("ZaloConnectionId", "GroupId", "SenderZaloUserId")
                );
                CREATE INDEX IF NOT EXISTS "IX_ZaloBanterAffinities_UpdatedAt"
                ON "ZaloBanterAffinities" ("UpdatedAt");
                """, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static DateTimeOffset Timestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }

    private static async Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }
}
