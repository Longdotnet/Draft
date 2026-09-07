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
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated("proposal-owned"));

        var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await lifecycle.EnsureAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ZaloAutoSessionLifecycleOwnerships\" (\"ProposalId\", \"State\", \"LinkedSessionCount\", \"HandedOffAt\") VALUES ('proposal-owned', 'HandedOff', 1, '2026-09-08T00:00:00.0000000+00:00');");

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
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated("proposal-owned"));

        var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await lifecycle.EnsureAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ZaloAutoSessionLifecycleOwnerships\" (\"ProposalId\", \"State\", \"LinkedSessionCount\", \"HandedOffAt\") VALUES ('proposal-owned', 'HandedOff', 1, '2026-09-08T00:00:00.0000000+00:00');");

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
        var conversations = new ZaloAutoSessionConversationStore(db);
        var conversation = await conversations.CreateIfMissingAsync(BuildCreated("proposal-pending"));

        var reloaded = await conversations.GetByIdAsync(conversation.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(ZaloAutoSessionConversationState.Created, reloaded!.State);
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
