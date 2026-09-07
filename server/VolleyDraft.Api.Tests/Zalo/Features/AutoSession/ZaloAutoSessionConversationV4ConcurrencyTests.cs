using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationV4ConcurrencyTests
{
    [Fact]
    public async Task LowerVersionSave_CannotRegressPersistedConversationState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionConversationStore(db);
        var now = DateTimeOffset.UtcNow;

        var current = await store.CreateIfMissingAsync(new ZaloAutoSessionConversationData
        {
            Id = "conversation-1",
            ProposalId = "proposal-without-v4-backfill",
            TrackedGroupId = "tracked-missing",
            PollId = "poll-1",
            GroupId = "group-1",
            OriginalOrganizerId = "leader-1",
            ActiveOrganizerId = "leader-1",
            State = ZaloAutoSessionConversationState.ReadyToConfirm,
            InitialDraftJson = "{}",
            DraftJson = "{}",
            PreviewMessageId = "preview-1",
            CurrentBotMessageId = "bot-5",
            LastIntent = "ModifyDraft",
            Version = 5,
            ExpiresAt = now.AddHours(12),
            CreatedAt = now.AddMinutes(-5)
        });
        Assert.Equal(5, current.Version);

        var stale = new ZaloAutoSessionConversationData
        {
            Id = current.Id,
            ProposalId = current.ProposalId,
            TrackedGroupId = current.TrackedGroupId,
            PollId = current.PollId,
            GroupId = current.GroupId,
            OriginalOrganizerId = current.OriginalOrganizerId,
            ActiveOrganizerId = current.ActiveOrganizerId,
            State = ZaloAutoSessionConversationState.Cancelled,
            InitialDraftJson = current.InitialDraftJson,
            DraftJson = current.DraftJson,
            PreviewMessageId = current.PreviewMessageId,
            CurrentBotMessageId = "stale-bot-4",
            LastIntent = "Cancel",
            Version = 4,
            ExpiresAt = current.ExpiresAt,
            CreatedAt = current.CreatedAt
        };

        var returned = await store.SaveAsync(stale);
        var persisted = await store.GetByIdAsync(current.Id);

        Assert.Equal(5, returned.Version);
        Assert.Equal(ZaloAutoSessionConversationState.ReadyToConfirm, returned.State);
        Assert.NotNull(persisted);
        Assert.Equal(5, persisted!.Version);
        Assert.Equal(ZaloAutoSessionConversationState.ReadyToConfirm, persisted.State);
        Assert.Equal("bot-5", persisted.CurrentBotMessageId);
        Assert.Equal("ModifyDraft", persisted.LastIntent);
    }
}