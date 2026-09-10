using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloConversationLifetimePolicyTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(180)]
    public void Draft_session_choice_context_survives_normal_delayed_replies(int replyDelayMinutes)
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 13, 0, TimeSpan.Zero);
        var requestedExpiry = now.AddMinutes(15);

        var effectiveExpiry = ZaloConversationLifetimePolicy.NormalizeExpiry(
            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
            requestedExpiry,
            now);

        Assert.True(effectiveExpiry > now.AddMinutes(replyDelayMinutes));
        Assert.Equal(now.AddHours(4), effectiveExpiry);
    }

    [Fact]
    public void Draft_session_choice_keeps_a_longer_caller_expiry()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 13, 0, TimeSpan.Zero);
        var requestedExpiry = now.AddHours(8);

        var effectiveExpiry = ZaloConversationLifetimePolicy.NormalizeExpiry(
            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
            requestedExpiry,
            now);

        Assert.Equal(requestedExpiry, effectiveExpiry);
    }

    [Theory]
    [InlineData("AutoDraftConfirm")]
    [InlineData("RedraftConfirm")]
    [InlineData("TeamPreferenceConfirm")]
    [InlineData("ShareSlotConfirm")]
    [InlineData("DraftAutopilotRequesterConsent")]
    public void Authority_or_other_workflow_expiry_is_never_extended(string intent)
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 13, 0, TimeSpan.Zero);
        var requestedExpiry = now.AddMinutes(15);

        var effectiveExpiry = ZaloConversationLifetimePolicy.NormalizeExpiry(
            intent,
            requestedExpiry,
            now);

        Assert.Equal(requestedExpiry, effectiveExpiry);
    }

    [Fact]
    public async Task Store_persists_durable_expiry_for_draft_session_choice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var beforeSave = DateTimeOffset.UtcNow;
        var store = new ZaloConversationStateV2Store(db);
        var saved = await store.SaveActiveAsync(
            "group-1",
            "user-1",
            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
            "{}",
            "[]",
            "[\"session-t4\",\"session-cn\"]",
            "prompt-1",
            "prompt-1",
            beforeSave.AddMinutes(15));

        Assert.True(saved.ExpiresAt >= beforeSave.AddHours(4));

        var reloaded = await new ZaloConversationStateV2Store(db)
            .LoadActiveAsync("group-1", "user-1");
        Assert.NotNull(reloaded);
        Assert.Equal(saved.ExpiresAt, reloaded!.ExpiresAt);
        Assert.Contains("session-t4", reloaded.CandidateEntitiesJson);
    }

    [Fact]
    public async Task Store_does_not_extend_non_context_expiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var requestedExpiry = DateTimeOffset.UtcNow.AddMinutes(15);
        var saved = await new ZaloConversationStateV2Store(db).SaveActiveAsync(
            "group-1",
            "user-1",
            "AutoDraftConfirm",
            "{}",
            "[]",
            "[\"session-t4\"]",
            "confirm-1",
            "confirm-1",
            requestedExpiry);

        Assert.InRange(
            saved.ExpiresAt,
            requestedExpiry.AddSeconds(-1),
            requestedExpiry.AddSeconds(1));
    }
}
