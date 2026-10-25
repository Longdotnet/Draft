using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloMemberIntegrationContextBuilder(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private const int MaxActivityMessages = 5000;
    private const int MaxActivityPolls = 1000;
    private const int MaxReplyRelations = 5000;
    private readonly ZaloMemberIntegrationSettings settings = ZaloMemberIntegrationSettings.FromConfiguration(configuration);

    public async Task<IReadOnlyList<ZaloMemberIntegrationContext>> BuildRecentAsync(
        string connectionId,
        string groupId,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken = default)
    {
        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        if (connectionId.Length == 0 || groupId.Length == 0 || !settings.Enabled) return [];

        var tracked = (await new ZaloAutoSessionSettingsStore(db).GetAllAsync(cancellationToken))
            .FirstOrDefault(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId);
        var groupName = tracked?.GroupName?.Trim() ?? groupId;
        if (!settings.AppliesTo(groupName)) return [];

        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken);
        if (connection is null) return [];

        var membership = new ZaloMembershipHistoryService(
            db,
            NullLogger<ZaloMembershipHistoryService>.Instance);
        var recent = await membership.QueryRecentAsync(
            connectionId,
            groupId,
            settings.NewMemberWindowDays,
            snapshotAt,
            cancellationToken);
        var recoveredStore = new ZaloRecoveredNewcomerStore(db);
        await EnsureConfiguredRecoveriesAsync(connectionId, groupId, groupName, cancellationToken);
        var recovered = await recoveredStore.GetActiveAsync(
            connectionId,
            groupId,
            snapshotAt.AddDays(-settings.NewMemberWindowDays),
            cancellationToken);
        if (recent.Members.Count == 0 && recovered.Count == 0) return [];

        var currentMembers = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.IsCurrentMember)
            .Select(item => new { item.ZaloUserId, item.DisplayName })
            .ToListAsync(cancellationToken);
        var names = currentMembers
            .GroupBy(item => CleanId(item.ZaloUserId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => CleanName(group.First().DisplayName), StringComparer.Ordinal);

        var activityStart = snapshotAt.AddDays(-Math.Max(30, settings.NewMemberWindowDays));
        var allMessages = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId && !item.IsFromBot)
            .OrderByDescending(item => item.SentAt)
            .Take(MaxActivityMessages)
            .Select(item => new
            {
                item.MessageId,
                item.SenderId,
                item.SenderName,
                item.SentAt
            })
            .ToListAsync(cancellationToken);
        var messages = allMessages
            .Where(item => item.SentAt >= activityStart && item.SentAt <= snapshotAt)
            .ToList();

        var pollRows = await db.ZaloPollSnapshots
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId && item.IsAnalyticsEligible)
            .OrderByDescending(item => item.CreatedAtFromZalo)
            .Take(MaxActivityPolls)
            .Select(item => new { item.Id, item.CreatedAtFromZalo })
            .ToListAsync(cancellationToken);
        var recentPollIds = pollRows
            .Where(item => item.CreatedAtFromZalo is { } created && created >= activityStart && created <= snapshotAt)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var voteRows = recentPollIds.Count == 0
            ? new List<VoteRow>()
            : await db.ZaloPollVoteActivities
                .AsNoTracking()
                .Where(item => recentPollIds.Contains(item.PollSnapshotId) && item.IsCurrentlySelected)
                .Select(item => new VoteRow(item.PollSnapshotId, item.ZaloUserId))
                .ToListAsync(cancellationToken);

        var messageCounts = messages
            .GroupBy(item => CleanId(item.SenderId), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var lastMessages = messages
            .GroupBy(item => CleanId(item.SenderId), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.Max(item => item.SentAt), StringComparer.Ordinal);
        var voteCounts = voteRows
            .GroupBy(item => CleanId(item.ZaloUserId), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.PollSnapshotId).Distinct(StringComparer.Ordinal).Count(),
                StringComparer.Ordinal);

        var relations = await LoadReplyRelationsAsync(connectionId, groupId, cancellationToken);
        var messageSenderById = messages
            .Where(item => !string.IsNullOrWhiteSpace(item.MessageId))
            .GroupBy(item => item.MessageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => CleanId(group.First().SenderId), StringComparer.Ordinal);

        var roles = await LoadRolesAsync(connection, groupId, names, cancellationToken);
        var botUserId = CleanId(connection.AccountZaloId);
        var recentMemberIds = recent.Members
            .Select(item => CleanId(item.ZaloUserId))
            .Concat(recovered.Select(item => CleanId(item.ZaloUserId)))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var peers = currentMembers
            .Select(member =>
            {
                var id = CleanId(member.ZaloUserId);
                DateTimeOffset? lastActivity = lastMessages.TryGetValue(id, out var lastMessage)
                    ? lastMessage
                    : null;
                return new ZaloMemberIntegrationPeer(
                    id,
                    CleanName(member.DisplayName),
                    messageCounts.GetValueOrDefault(id),
                    voteCounts.GetValueOrDefault(id),
                    lastActivity);
            })
            .Where(item => item.ZaloUserId.Length > 0 &&
                           item.DisplayName.Length > 0 &&
                           !recentMemberIds.Contains(item.ZaloUserId) &&
                           !string.Equals(item.ZaloUserId, botUserId, StringComparison.Ordinal))
            .OrderByDescending(item => item.MessageCount + item.VoteCount * 2)
            .ThenByDescending(item => item.LastActivityAt)
            .Take(12)
            .ToArray();
        var loreHints = BuildLoreHints(peers);
        var facts = new ZaloMemberIntegrationGroupFacts(
            groupName,
            settings.BotCreatorName,
            settings.GroupFounderName,
            settings.GroupPurpose,
            settings.CultureSummary,
            settings.NormalCourtFeeVnd,
            settings.MaxPlayersPerSession,
            settings.ApprovedLore);

        var providerIds = recent.Members
            .Select(item => CleanId(item.ZaloUserId))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var currentIds = currentMembers
            .Select(item => CleanId(item.ZaloUserId))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = recent.Members
            .Select(item => new IntegrationCandidate(
                CleanId(item.ZaloUserId),
                CleanName(item.DisplayName),
                item.JoinedAt,
                item.JoinedAt,
                item.IsRejoin,
                false))
            .Concat(recovered
                .Where(item => !providerIds.Contains(CleanId(item.ZaloUserId)) && currentIds.Contains(CleanId(item.ZaloUserId)))
                .Select(item => new IntegrationCandidate(
                    CleanId(item.ZaloUserId),
                    CleanName(item.DisplayName),
                    null,
                    item.FirstObservedAt,
                    false,
                    true)))
            .OrderByDescending(item => item.IntegrationStartedAt)
            .ToArray();

        var contexts = new List<ZaloMemberIntegrationContext>();
        foreach (var candidate in candidates)
        {
            var userId = CleanId(candidate.ZaloUserId);
            if (userId.Length == 0 || string.Equals(userId, botUserId, StringComparison.Ordinal)) continue;
            var analysisStartAt = candidate.JoinedAt ?? candidate.IntegrationStartedAt;
            var memberMessages = messages
                .Where(item => CleanId(item.SenderId) == userId && item.SentAt >= analysisStartAt)
                .ToList();
            var memberMessageIds = memberMessages
                .Select(item => item.MessageId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToHashSet(StringComparer.Ordinal);

            var interactionIds = new HashSet<string>(StringComparer.Ordinal);
            var repliedToMember = false;
            var beenRepliedTo = false;
            foreach (var relation in relations)
            {
                messageSenderById.TryGetValue(relation.FromMessageId, out var fromSender);
                var quotedSender = CleanId(relation.QuotedSenderId);
                if (string.Equals(fromSender, userId, StringComparison.Ordinal) &&
                    quotedSender.Length > 0 && quotedSender != userId && quotedSender != botUserId)
                {
                    repliedToMember = true;
                    interactionIds.Add(quotedSender);
                }
                if (quotedSender == userId &&
                    !string.IsNullOrWhiteSpace(fromSender) && fromSender != userId && fromSender != botUserId)
                {
                    beenRepliedTo = true;
                    interactionIds.Add(fromSender);
                }
                if (memberMessageIds.Contains(relation.ToMessageId ?? string.Empty) &&
                    !string.IsNullOrWhiteSpace(fromSender) && fromSender != userId && fromSender != botUserId)
                {
                    beenRepliedTo = true;
                    interactionIds.Add(fromSender);
                }
            }

            var memberVotes = voteRows
                .Where(item => CleanId(item.ZaloUserId) == userId)
                .Select(item => item.PollSnapshotId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var activeDays = memberMessages
                .Select(item => item.SentAt.ToOffset(VietnamOffset).Date)
                .Distinct()
                .Count();
            DateTimeOffset? lastMessageAt = memberMessages.Count == 0
                ? null
                : memberMessages.Max(item => item.SentAt);
            var signals = new ZaloMemberIntegrationSignals(
                memberMessages.Count,
                activeDays,
                memberVotes,
                interactionIds.Count,
                memberMessages.Count > 0,
                repliedToMember,
                beenRepliedTo,
                lastMessageAt);
            var displayName = names.GetValueOrDefault(userId);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = CleanName(candidate.DisplayName);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = userId;

            contexts.Add(new ZaloMemberIntegrationContext(
                connectionId,
                CleanId(connection.AccountZaloId),
                groupId,
                userId,
                displayName,
                candidate.JoinedAt,
                candidate.IsRejoin,
                snapshotAt,
                signals,
                facts,
                roles,
                peers.Where(item => item.ZaloUserId != userId).Take(8).ToArray(),
                loreHints,
                candidate.IntegrationStartedAt,
                candidate.IsRecoveredNewcomer));
        }

        return contexts;
    }

    public async Task EnsureConfiguredRecoveriesAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        connectionId = CleanId(connectionId);
        groupId = CleanId(groupId);
        if (connectionId.Length == 0 || groupId.Length == 0 || !settings.Enabled || settings.RecoveredNewcomers.Count == 0)
            return;
        var tracked = (await new ZaloAutoSessionSettingsStore(db).GetAllAsync(cancellationToken))
            .FirstOrDefault(item => item.ZaloConnectionId == connectionId && item.GroupId == groupId);
        var groupName = tracked?.GroupName?.Trim() ?? groupId;
        await EnsureConfiguredRecoveriesAsync(connectionId, groupId, groupName, cancellationToken);
    }

    private async Task EnsureConfiguredRecoveriesAsync(
        string connectionId,
        string groupId,
        string groupName,
        CancellationToken cancellationToken)
    {
        if (!settings.AppliesTo(groupName) || settings.RecoveredNewcomers.Count == 0) return;
        var store = new ZaloRecoveredNewcomerStore(db);
        var existing = await store.GetActiveAsync(
            connectionId,
            groupId,
            DateTimeOffset.MinValue,
            cancellationToken);
        var existingIds = existing.Select(item => CleanId(item.ZaloUserId)).ToHashSet(StringComparer.Ordinal);
        foreach (var seed in settings.RecoveredNewcomers)
        {
            var userId = CleanId(seed.ZaloUserId);
            if (userId.Length == 0 || existingIds.Contains(userId)) continue;
            await store.UpsertAsync(
                connectionId,
                groupId,
                userId,
                seed.DisplayName,
                seed.FirstObservedAt,
                confirmedBy: "member-integration-config",
                sourceNote: "Admin-confirmed newcomer recovered after listener downtime.",
                confirmedAt: seed.FirstObservedAt,
                cancellationToken: cancellationToken);
            existingIds.Add(userId);
        }
    }

    private async Task<ZaloMemberIntegrationRoleDirectory> LoadRolesAsync(
        ZaloConnection connection,
        string groupId,
        IReadOnlyDictionary<string, string> knownNames,
        CancellationToken cancellationToken)
    {
        if (connection.Status != ZaloConnectionStatus.Connected || string.IsNullOrWhiteSpace(connection.EncryptedCredentials))
            return new(null, []);
        try
        {
            var protector = new ZaloCredentialProtector(configuration);
            using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
            var credentials = document.RootElement.Clone();
            var roles = await bridge.GetGroupRolesAsync(credentials, groupId);
            var creatorId = CleanId(roles.CreatorId);
            var adminIds = roles.AdminIds.Select(CleanId)
                .Where(id => id.Length > 0 && id != creatorId)
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToArray();
            var missingIds = new[] { creatorId }.Concat(adminIds)
                .Where(id => id.Length > 0 && !knownNames.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var bridgeNames = missingIds.Length == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : (await bridge.GetMembersAsync(credentials, missingIds))
                    .GroupBy(item => CleanId(item.ZaloUserId), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => CleanName(group.First().DisplayName), StringComparer.Ordinal);
            string NameOf(string id) => knownNames.GetValueOrDefault(id) ?? bridgeNames.GetValueOrDefault(id) ?? id;
            var creator = creatorId.Length == 0
                ? null
                : new ZaloMemberIntegrationMentionTarget(creatorId, NameOf(creatorId));
            var admins = adminIds.Select(id => new ZaloMemberIntegrationMentionTarget(id, NameOf(id))).ToArray();
            return new ZaloMemberIntegrationRoleDirectory(creator, admins);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return new(null, []);
        }
    }

    private async Task<IReadOnlyList<ReplyRelation>> LoadReplyRelationsAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        try
        {
            await new ZaloMessageGraphStore(db)
                .LoadRelationAsync(connectionId, groupId, "__member_integration_schema_probe__", cancellationToken);
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "FromMessageId", "ToMessageId", "QuotedSenderId"
                FROM "ZaloMessageRelations"
                WHERE "ZaloConnectionId" = @connectionId
                  AND "GroupId" = @groupId
                  AND "RelationType" = 'ReplyTo'
                ORDER BY "CreatedAt" DESC
                LIMIT @take;
                """;
            Add(command, "@connectionId", connectionId);
            Add(command, "@groupId", groupId);
            Add(command, "@take", MaxReplyRelations);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<ReplyRelation>();
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new ReplyRelation(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            return result;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ZaloMemberIntegrationLoreHint> BuildLoreHints(
        IReadOnlyList<ZaloMemberIntegrationPeer> peers)
    {
        var hints = new List<ZaloMemberIntegrationLoreHint>();
        var voter = peers.Where(item => item.VoteCount >= 2)
            .OrderByDescending(item => item.VoteCount)
            .FirstOrDefault();
        if (voter is not null)
        {
            var mention = new ZaloMemberIntegrationMentionTarget(voter.ZaloUserId, voter.DisplayName);
            hints.Add(new(
                "regular-voter",
                $"@{voter.DisplayName} thuộc hội thấy poll là hay có mặt, dữ liệu gần đây vote khá đều 😄",
                [mention]));
        }
        var chatter = peers.Where(item => item.MessageCount >= 3 && item.ZaloUserId != voter?.ZaloUserId)
            .OrderByDescending(item => item.MessageCount)
            .FirstOrDefault();
        if (chatter is not null)
        {
            var mention = new ZaloMemberIntegrationMentionTarget(chatter.ZaloUserId, chatter.DisplayName);
            hints.Add(new(
                "regular-chatter",
                $"@{chatter.DisplayName} là một trong mấy ông hay xuất hiện trong chat gần đây, bắt chuyện ông này khá dễ 😆",
                [mention]));
        }
        return hints;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static string CleanName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= 80 ? text : text[..80];
    }

    private sealed record ReplyRelation(string FromMessageId, string? ToMessageId, string? QuotedSenderId);
    private sealed record VoteRow(string PollSnapshotId, string ZaloUserId);
    private sealed record IntegrationCandidate(
        string ZaloUserId,
        string DisplayName,
        DateTimeOffset? JoinedAt,
        DateTimeOffset IntegrationStartedAt,
        bool IsRejoin,
        bool IsRecoveredNewcomer);
}
