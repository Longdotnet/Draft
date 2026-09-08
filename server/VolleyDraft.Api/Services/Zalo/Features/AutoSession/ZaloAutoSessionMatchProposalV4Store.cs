using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionMatchProposalV4Revision(
    string Id,
    string ProposalId,
    string TrackedGroupId,
    string PollId,
    int Revision,
    int ConversationVersion,
    string SourcePollQuestion,
    long SourcePollUpdatedAtUnixMs,
    string SourcePollStructureHash,
    string DraftJson,
    string EvidenceJson,
    string DraftFingerprint,
    string? ActorZaloUserId,
    string ChangeKind,
    DateTimeOffset CreatedAt);

internal sealed record ZaloAutoSessionMatchProposalV4WriteResult(
    ZaloAutoSessionMatchProposalV4Revision Revision,
    bool Accepted,
    string Reason);

internal sealed class ZaloAutoSessionProposalEvidenceValueV4
{
    public string Source { get; set; } = string.Empty;
    public string? ActorZaloUserId { get; set; }
    public string? Intent { get; set; }
    public string? Detail { get; set; }
}

internal sealed class ZaloAutoSessionProposalEvidenceV4
{
    public int SchemaVersion { get; set; } = 4;
    public string PollId { get; set; } = string.Empty;
    public string PollStructureHash { get; set; } = string.Empty;
    public ZaloAutoSessionProposalEvidenceValueV4 Location { get; set; } = new();
    public ZaloAutoSessionProposalEvidenceValueV4 TeamSize { get; set; } = new();
    public Dictionary<string, ZaloAutoSessionProposalEvidenceValueV4> OptionIdentity { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ZaloAutoSessionProposalEvidenceValueV4> StartTimes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ZaloAutoSessionProposalEvidenceValueV4> Selections { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Auto Session V4 durable MatchProposal revision ledger.
///
/// Conversation V3 remains the compatibility UI/state machine, but mutable match-birth
/// decisions are mirrored here as monotonic revisions with field provenance. The ledger
/// is DB-backed, model-independent and append-only so a restart can reconstruct the latest
/// organizer-approved draft without trusting process memory or AI output.
/// </summary>
internal sealed class ZaloAutoSessionMatchProposalV4Store(VolleyDraftDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionMatchProposalRevisionsV4" (
                "Id" TEXT PRIMARY KEY,
                "ProposalId" TEXT NOT NULL,
                "TrackedGroupId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL,
                "ConversationVersion" INTEGER NOT NULL DEFAULT 0,
                "SourcePollQuestion" TEXT NOT NULL,
                "SourcePollUpdatedAtUnixMs" BIGINT NOT NULL DEFAULT 0,
                "SourcePollStructureHash" TEXT NOT NULL,
                "DraftJson" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "DraftFingerprint" TEXT NOT NULL,
                "ActorZaloUserId" TEXT NULL,
                "ChangeKind" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                UNIQUE ("ProposalId", "Revision")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionMatchProposalV4_Latest"
                ON "ZaloAutoSessionMatchProposalRevisionsV4" ("ProposalId", "Revision" DESC);
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionMatchProposalV4_Poll"
                ON "ZaloAutoSessionMatchProposalRevisionsV4" ("TrackedGroupId", "PollId", "Revision" DESC);
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<ZaloAutoSessionMatchProposalV4Revision?> GetLatestAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionMatchProposalRevisionsV4\" WHERE \"ProposalId\" = @ProposalId ORDER BY \"Revision\" DESC LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ProposalId", Clean(proposalId, 120));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<ZaloAutoSessionMatchProposalV4WriteResult?> InitializeFromPreviewAsync(
        ZaloPollSessionProposalData proposal,
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationData conversation,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetLatestAsync(proposal.Id, cancellationToken);
        if (existing is not null)
            return new ZaloAutoSessionMatchProposalV4WriteResult(existing, true, "already_initialized");

        var normalized = NormalizeDraft(conversation.InitialDraftJson);
        if (normalized is null) return null;

        var evidence = BuildInitialEvidence(proposal, tracked, normalized.Value.Draft);
        var revision = BuildRevision(
            proposal,
            revision: 1,
            conversationVersion: 0,
            normalized.Value.Json,
            JsonSerializer.Serialize(evidence, JsonOptions),
            normalized.Value.Fingerprint,
            conversation.OriginalOrganizerId,
            "preview");

        var inserted = await TryInsertAsync(revision, cancellationToken);
        if (inserted)
            return new ZaloAutoSessionMatchProposalV4WriteResult(revision, true, "initialized");

        existing = await GetLatestAsync(proposal.Id, cancellationToken);
        return existing is null
            ? null
            : new ZaloAutoSessionMatchProposalV4WriteResult(existing, true, "initialized_by_concurrent_writer");
    }

    public async Task<ZaloAutoSessionMatchProposalV4WriteResult?> SaveConversationDraftAsync(
        ZaloAutoSessionConversationData conversation,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(conversation.ProposalId, cancellationToken);
        if (latest is null)
        {
            var autoSessionStore = new ZaloAutoSessionStore(db);
            var proposal = await autoSessionStore.GetProposalAsync(
                conversation.TrackedGroupId,
                conversation.PollId,
                cancellationToken);
            var tracked = await autoSessionStore.GetTrackedGroupAsync(
                conversation.TrackedGroupId,
                cancellationToken);
            if (proposal is null || tracked is null) return null;

            var initialized = await InitializeFromPreviewAsync(proposal, tracked, conversation, cancellationToken);
            latest = initialized?.Revision;
            if (latest is null) return null;
        }

        var normalized = NormalizeDraft(conversation.DraftJson);
        if (normalized is null)
            return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "invalid_draft_json");

        if (string.Equals(latest.DraftFingerprint, normalized.Value.Fingerprint, StringComparison.Ordinal))
            return new ZaloAutoSessionMatchProposalV4WriteResult(latest, true, "unchanged");

        var previousDraft = NormalizeDraft(latest.DraftJson);
        if (previousDraft is null)
            return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "latest_revision_invalid");

        if (!HasSameAuthoritativeOptionIdentity(previousDraft.Value.Draft, normalized.Value.Draft))
            return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "authoritative_option_identity_changed");

