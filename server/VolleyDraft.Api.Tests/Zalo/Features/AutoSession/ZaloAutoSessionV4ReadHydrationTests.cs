using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionV4ReadHydrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DraftConsumingReadPaths_HydrateLatestV4DraftAfterV3CacheDrift()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);

        var tracked = await new ZaloAutoSessionSettingsStore(db).InsertIfMissingAsync(BuildTracked());
        var proposal = BuildProposal(tracked.Id);
        proposal = await new ZaloAutoSessionStore(db).UpsertProposalAsync(proposal);
        var store = new ZaloAutoSessionConversationStore(db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var candidates = new[]
        {
            new ZaloAutoSessionCandidate("t6", "T6 11/9", "T6", At(11, 17), 10),
            new ZaloAutoSessionCandidate("cn", "CN 13/9", "CN", At(13, 17), 9)
        };
        var conversation = await store.CreateFromPreviewAsync(
            proposal,
            tracked,
            candidates,
            "preview-1",
            configuration);
        var staleDraftJson = conversation.InitialDraftJson;

        var currentDraft = DeserializeDraft(conversation.DraftJson);
        conversation.Version = 1;
        conversation.ActiveOrganizerId = "leader-1";
        conversation.LastIntent = "ModifyDraft";
        conversation.DraftJson = JsonSerializer.Serialize(
            currentDraft with { Location = "Sân durable V4" },
            JsonOptions);
        var saved = await store.SaveAsync(conversation);
        Assert.Equal("Sân durable V4", DeserializeDraft(saved.DraftJson).Location);

        await using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE \"ZaloAutoSessionConversations\" SET \"DraftJson\"=@draft WHERE \"Id\"=@id;";
            tamper.Parameters.AddWithValue("@draft", staleDraftJson);
            tamper.Parameters.AddWithValue("@id", conversation.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        Assert.Equal(
            "Sân durable V4",
            DeserializeDraft((await store.FindByQuotedBotMessageAsync(tracked.GroupId, "preview-1"))!.DraftJson).Location);

        var activeForGroup = await store.GetActiveForGroupAsync(tracked.GroupId);
        Assert.Single(activeForGroup);
        Assert.Equal("Sân durable V4", DeserializeDraft(activeForGroup[0].DraftJson).Location);

        var active = await store.GetActiveAsync();
        Assert.Contains(active, item =>
            item.Id == conversation.Id &&
            DeserializeDraft(item.DraftJson).Location == "Sân durable V4");

        var due = await store.GetDueAsync(DateTimeOffset.UtcNow.AddHours(1));
        Assert.Contains(due, item =>
            item.Id == conversation.Id &&
            DeserializeDraft(item.DraftJson).Location == "Sân durable V4");
    }

    private static ZaloPollSessionProposalData BuildProposal(string trackedGroupId) => new()
    {
        Id = "proposal-read-hydration",
        TrackedGroupId = trackedGroupId,
        PollId = "poll-read-hydration",
        PollQuestion = "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1788750000000,
        PollStructureHash = "poll-hash-read-hydration",
        CandidatesJson = "[]",
        ClassifierConfidence = 1,
        ClassifierReason = "deterministic",
        Status = ZaloPollSessionProposalStatus.AwaitingApproval,
        ProposalMessageId = "preview-1"
    };

    private static ZaloTrackedGroupData BuildTracked() => new()
    {
        Id = "tracked-read-hydration",
        AdminUserId = "admin-1",
        ZaloConnectionId = "connection-1",
        GroupId = "group-read-hydration",
        GroupName = "UTE Volley",
        AutoSessionEnabled = true,
        DefaultTeamCount = 3,
        DefaultTeamSize = 6,
        DefaultLocation = "Sân UTE"
    };

    private static DateTimeOffset At(int day, int hour) =>
        new(2026, 9, day, hour, 45, 0, TimeSpan.FromHours(7));

    private static ZaloAutoSessionConversationDraft DeserializeDraft(string json) =>
        JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json, JsonOptions)!;
}
