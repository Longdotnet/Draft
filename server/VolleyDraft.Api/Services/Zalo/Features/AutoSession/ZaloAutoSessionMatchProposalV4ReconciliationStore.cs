using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionMatchProposalV4SourceState(
    ZaloAutoSessionConversationDraft Draft,
    string StructureHash);

/// <summary>
/// Persists authoritative poll-source reconciliation as a V4 proposal revision.
/// Conversation V3 may cache the current draft, but this ledger remains the restart-safe
/// source for both the organizer draft and the poll baseline used by the next three-way merge.
/// </summary>
internal sealed class ZaloAutoSessionMatchProposalV4ReconciliationStore(VolleyDraftDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private readonly ZaloAutoSessionMatchProposalV4Store revisions = new(db);

    public async Task<ZaloAutoSessionMatchProposalV4SourceState?> LoadSourceAsync(
        string proposalId,
        string fallbackSourceDraftJson,
        CancellationToken cancellationToken = default)
    {
        var latest = await revisions.GetLatestAsync(proposalId, cancellationToken);
        if (latest is null)
        {
            var fallback = DeserializeDraft(fallbackSourceDraftJson);
            return fallback is null ? null : new(fallback, string.Empty);
        }

        var evidence = ParseObject(latest.EvidenceJson);
        var sourceJson = evidence?["sourceDraftJson"]?.GetValue<string>();
        var source = DeserializeDraft(sourceJson) ?? DeserializeDraft(fallbackSourceDraftJson);
        return source is null ? null : new(source, latest.SourcePollStructureHash);
    }

    public async Task<ZaloAutoSessionMatchProposalV4WriteResult?> AppendReconciliationAsync(
        ZaloAutoSessionConversationData conversation,
        BridgePoll currentPoll,
        ZaloAutoSessionConversationDraft previousSource,
        ZaloAutoSessionPollRevalidationV4 revalidation,
        string actorZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = NormalizeDraft(revalidation.CurrentSourceDraft);
        var normalizedDraft = NormalizeDraft(revalidation.Reconciliation.Draft);
        if (normalizedSource is null || normalizedDraft is null) return null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var latest = await revisions.GetLatestAsync(conversation.ProposalId, cancellationToken);
            if (latest is null) return null;
            if (conversation.Version <= latest.ConversationVersion)
                return new ZaloAutoSessionMatchProposalV4WriteResult(
                    latest,
                    false,
                    "stale_or_conflicting_conversation_version");

            var evidence = BuildEvidence(
                latest,
                currentPoll,
                previousSource,
                revalidation.CurrentSourceDraft,
                revalidation.Reconciliation.Draft,
                normalizedSource.Value.Json,
                revalidation.CurrentStructureHash,
                actorZaloUserId);
            var next = new ZaloAutoSessionMatchProposalV4Revision(
                Guid.NewGuid().ToString("n"),
                latest.ProposalId,
                latest.TrackedGroupId,
                latest.PollId,
                latest.Revision + 1,
                conversation.Version,
                currentPoll.Question,
                currentPoll.UpdatedAtUnixMs,
                revalidation.CurrentStructureHash,
                normalizedDraft.Value.Json,
                evidence.ToJsonString(JsonOptions),
                normalizedDraft.Value.Fingerprint,
                CleanOptional(actorZaloUserId, 120),
                "poll_reconciliation",
                DateTimeOffset.UtcNow);

            if (await TryInsertAsync(next, cancellationToken))
                return new ZaloAutoSessionMatchProposalV4WriteResult(next, true, "source_reconciled");
        }

        var current = await revisions.GetLatestAsync(conversation.ProposalId, cancellationToken);
        return current is null
            ? null
            : new ZaloAutoSessionMatchProposalV4WriteResult(current, false, "revision_contention");
    }

    private static JsonObject BuildEvidence(
        ZaloAutoSessionMatchProposalV4Revision latest,
        BridgePoll currentPoll,
        ZaloAutoSessionConversationDraft previousSource,
        ZaloAutoSessionConversationDraft currentSource,
        ZaloAutoSessionConversationDraft reconciled,
        string sourceDraftJson,
        string structureHash,
        string actorZaloUserId)
    {
        var evidence = ParseObject(latest.EvidenceJson) ?? new JsonObject();
        evidence["schemaVersion"] = 4;
        evidence["pollId"] = latest.PollId;
        evidence["pollStructureHash"] = structureHash;
        evidence["sourceDraftJson"] = sourceDraftJson;

        RefreshApprovedPolicyEvidence(evidence, previousSource, currentSource, reconciled);

        var oldIdentity = evidence["optionIdentity"] as JsonObject;
        var oldStarts = evidence["startTimes"] as JsonObject;
        var oldSelections = evidence["selections"] as JsonObject;
        var previousById = previousSource.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        var reconciledById = reconciled.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);

        var identities = new JsonObject();
        var starts = new JsonObject();
        var selections = new JsonObject();
        foreach (var current in currentSource.Items)
        {
            identities[current.OptionId] = Evidence("poll_option", current.OptionContent);

            var sourceTimeUnchanged = previousById.TryGetValue(current.OptionId, out var previous) &&
                                      previous.StartTime == current.StartTime;
            var organizerTimePreserved = reconciledById.TryGetValue(current.OptionId, out var reconciledItem) &&
                                         reconciledItem.StartTime != current.StartTime;
            if ((sourceTimeUnchanged || organizerTimePreserved) && oldStarts?[current.OptionId] is JsonNode oldStart)
            {
                starts[current.OptionId] = oldStart.DeepClone();
            }
            else
            {
                starts[current.OptionId] = BuildCurrentStartTimeEvidence(currentPoll, current);
            }

            if (oldSelections?[current.OptionId] is JsonNode oldSelection)
                selections[current.OptionId] = oldSelection.DeepClone();
            else
            {
                var selected = reconciledById.TryGetValue(current.OptionId, out var item) && item.Selected;
                selections[current.OptionId] = new JsonObject
                {
                    ["source"] = "poll_reconciliation_default_unselected",
                    ["actorZaloUserId"] = CleanOptional(actorZaloUserId, 120),
                    ["intent"] = "poll_reconciliation",
                    ["detail"] = selected ? "true" : "false"
                };
            }
        }

        evidence["optionIdentity"] = identities;
        evidence["startTimes"] = starts;
        evidence["selections"] = selections;
        return evidence;
    }

    private static void RefreshApprovedPolicyEvidence(
        JsonObject evidence,
        ZaloAutoSessionConversationDraft previousSource,
        ZaloAutoSessionConversationDraft currentSource,
        ZaloAutoSessionConversationDraft reconciled)
    {
        var locationPolicyChanged = !string.Equals(
            previousSource.Location,
            currentSource.Location,
            StringComparison.Ordinal);
        var organizerLocationPreserved = !string.Equals(
            reconciled.Location,
            currentSource.Location,
            StringComparison.Ordinal);
        if (locationPolicyChanged && !organizerLocationPreserved)
        {
            evidence["location"] = Evidence(
                "approved_group_default",
                $"ZaloTrackedGroups.DefaultLocation={currentSource.Location?.Trim() ?? string.Empty}");
        }

        var teamSizePolicyChanged = previousSource.TeamSize != currentSource.TeamSize;
        var organizerTeamSizePreserved = reconciled.TeamSize != currentSource.TeamSize;
        if (teamSizePolicyChanged && !organizerTeamSizePreserved)
        {
            evidence["teamSize"] = Evidence(
                "approved_group_default",
                $"ZaloTrackedGroups.DefaultTeamSize={currentSource.TeamSize.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static JsonObject BuildCurrentStartTimeEvidence(
        BridgePoll currentPoll,
        ZaloAutoSessionConversationDraftItem current)
    {
        var option = currentPoll.Options.FirstOrDefault(item =>
            string.Equals(item.Id, current.OptionId, StringComparison.Ordinal));
        if (option is not null && ZaloSessionResolver.ContainsExplicitSessionTime(option.Content ?? string.Empty))
        {
            return Evidence(
                "poll_option_explicit_time",
                current.StartTime.ToString("O", CultureInfo.InvariantCulture));
        }

        if (ZaloSessionResolver.ContainsExplicitSessionTime(currentPoll.Question ?? string.Empty))
        {
            return Evidence(
                "poll_title_explicit_time",
                current.StartTime.ToString("O", CultureInfo.InvariantCulture));
        }

        var local = current.StartTime.ToOffset(VietnamOffset);
        var minutes = local.Hour * 60 + local.Minute;
        return Evidence(
            "approved_group_default",
            $"ZaloTrackedGroups.DefaultStartMinutes={minutes.ToString(CultureInfo.InvariantCulture)}");
    }

    private static JsonObject Evidence(string source, string detail) => new()
    {
        ["source"] = source,
        ["detail"] = detail
    };

    private async Task<bool> TryInsertAsync(
        ZaloAutoSessionMatchProposalV4Revision revision,
        CancellationToken cancellationToken)
    {
        await revisions.EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloAutoSessionMatchProposalRevisionsV4" (
                "Id", "ProposalId", "TrackedGroupId", "PollId", "Revision", "ConversationVersion",
                "SourcePollQuestion", "SourcePollUpdatedAtUnixMs", "SourcePollStructureHash",
                "DraftJson", "EvidenceJson", "DraftFingerprint", "ActorZaloUserId", "ChangeKind", "CreatedAt")
            VALUES (
                @Id, @ProposalId, @TrackedGroupId, @PollId, @Revision, @ConversationVersion,
                @SourcePollQuestion, @SourcePollUpdatedAtUnixMs, @SourcePollStructureHash,
                @DraftJson, @EvidenceJson, @DraftFingerprint, @ActorZaloUserId, @ChangeKind, @CreatedAt)
            ON CONFLICT ("ProposalId", "Revision") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", revision.Id);
        AddParameter(command, "@ProposalId", revision.ProposalId);
        AddParameter(command, "@TrackedGroupId", revision.TrackedGroupId);
        AddParameter(command, "@PollId", revision.PollId);
        AddParameter(command, "@Revision", revision.Revision);
        AddParameter(command, "@ConversationVersion", revision.ConversationVersion);
        AddParameter(command, "@SourcePollQuestion", revision.SourcePollQuestion);
        AddParameter(command, "@SourcePollUpdatedAtUnixMs", revision.SourcePollUpdatedAtUnixMs);
        AddParameter(command, "@SourcePollStructureHash", revision.SourcePollStructureHash);
        AddParameter(command, "@DraftJson", revision.DraftJson);
        AddParameter(command, "@EvidenceJson", revision.EvidenceJson);
        AddParameter(command, "@DraftFingerprint", revision.DraftFingerprint);
        AddParameter(command, "@ActorZaloUserId", revision.ActorZaloUserId);
        AddParameter(command, "@ChangeKind", revision.ChangeKind);
        AddParameter(command, "@CreatedAt", revision.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
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

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static ZaloAutoSessionConversationDraft? DeserializeDraft(string? json)
    {
        try { return JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json ?? string.Empty, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static (string Json, string Fingerprint)? NormalizeDraft(ZaloAutoSessionConversationDraft draft)
    {
        if (draft.Items.Count == 0 || draft.Items.Any(item => string.IsNullOrWhiteSpace(item.OptionId))) return null;
        if (draft.Items.Select(item => item.OptionId).Distinct(StringComparer.Ordinal).Count() != draft.Items.Count) return null;
        var json = JsonSerializer.Serialize(draft, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return (json, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
