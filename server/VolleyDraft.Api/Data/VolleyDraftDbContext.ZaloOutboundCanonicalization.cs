using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Data;

public sealed partial class VolleyDraftDbContext
{
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await CanonicalizePendingLegacyBotMessagesAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The legacy bot path still constructs an outbound history row with a synthetic
    /// <c>bot:{guid}</c> message ID after the provider send has completed. Since the
    /// bridge records a short-lived receipt containing provider ID, parent inbound ID
    /// and a SHA-256 content fingerprint before control returns to that legacy path,
    /// we can replace the synthetic ID immediately before the row is persisted.
    ///
    /// Matching is deliberately fail-closed: exactly one provider receipt must match
    /// connection + group + a tracked parent inbound message + content fingerprint.
    /// Zero or multiple matches leave the synthetic ID untouched for the existing
    /// reconciliation/migration path instead of guessing.
    /// </summary>
    private async Task CanonicalizePendingLegacyBotMessagesAsync(CancellationToken cancellationToken)
    {
        var pending = ChangeTracker.Entries<ZaloGroupMessage>()
            .Where(entry =>
                entry.State == EntityState.Added &&
                entry.Entity.IsFromBot &&
                entry.Entity.MessageId.StartsWith("bot:", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(entry.Entity.ZaloConnectionId) &&
                !string.IsNullOrWhiteSpace(entry.Entity.GroupId))
            .ToArray();
        if (pending.Length == 0) return;

        var parentIdsByScope = ChangeTracker.Entries<ZaloGroupMessage>()
            .Where(entry =>
                entry.State is not EntityState.Detached and not EntityState.Deleted &&
                !entry.Entity.IsFromBot &&
                !string.IsNullOrWhiteSpace(entry.Entity.MessageId))
            .GroupBy(entry => new ScopeKey(entry.Entity.ZaloConnectionId, entry.Entity.GroupId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Entity.MessageId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());

        if (parentIdsByScope.Count == 0) return;

        var connection = Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        try
        {
            if (openedHere) await connection.OpenAsync(cancellationToken);

            foreach (var entry in pending)
            {
                var message = entry.Entity;
                var scope = new ScopeKey(message.ZaloConnectionId, message.GroupId);
                if (!parentIdsByScope.TryGetValue(scope, out var parentIds) || parentIds.Length == 0)
                    continue;

                var fingerprint = Fingerprint(message.Content);
                var providerIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parentId in parentIds)
                {
                    foreach (var providerId in await LoadMatchingProviderIdsAsync(
                                 connection,
                                 message.ZaloConnectionId,
                                 message.GroupId,
                                 parentId,
                                 fingerprint,
                                 cancellationToken))
                    {
                        providerIds.Add(providerId);
                        if (providerIds.Count > 1) break;
                    }

                    if (providerIds.Count > 1) break;
                }

                if (providerIds.Count != 1) continue;
                var providerMessageId = providerIds.Single();

                var trackedCanonical = ChangeTracker.Entries<ZaloGroupMessage>()
                    .Any(other =>
                        !ReferenceEquals(other.Entity, message) &&
                        other.State is not EntityState.Detached and not EntityState.Deleted &&
                        string.Equals(other.Entity.ZaloConnectionId, message.ZaloConnectionId, StringComparison.Ordinal) &&
                        string.Equals(other.Entity.MessageId, providerMessageId, StringComparison.Ordinal));
                if (trackedCanonical)
                {
                    entry.State = EntityState.Detached;
                    continue;
                }

                var persistedCanonical = await ZaloGroupMessages
                    .AsNoTracking()
                    .AnyAsync(item =>
                        item.ZaloConnectionId == message.ZaloConnectionId &&
                        item.MessageId == providerMessageId,
                        cancellationToken);
                if (persistedCanonical)
                {
                    entry.State = EntityState.Detached;
                    continue;
                }

                message.MessageId = providerMessageId;
                message.ObservationSource = "ProviderIdCanonicalized";
            }
        }
        catch (DbException)
        {
            // A missing/unavailable receipt table or transient receipt read is not a
            // reason to fail the domain save. Existing quote/retention reconciliation
            // can still canonicalize the legacy row later.
        }
        finally
        {
            if (openedHere && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> LoadMatchingProviderIdsAsync(
        DbConnection connection,
        string connectionId,
        string groupId,
        string parentMessageId,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ProviderMessageId"
            FROM "ZaloOutboundReceipts"
            WHERE "ZaloConnectionId" = @connectionId
              AND "GroupId" = @groupId
              AND "ParentMessageId" = @parentMessageId
              AND "ContentSha256" = @contentSha256
            LIMIT 2;
            """;
        Add(command, "@connectionId", connectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@parentMessageId", parentMessageId);
        Add(command, "@contentSha256", contentSha256);

        var rows = new List<string>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0));
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(value.Trim());
        }
        return rows;
    }

    private static string Fingerprint(string? content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private readonly record struct ScopeKey(string ZaloConnectionId, string GroupId);
}
