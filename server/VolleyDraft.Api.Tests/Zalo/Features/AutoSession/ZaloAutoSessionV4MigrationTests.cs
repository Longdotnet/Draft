using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionV4MigrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExistingModifiedV3Conversation_LazilyCreatesBaselineThenCurrentV4Revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);

        var tracked = await new ZaloAutoSessionSettingsStore(db).InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            Id = "tracked-old",
            AdminUserId = "admin-1",
            ZaloConnectionId = "connection-1",
            GroupId = "group-1",
            GroupName = "UTE Volley",
            DefaultTeamSize = 6,
            DefaultLocation = "Sân UTE"
        });
        var proposal = await new ZaloAutoSessionStore(db).UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            Id = "proposal-old",
            TrackedGroupId = tracked.Id,
            PollId = "poll-old",
            PollQuestion = "Vote sân UTE tuần sau",
            PollCreatorId = "leader-1",
            PollUpdatedAtUnixMs = 1788750000000,
            PollStructureHash = "hash-old",
            CandidatesJson = "[]",
            ClassifierConfidence = 1,
            ClassifierReason = "deterministic",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval,
            ProposalMessageId = "preview-old"
        });

        var initial = Draft("Sân UTE", 17);
        var modified = Draft("Sân A", 18);
        var conversations = new ZaloAutoSessionConversationStore(db);
        var legacy = await conversations.CreateIfMissingAsync(new ZaloAutoSessionConversationData
        {
            Id = "conversation-old",
            ProposalId = proposal.Id,
            TrackedGroupId = tracked.Id,
            PollId = proposal.PollId,
            GroupId = tracked.GroupId,
            OriginalOrganizerId = "leader-1",
            ActiveOrganizerId = "leader-1",
            State = ZaloAutoSessionConversationState.ReadyToConfirm,
            InitialDraftJson = initial,
            DraftJson = modified,
            PreviewMessageId = "preview-old",
            CurrentBotMessageId = "bot-current",
            LastIntent = "ModifyDraft",
            Version = 5,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        Assert.Null(await new ZaloAutoSessionMatchProposalV4Store(db).GetLatestAsync(proposal.Id));

        var recovered = await conversations.GetByIdAsync(legacy.Id);
        var latest = await new ZaloAutoSessionMatchProposalV4Store(db).GetLatestAsync(proposal.Id);

        Assert.NotNull(recovered);
        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Revision);
        Assert.Equal(5, latest.ConversationVersion);
        Assert.Equal("Sân A", Deserialize(latest.DraftJson).Location);
        Assert.Equal("Sân A", Deserialize(recovered!.DraftJson).Location);

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM \"ZaloAutoSessionMatchProposalRevisionsV4\" WHERE \"ProposalId\"='proposal-old';";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    private static string Draft(string location, int hour) =>
        JsonSerializer.Serialize(
            new ZaloAutoSessionConversationDraft(
                [new ZaloAutoSessionConversationDraftItem(
                    "t6",
                    "T6 11/9",
                    "T6",
                    new DateTimeOffset(2026, 9, 11, hour, 45, 0, TimeSpan.FromHours(7)),
                    10,
                    true)],
                location,
                6),
            JsonOptions);

    private static ZaloAutoSessionConversationDraft Deserialize(string json) =>
        JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json, JsonOptions)!;
}