        if (conversation.Version <= latest.ConversationVersion)
            return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "stale_or_conflicting_conversation_version");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            latest = await GetLatestAsync(conversation.ProposalId, cancellationToken) ?? latest;
            if (string.Equals(latest.DraftFingerprint, normalized.Value.Fingerprint, StringComparison.Ordinal))
                return new ZaloAutoSessionMatchProposalV4WriteResult(latest, true, "already_persisted");
            if (conversation.Version <= latest.ConversationVersion)
                return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "stale_or_conflicting_conversation_version");

            previousDraft = NormalizeDraft(latest.DraftJson);
            if (previousDraft is null ||
                !HasSameAuthoritativeOptionIdentity(previousDraft.Value.Draft, normalized.Value.Draft))
                return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "authoritative_option_identity_changed");

            var evidence = EvolveEvidence(
                latest,
                previousDraft.Value.Draft,
                normalized.Value.Draft,
                conversation.ActiveOrganizerId,
                conversation.LastIntent);
            var next = new ZaloAutoSessionMatchProposalV4Revision(
                Guid.NewGuid().ToString("n"),
                latest.ProposalId,
                latest.TrackedGroupId,
                latest.PollId,
                latest.Revision + 1,
                conversation.Version,
                latest.SourcePollQuestion,
                latest.SourcePollUpdatedAtUnixMs,
                latest.SourcePollStructureHash,
                normalized.Value.Json,
                JsonSerializer.Serialize(evidence, JsonOptions),
                normalized.Value.Fingerprint,
                CleanOptional(conversation.ActiveOrganizerId, 120),
                Clean(conversation.LastIntent, 120, "conversation_update"),
                DateTimeOffset.UtcNow);

            if (await TryInsertAsync(next, cancellationToken))
                return new ZaloAutoSessionMatchProposalV4WriteResult(next, true, "revision_appended");
        }

        latest = await GetLatestAsync(conversation.ProposalId, cancellationToken) ?? latest;
        return new ZaloAutoSessionMatchProposalV4WriteResult(latest, false, "revision_contention");
    }

    public async Task<ZaloAutoSessionConversationData> HydrateDraftAsync(
        ZaloAutoSessionConversationData conversation,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(conversation.ProposalId, cancellationToken);
        if (latest is null)
        {
            var initialized = await SaveConversationDraftAsync(conversation, cancellationToken);
            latest = initialized?.Revision;
        }

        if (latest is not null)
            conversation.DraftJson = latest.DraftJson;
        return conversation;
    }

    private static ZaloAutoSessionMatchProposalV4Revision BuildRevision(
        ZaloPollSessionProposalData proposal,
        int revision,
        int conversationVersion,
        string draftJson,
        string evidenceJson,
        string fingerprint,
        string? actorZaloUserId,
        string changeKind) => new(
            Guid.NewGuid().ToString("n"),
            proposal.Id,
            proposal.TrackedGroupId,
            proposal.PollId,
            revision,
            conversationVersion,
            proposal.PollQuestion,
            proposal.PollUpdatedAtUnixMs,
            proposal.PollStructureHash,
            draftJson,
            evidenceJson,
            fingerprint,
            CleanOptional(actorZaloUserId, 120),
            Clean(changeKind, 120, "preview"),
            DateTimeOffset.UtcNow);

    private static ZaloAutoSessionProposalEvidenceV4 BuildInitialEvidence(
        ZaloPollSessionProposalData proposal,
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationDraft draft)
    {
        var evidence = new ZaloAutoSessionProposalEvidenceV4
        {
            PollId = proposal.PollId,
            PollStructureHash = proposal.PollStructureHash,
            Location = BuildInitialLocationEvidence(tracked, draft),
            TeamSize = BuildInitialTeamSizeEvidence(tracked, draft)
        };

        foreach (var item in draft.Items)
        {
            evidence.OptionIdentity[item.OptionId] = new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "poll_option",
                Detail = item.OptionContent
            };
            evidence.StartTimes[item.OptionId] = BuildInitialStartTimeEvidence(proposal, tracked, item);
            evidence.Selections[item.OptionId] = new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "poll_option_default_selected",
                Detail = "true"
            };
        }

        return evidence;
    }

    private static ZaloAutoSessionProposalEvidenceValueV4 BuildInitialLocationEvidence(
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationDraft draft)
    {
        var draftLocation = draft.Location?.Trim() ?? string.Empty;
        var currentApprovedLocation = tracked.DefaultLocation?.Trim() ?? string.Empty;
        if (draftLocation.Length == 0)
            return new ZaloAutoSessionProposalEvidenceValueV4 { Source = "missing" };
        if (currentApprovedLocation.Length > 0 &&
            string.Equals(draftLocation, currentApprovedLocation, StringComparison.Ordinal))
        {
            return new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "approved_group_default",
                Detail = "ZaloTrackedGroups.DefaultLocation"
            };
        }

        return StaleDefaultEvidence(
            $"draft={draftLocation}; current={currentApprovedLocation}");
    }

    private static ZaloAutoSessionProposalEvidenceValueV4 BuildInitialTeamSizeEvidence(
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationDraft draft)
    {
        if (draft.TeamSize == tracked.DefaultTeamSize)
        {
            return new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "approved_group_default",
                Detail = "ZaloTrackedGroups.DefaultTeamSize"
            };
        }

        return StaleDefaultEvidence(
            $"draft={draft.TeamSize.ToString(CultureInfo.InvariantCulture)}; current={tracked.DefaultTeamSize.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ZaloAutoSessionProposalEvidenceValueV4 BuildInitialStartTimeEvidence(
        ZaloPollSessionProposalData proposal,
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionConversationDraftItem item)
    {
        if (ZaloSessionResolver.ContainsExplicitSessionTime(item.OptionContent ?? string.Empty))
        {
            return new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "poll_option_explicit_time",
                Detail = item.StartTime.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        if (ZaloSessionResolver.ContainsExplicitSessionTime(proposal.PollQuestion ?? string.Empty))
        {
            return new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "poll_title_explicit_time",
                Detail = item.StartTime.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        var local = item.StartTime.ToOffset(VietnamOffset);
        var draftMinutes = local.Hour * 60 + local.Minute;
        if (draftMinutes == tracked.DefaultStartMinutes)
        {
            return new ZaloAutoSessionProposalEvidenceValueV4
            {
                Source = "approved_group_default",
                Detail = $"ZaloTrackedGroups.DefaultStartMinutes={tracked.DefaultStartMinutes.ToString(CultureInfo.InvariantCulture)}"
            };
        }

        return StaleDefaultEvidence(
            $"draft={draftMinutes.ToString(CultureInfo.InvariantCulture)}; current={tracked.DefaultStartMinutes.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ZaloAutoSessionProposalEvidenceValueV4 StaleDefaultEvidence(string detail) => new()
    {
        Source = "stale_or_unapproved_default",
        Detail = detail
    };

    private static ZaloAutoSessionProposalEvidenceV4 EvolveEvidence(
        ZaloAutoSessionMatchProposalV4Revision latest,
        ZaloAutoSessionConversationDraft previous,
        ZaloAutoSessionConversationDraft current,
        string? actorZaloUserId,
        string? intent)
    {
        var evidence = DeserializeEvidence(latest.EvidenceJson) ?? BuildFallbackEvidence(latest, previous);
        var actor = CleanOptional(actorZaloUserId, 120);
        var cleanIntent = CleanOptional(intent, 120);

        if (!string.Equals(previous.Location, current.Location, StringComparison.Ordinal))
            evidence.Location = OrganizerEvidence(actor, cleanIntent, current.Location);
        if (previous.TeamSize != current.TeamSize)
            evidence.TeamSize = OrganizerEvidence(actor, cleanIntent, current.TeamSize.ToString(CultureInfo.InvariantCulture));

        var previousById = previous.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        foreach (var item in current.Items)
        {
            var old = previousById[item.OptionId];
            if (old.StartTime != item.StartTime)
                evidence.StartTimes[item.OptionId] = OrganizerEvidence(actor, cleanIntent, item.StartTime.ToString("O", CultureInfo.InvariantCulture));
            if (old.Selected != item.Selected)
                evidence.Selections[item.OptionId] = OrganizerEvidence(actor, cleanIntent, item.Selected ? "true" : "false");
        }

        return evidence;
    }

    private static ZaloAutoSessionProposalEvidenceV4 BuildFallbackEvidence(
        ZaloAutoSessionMatchProposalV4Revision latest,
        ZaloAutoSessionConversationDraft draft)
    {
        var evidence = new ZaloAutoSessionProposalEvidenceV4
        {
            PollId = latest.PollId,
            PollStructureHash = latest.SourcePollStructureHash,
            Location = new ZaloAutoSessionProposalEvidenceValueV4 { Source = "persisted_v4_revision" },
            TeamSize = new ZaloAutoSessionProposalEvidenceValueV4 { Source = "persisted_v4_revision" }
        };
        foreach (var item in draft.Items)
        {
            evidence.OptionIdentity[item.OptionId] = new ZaloAutoSessionProposalEvidenceValueV4 { Source = "poll_option", Detail = item.OptionContent };
            evidence.StartTimes[item.OptionId] = new ZaloAutoSessionProposalEvidenceValueV4 { Source = "persisted_v4_revision" };
            evidence.Selections[item.OptionId] = new ZaloAutoSessionProposalEvidenceValueV4 { Source = "persisted_v4_revision" };
        }
        return evidence;
    }

    private static ZaloAutoSessionProposalEvidenceValueV4 OrganizerEvidence(string? actor, string? intent, string? detail) => new()
    {
        Source = "organizer_correction",
        ActorZaloUserId = actor,
        Intent = intent,
        Detail = detail
    };

    private static ZaloAutoSessionProposalEvidenceV4? DeserializeEvidence(string json)
    {
        try { return JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool HasSameAuthoritativeOptionIdentity(
        ZaloAutoSessionConversationDraft previous,
        ZaloAutoSessionConversationDraft current)
    {
        if (previous.Items.Count != current.Items.Count) return false;
        var currentById = current.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        foreach (var old in previous.Items)
        {
            if (!currentById.TryGetValue(old.OptionId, out var item)) return false;
            if (!string.Equals(old.OptionContent, item.OptionContent, StringComparison.Ordinal)) return false;
            if (!string.Equals(old.DayKey, item.DayKey, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static (ZaloAutoSessionConversationDraft Draft, string Json, string Fingerprint)? NormalizeDraft(string? json)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json ?? string.Empty, JsonOptions);
            if (draft is null || draft.Items is null || draft.Items.Count == 0) return null;
            if (draft.Items.Any(item => string.IsNullOrWhiteSpace(item.OptionId))) return null;
            if (draft.Items.Select(item => item.OptionId).Distinct(StringComparer.Ordinal).Count() != draft.Items.Count) return null;
            var canonical = JsonSerializer.Serialize(draft, JsonOptions);
            return (draft, canonical, Fingerprint(canonical));
        }
        catch (JsonException) { return null; }
    }

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<bool> TryInsertAsync(ZaloAutoSessionMatchProposalV4Revision revision, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
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
        if (db.Database.CurrentTransaction is { } transaction) command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static ZaloAutoSessionMatchProposalV4Revision ReadRevision(DbDataReader reader) => new(
        ReadString(reader, "Id") ?? string.Empty,
        ReadString(reader, "ProposalId") ?? string.Empty,
        ReadString(reader, "TrackedGroupId") ?? string.Empty,
        ReadString(reader, "PollId") ?? string.Empty,
        ReadInt(reader, "Revision"),
        ReadInt(reader, "ConversationVersion"),
        ReadString(reader, "SourcePollQuestion") ?? string.Empty,
        ReadLong(reader, "SourcePollUpdatedAtUnixMs"),
        ReadString(reader, "SourcePollStructureHash") ?? string.Empty,
        ReadString(reader, "DraftJson") ?? "{}",
        ReadString(reader, "EvidenceJson") ?? "{}",
        ReadString(reader, "DraftFingerprint") ?? string.Empty,
        ReadString(reader, "ActorZaloUserId"),
        ReadString(reader, "ChangeKind") ?? string.Empty,
        ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static long ReadLong(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }

    private static string Clean(string? value, int maxLength, string fallback = "")
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) text = fallback;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }
}
