using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionObservabilityTests
{
    [Fact]
    public async Task GetActivity_ReturnsProposalFactsOnlyToOwningAdmin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var settingsStore = new ZaloAutoSessionSettingsStore(db);
        var baseStore = new ZaloAutoSessionStore(db);
        var tracked = await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng UTE"
        });
        var candidates = new[]
        {
            new ZaloAutoSessionCandidate(
                "option-t6",
                "T6 17h30",
                "T6",
                new DateTimeOffset(2026, 8, 21, 17, 30, 0, TimeSpan.FromHours(7)),
                12)
        };
        await baseStore.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần này",
            PollCreatorId = "captain-1",
            PollStructureHash = "hash-1",
            CandidatesJson = JsonSerializer.Serialize(candidates, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ClassifierConfidence = 0.94,
            ClassifierReason = "weekday_pattern;volleyball_context",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval,
            ProposalMessageId = "provider-message-1"
        });

        var service = new ZaloAutoSessionObservabilityService(db);
        var owned = await service.GetActivityAsync("admin-a", tracked.Id, 10);
        var foreign = await service.GetActivityAsync("admin-b", tracked.Id, 10);

        Assert.True(owned.IsSuccess);
        Assert.NotNull(owned.Value);
        Assert.Equal(1, owned.Value!.ProposalCount);
        Assert.Equal(1, owned.Value.AwaitingApprovalCount);
        Assert.Equal("Kèo tuần này", owned.Value.Proposals[0].PollQuestion);
        Assert.Equal(0.94, owned.Value.Proposals[0].ClassifierConfidence, 2);
        Assert.Single(owned.Value.Proposals[0].Candidates);
        Assert.Equal("T6", owned.Value.Proposals[0].Candidates[0].DayKey);
        Assert.Null(owned.Value.Proposals[0].Candidates[0].SessionId);

        Assert.False(foreign.IsSuccess);
        Assert.Equal(404, foreign.StatusCode);
    }

    [Fact]
    public async Task ObservabilityStore_HidesLinksFromOtherAdmins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var settingsStore = new ZaloAutoSessionSettingsStore(db);
        var baseStore = new ZaloAutoSessionStore(db);
        var tracked = await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng UTE"
        });
        await baseStore.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            tracked.Id,
            "poll-1",
            "option-t6",
            "session-1",
            DateTimeOffset.UtcNow));

        var store = new ZaloAutoSessionObservabilityStore(db);
        Assert.Single(await store.GetLinksAsync("admin-a", tracked.Id));
        Assert.Empty(await store.GetLinksAsync("admin-b", tracked.Id));
    }
}
