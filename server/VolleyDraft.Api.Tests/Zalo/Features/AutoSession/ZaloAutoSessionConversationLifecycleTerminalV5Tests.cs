using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationLifecycleTerminalV5Tests
{
    [Fact]
    public async Task CreatedConversation_WithDurableLifecycleOwnership_ReadsAsHandedOff()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var proposalId = await SeedCreatedProposalAsync(db, "owned");
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated(proposalId));

        var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await lifecycle.EnsureAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleOwnerships"
                ("ProposalId", "State", "LinkedSessionCount", "HandedOffAt")
            VALUES
                ({{proposalId}}, 'HandedOff', 1, '2026-09-08T00:00:00.0000000+00:00');
            """);

        var reloaded = await conversations.GetByIdAsync(conversation.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(ZaloAutoSessionConversationState.HandedOff, reloaded!.State);
        Assert.Null(reloaded.NextFollowUpAt);
    }

    [Fact]
    public async Task SavingCreatedCompatibilityState_CannotDowngradeDurableHandedOffOwnership()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var proposalId = await SeedCreatedProposalAsync(db, "compatibility");
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated(proposalId));

        var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await lifecycle.EnsureAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleOwnerships"
                ("ProposalId", "State", "LinkedSessionCount", "HandedOffAt")
            VALUES
                ({{proposalId}}, 'HandedOff', 1, '2026-09-08T00:00:00.0000000+00:00');
            """);

        conversation.State = ZaloAutoSessionConversationState.Created;
        conversation.NextFollowUpAt = DateTimeOffset.UtcNow.AddMinutes(30);
        conversation.Version += 1;
        var saved = await conversations.SaveAsync(conversation);
        var reloaded = await conversations.GetByIdAsync(conversation.Id);

        Assert.Equal(ZaloAutoSessionConversationState.HandedOff, saved.State);
        Assert.NotNull(reloaded);
        Assert.Equal(ZaloAutoSessionConversationState.HandedOff, reloaded!.State);
        Assert.Null(reloaded.NextFollowUpAt);
    }

    [Fact]
    public async Task CreatedConversation_WithoutLifecycleOwnership_RemainsCreated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var proposalId = await SeedCreatedProposalAsync(db, "pending");
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated(proposalId));

        var reloaded = await conversations.GetByIdAsync(conversation.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(ZaloAutoSessionConversationState.Created, reloaded!.State);
    }

    private static async Task<string> SeedCreatedProposalAsync(VolleyDraftDbContext db, string suffix)
    {
        var tracked = await new ZaloAutoSessionSettingsStore(db).InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng UTE"
        });
        var proposal = await new ZaloAutoSessionStore(db).UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = $"poll-{suffix}",
            PollQuestion = $"Kèo {suffix}",
            PollCreatorId = "captain",
            PollStructureHash = $"hash-{suffix}",
            Status = ZaloPollSessionProposalStatus.Created
        });
        return proposal.Id;
    }

    private static ZaloAutoSessionConversationData BuildCreated(string proposalId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ZaloAutoSessionConversationData
        {
            ProposalId = proposalId,
            TrackedGroupId = "tracked-1",
            PollId = $"poll-{proposalId}",
            GroupId = "group-1",
            OriginalOrganizerId = "captain",
            ActiveOrganizerId = "captain",
            State = ZaloAutoSessionConversationState.Created,
            InitialDraftJson = "{}",
            DraftJson = "{}",
            PreviewMessageId = "preview-1",
            CurrentBotMessageId = "preview-1",
            NextFollowUpAt = now.AddMinutes(30),
            ExpiresAt = now.AddHours(24),
            CreatedAt = now
        };
    }
}
