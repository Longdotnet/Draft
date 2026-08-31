using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloProactiveTarget(
    string ConnectionId,
    string GroupId,
    string AccountId);

/// <summary>
/// Resolves the groups that are allowed to participate in unsolicited NPC lanes.
///
/// ZaloTrackedGroups is the durable configuration source used by the Zalo group
/// settings UI. MatchSessions is kept only as a backwards-compatible fallback for
/// installations that predate tracked-group seeding. A configured group therefore
/// remains eligible for greetings/community hints even when it has no current match.
/// </summary>
internal sealed class ZaloProactiveTargetResolver(VolleyDraftDbContext db)
{
    public async Task<IReadOnlyList<ZaloProactiveTarget>> GetTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        await new ZaloAutoSessionStore(db).EnsureAsync(cancellationToken);

        var configured = await ReadConfiguredKeysAsync(cancellationToken);
        var legacy = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.BotEnabled &&
                session.ZaloConnectionId != null &&
                session.ZaloGroupId != null)
            .Select(session => new
            {
                ConnectionId = session.ZaloConnectionId!,
                GroupId = session.ZaloGroupId!
            })
            .ToListAsync(cancellationToken);

        var keys = configured
            .Concat(legacy.Select(item => new TargetKey(item.ConnectionId, item.GroupId)))
            .Select(item => new TargetKey(CleanId(item.ConnectionId), CleanId(item.GroupId)))
            .Where(item => item.ConnectionId.Length > 0 && item.GroupId.Length > 0)
            .Distinct()
            .ToList();
        if (keys.Count == 0) return [];

        var connectionIds = keys
            .Select(item => item.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var connections = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => connectionIds.Contains(item.Id) &&
                           item.Status == ZaloConnectionStatus.Connected)
            .Select(item => new { item.Id, item.AccountZaloId })
            .ToListAsync(cancellationToken);
        var accounts = connections
            .Where(item => CleanId(item.AccountZaloId).Length > 0)
            .ToDictionary(item => CleanId(item.Id), item => CleanId(item.AccountZaloId), StringComparer.Ordinal);

        return keys
            .Where(item => accounts.ContainsKey(item.ConnectionId))
            .Select(item => new ZaloProactiveTarget(
                item.ConnectionId,
                item.GroupId,
                accounts[item.ConnectionId]))
            .Distinct()
            .ToList();
    }

    private async Task<IReadOnlyList<TargetKey>> ReadConfiguredKeysAsync(
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(
            "SELECT \"ZaloConnectionId\", \"GroupId\" FROM \"ZaloTrackedGroups\" ORDER BY \"UpdatedAt\" DESC;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TargetKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TargetKey(
                Convert.ToString(reader["ZaloConnectionId"]) ?? string.Empty,
                Convert.ToString(reader["GroupId"]) ?? string.Empty));
        }
        return result;
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private sealed record TargetKey(string ConnectionId, string GroupId);
}
