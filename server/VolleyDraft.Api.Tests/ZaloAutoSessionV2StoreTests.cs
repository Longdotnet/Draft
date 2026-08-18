using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionV2StoreTests
{
    [Fact]
    public async Task RuntimeAndRollout_ArePersistentAndSafeByDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionV2Store(db);

        var runtime = await store.GetRuntimeAsync();
        var rollout = await store.GetRolloutModeAsync("group-a");

        Assert.True(runtime.GlobalEnabled);
        Assert.Equal(ZaloAutoSessionRolloutMode.Live, rollout);

        await store.SetGlobalEnabledAsync(false, "admin-a");
        await store.SetRolloutModeAsync("group-a", ZaloAutoSessionRolloutMode.PreviewOnly, "admin-a");

        Assert.False((await store.GetRuntimeAsync()).GlobalEnabled);
        Assert.Equal(ZaloAutoSessionRolloutMode.PreviewOnly, await store.GetRolloutModeAsync("group-a"));
    }

    [Fact]
    public async Task Health_ErrorBackoffResetsAfterSuccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionV2Store(db);

        var first = await store.RecordErrorAsync("group-a", "bridge down");
        var second = await store.RecordErrorAsync("group-a", "bridge still down");

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.NotNull(first.NextRetryAt);
        Assert.NotNull(second.NextRetryAt);
        Assert.True(second.NextRetryAt > first.NextRetryAt);

        await store.RecordSuccessAsync("group-a");
        var recovered = await store.GetHealthAsync("group-a");

        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.NextRetryAt);
        Assert.Null(recovered.LastError);
        Assert.NotNull(recovered.LastSuccessAt);
    }

    [Fact]
    public async Task LearningRule_IsNotAppliedUntilExplicitlyApproved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionV2Store(db);
        var now = DateTimeOffset.UtcNow;
        var signal = new ZaloAutoSessionLearningSignalData(
            "signal-1",
            "group-a",
            "proposal-1",
            "poll-1",
            "option-cn",
            "organizer-1",
            "default_day_time_correction",
            "CN",
            now,
            now.AddHours(-1),
            "default_day_time",
            16 * 60,
            ZaloAutoSessionLearningStatus.Pending,
            null,
            null,
            null,
            now,
            now);

        await store.AddLearningSignalAsync(signal);
        Assert.Empty(await store.GetApprovedDayTimeRulesAsync("group-a"));

        var approved = await store.ReviewLearningSignalAsync(
            "group-a",
            "signal-1",
            ZaloAutoSessionLearningStatus.Approved,
            "admin-a",
            "CN thường đánh 16h");

        Assert.NotNull(approved);
        var rules = await store.GetApprovedDayTimeRulesAsync("group-a");
        Assert.Equal(16 * 60, rules["CN"]);
    }
}
