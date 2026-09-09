using System.Data;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Persists an execution failure without allowing a stale/losing execution to downgrade
/// a proposal that another execution has already committed as Created.
///
/// The INSERT + guarded ON CONFLICT is intentionally one database statement so the invariant
/// holds across API instances; a read-then-write check would reopen the same race.
/// </summary>
internal static class ZaloAutoSessionProposalFailurePersistence
{
    public static async Task PersistUnlessCreatedAsync(
        VolleyDraftDbContext db,
        ZaloAutoSessionStore store,
        ZaloPollSessionProposalData proposal,
        CancellationToken cancellationToken = default)
    {
        await store.EnsureAsync(cancellationToken);

        proposal.Id = string.IsNullOrWhiteSpace(proposal.Id)
            ? Guid.NewGuid().ToString("n")
            : proposal.Id;
        proposal.CreatedAt = proposal.CreatedAt == default
            ? DateTimeOffset.UtcNow
            : proposal.CreatedAt;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;

        const string sql = """
            INSERT INTO "ZaloPollSessionProposals" (
                "Id", "TrackedGroupId", "PollId", "PollQuestion", "PollCreatorId", "PollUpdatedAtUnixMs",
                "PollStructureHash", "CandidatesJson", "ClassifierConfidence", "ClassifierReason", "Status",
                "ProposalMessageId", "ApprovedByZaloUserId", "ApprovedAt", "LastError", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @TrackedGroupId, @PollId, @PollQuestion, @PollCreatorId, @PollUpdatedAtUnixMs,
                @PollStructureHash, @CandidatesJson, @ClassifierConfidence, @ClassifierReason, @Status,
                @ProposalMessageId, @ApprovedByZaloUserId, @ApprovedAt, @LastError, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("TrackedGroupId", "PollId") DO UPDATE SET
                "Status" = excluded."Status",
                "ApprovedByZaloUserId" = excluded."ApprovedByZaloUserId",
                "ApprovedAt" = excluded."ApprovedAt",
                "LastError" = excluded."LastError",
                "UpdatedAt" = excluded."UpdatedAt"
            WHERE "ZaloPollSessionProposals"."Status" <> 'Created';
            """;

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            AddParameter(command, "@Id", proposal.Id);
            AddParameter(command, "@TrackedGroupId", proposal.TrackedGroupId);
            AddParameter(command, "@PollId", proposal.PollId);
            AddParameter(command, "@PollQuestion", proposal.PollQuestion);
            AddParameter(command, "@PollCreatorId", proposal.PollCreatorId);
            AddParameter(command, "@PollUpdatedAtUnixMs", proposal.PollUpdatedAtUnixMs);
            AddParameter(command, "@PollStructureHash", proposal.PollStructureHash);
            AddParameter(command, "@CandidatesJson", proposal.CandidatesJson);
            AddParameter(command, "@ClassifierConfidence", proposal.ClassifierConfidence);
            AddParameter(command, "@ClassifierReason", proposal.ClassifierReason);
            AddParameter(command, "@Status", ZaloPollSessionProposalStatus.Failed.ToString());
            AddParameter(command, "@ProposalMessageId", proposal.ProposalMessageId);
            AddParameter(command, "@ApprovedByZaloUserId", proposal.ApprovedByZaloUserId);
            AddParameter(command, "@ApprovedAt", FormatDate(proposal.ApprovedAt));
            AddParameter(command, "@LastError", proposal.LastError);
            AddParameter(command, "@CreatedAt", FormatDate(proposal.CreatedAt));
            AddParameter(command, "@UpdatedAt", FormatDate(proposal.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? FormatDate(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
