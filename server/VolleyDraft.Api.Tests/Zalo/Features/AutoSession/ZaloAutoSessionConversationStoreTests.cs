using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationStoreTests
{
    [Fact]
    public async Task CreateFromPreview_PersistsResolvedPlanWithoutReparsingOptionText()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionConversationStore(db);
        var start = new DateTimeOffset(2026, 9, 13, 17, 45, 0, TimeSpan.FromHours(7));
        var proposal = new ZaloPollSessionProposalData
        {
            Id = "proposal-explicit-date",
            TrackedGroupId = "tracked-1",
            PollId = "poll-1",
            PollCreatorId = "captain",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var tracked = new ZaloTrackedGroupData
        {
            Id = "tracked-1",
            GroupId = "group-1",
            DefaultTeamSize = 6
        };
        var candidate = new ZaloAutoSessionCandidate(
            "o1",
            "Chủ nhật 13/9",
            "CN",
            start,
            2);

        var conversation = await store.CreateFromPreviewAsync(
            proposal,
            tracked,
            [candidate],
            "preview-1",
            new ConfigurationBuilder().Build());

        var draft = JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(
            conversation.DraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var persisted = Assert.Single(Assert.IsType<ZaloAutoSessionConversationDraft>(draft).Items);
        Assert.Equal("Chủ nhật 13/9", persisted.OptionContent);
        Assert.Equal("CN", persisted.DayKey);
        Assert.Equal(start, persisted.StartTime);
    }

    [Fact]
    public async Task Create_TurnLookup_AndExecutionClaim_AreIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionConversationStore(db);
        var now = DateTimeOffset.UtcNow;

        var conversation = await store.CreateIfMissingAsync(new ZaloAutoSessionConversationData
        {
            ProposalId = "proposal-1",
            TrackedGroupId = "tracked-1",
            PollId = "poll-1",
            GroupId = "group-1",
            OriginalOrganizerId = "captain",
            ActiveOrganizerId = "captain",
            State = ZaloAutoSessionConversationState.ReadyToConfirm,
            InitialDraftJson = "{}",
            DraftJson = "{}",
            PreviewMessageId = "bot-preview",
            CurrentBotMessageId = "bot-preview",
            ExpiresAt = now.AddHours(24),
            CreatedAt = now
        });

        var duplicate = await store.CreateIfMissingAsync(new ZaloAutoSessionConversationData
        {
            ProposalId = "proposal-1",
            TrackedGroupId = "tracked-other",
            PollId = "poll-other",
            GroupId = "group-other",
            OriginalOrganizerId = "other",
            ActiveOrganizerId = "other",
            State = ZaloAutoSessionConversationState.PreviewSent,
            InitialDraftJson = "{}",
            DraftJson = "{}",
            PreviewMessageId = "other",
            CurrentBotMessageId = "other",
            ExpiresAt = now.AddHours(24),
            CreatedAt = now
        });

        Assert.Equal(conversation.Id, duplicate.Id);
        Assert.Equal("group-1", duplicate.GroupId);

        await store.AddTurnAsync(
            conversation.Id,
            "bot-preview",
            "Bot",
            "bot",
            "Bot",
            "preview",
            "Preview",
            "system",
            1);

        Assert.True(await store.HasTurnAsync(conversation.Id, "bot-preview"));
        Assert.False(await store.HasTurnAsync(conversation.Id, "missing"));

        var resolved = await store.FindByQuotedBotMessageAsync("group-1", "bot-preview");
        Assert.NotNull(resolved);
        Assert.Equal(conversation.Id, resolved!.Id);

        Assert.True(await store.TryClaimExecutionAsync(conversation.Id, conversation.Version));
        Assert.False(await store.TryClaimExecutionAsync(conversation.Id, conversation.Version));

        var claimed = await store.GetByIdAsync(conversation.Id);
        Assert.NotNull(claimed);
        Assert.Equal(ZaloAutoSessionConversationState.Executing, claimed!.State);
        Assert.Equal(conversation.Version + 1, claimed.Version);
    }

    [Fact]
    public async Task DueQuery_ReturnsReminderOrExpiredConversationsOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionConversationStore(db);
        var now = DateTimeOffset.UtcNow;

        await store.CreateIfMissingAsync(Build("due", now.AddMinutes(-1), now.AddHours(2)));
        await store.CreateIfMissingAsync(Build("future", now.AddHours(1), now.AddHours(2)));
        await store.CreateIfMissingAsync(Build("expired", null, now.AddMinutes(-1)));

        var due = await store.GetDueAsync(now);

        Assert.Contains(due, item => item.ProposalId == "due");
        Assert.Contains(due, item => item.ProposalId == "expired");
        Assert.DoesNotContain(due, item => item.ProposalId == "future");
    }

    private static ZaloAutoSessionConversationData Build(
        string id,
        DateTimeOffset? followUp,
        DateTimeOffset expires) => new()
        {
            ProposalId = id,
            TrackedGroupId = "tracked",
            PollId = $"poll-{id}",
            GroupId = "group",
            OriginalOrganizerId = "captain",
            ActiveOrganizerId = "captain",
            State = ZaloAutoSessionConversationState.PreviewSent,
            InitialDraftJson = "{}",
            DraftJson = "{}",
            PreviewMessageId = $"preview-{id}",
            CurrentBotMessageId = $"preview-{id}",
            NextFollowUpAt = followUp,
            ExpiresAt = expires,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
