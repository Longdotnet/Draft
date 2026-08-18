using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionObservabilityStore(VolleyDraftDbContext db)
{
    private readonly ZaloAutoSessionStore baseStore = new(db);

    public async Task<IReadOnlyList<ZaloPollSessionProposalData>> GetProposalsAsync(
        string adminUserId,
        string trackedGroupId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await baseStore.EnsureAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 100);
        const string sql = """
            SELECT p.*
            FROM "ZaloPollSessionProposals" p
            INNER JOIN "ZaloTrackedGroups" g ON g."Id" = p."TrackedGroupId"
            WHERE p."TrackedGroupId" = @TrackedGroupId
              AND g."AdminUserId" = @AdminUserId
            ORDER BY p."UpdatedAt" DESC
            LIMIT @Limit;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@AdminUserId", adminUserId);
        AddParameter(command, "@Limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloPollSessionProposalData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadProposal(reader));
        return result;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionLinkData>> GetLinksAsync(
        string adminUserId,
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await baseStore.EnsureAsync(cancellationToken);
        const string sql = """
            SELECT l.*
            FROM "ZaloAutoSessionLinks" l
            INNER JOIN "ZaloTrackedGroups" g ON g."Id" = l."TrackedGroupId"
            WHERE l."TrackedGroupId" = @TrackedGroupId
              AND g."AdminUserId" = @AdminUserId
            ORDER BY l."CreatedAt" DESC;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@AdminUserId", adminUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionLinkData>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloAutoSessionLinkData(
                ReadString(reader, "Id") ?? string.Empty,
                ReadString(reader, "TrackedGroupId") ?? string.Empty,
                ReadString(reader, "PollId") ?? string.Empty,
                ReadString(reader, "OptionId") ?? string.Empty,
                ReadString(reader, "SessionId") ?? string.Empty,
                ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue));
        }
        return result;
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ZaloPollSessionProposalData ReadProposal(DbDataReader reader) => new()
    {
        Id = ReadString(reader, "Id") ?? string.Empty,
        TrackedGroupId = ReadString(reader, "TrackedGroupId") ?? string.Empty,
        PollId = ReadString(reader, "PollId") ?? string.Empty,
        PollQuestion = ReadString(reader, "PollQuestion") ?? string.Empty,
        PollCreatorId = ReadString(reader, "PollCreatorId") ?? string.Empty,
        PollUpdatedAtUnixMs = ReadLong(reader, "PollUpdatedAtUnixMs"),
        PollStructureHash = ReadString(reader, "PollStructureHash") ?? string.Empty,
        CandidatesJson = ReadString(reader, "CandidatesJson") ?? "[]",
        ClassifierConfidence = ReadDouble(reader, "ClassifierConfidence"),
        ClassifierReason = ReadString(reader, "ClassifierReason") ?? string.Empty,
        Status = Enum.TryParse<ZaloPollSessionProposalStatus>(ReadString(reader, "Status"), true, out var status)
            ? status
            : ZaloPollSessionProposalStatus.Failed,
        ProposalMessageId = ReadString(reader, "ProposalMessageId"),
        ApprovedByZaloUserId = ReadString(reader, "ApprovedByZaloUserId"),
        ApprovedAt = ReadDate(reader, "ApprovedAt"),
        LastError = ReadString(reader, "LastError"),
        CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue,
        UpdatedAt = ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue
    };

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static long ReadLong(DbDataReader reader, string name, long fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(DbDataReader reader, string name, double fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }
}
