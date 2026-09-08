using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftReminderExecutionFenceContractTests
{
    [Fact]
    public void Reminder_lane_fail_closes_when_execution_fence_is_active_or_wins_the_race()
    {
        var root = FindRepoRoot();
        var reminderPath = Path.Combine(
            root,
            "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Draft",
            "ZaloOverbookService.DraftPreparationRemindersV2.cs");
        var supersedePath = Path.Combine(
            root,
            "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Draft",
            "ZaloOverbookService.DraftReminderExecutionFence.cs");

        var reminder = File.ReadAllText(reminderPath);
        var supersede = File.ReadAllText(supersedePath);

        Assert.Contains(
            "existingRequest?.State == ZaloDraftEscalationState.Executing",
            reminder,
            StringComparison.Ordinal);
        Assert.Contains(
            "var superseded = await TrySupersedeDraftReminderRequestAsync(",
            reminder,
            StringComparison.Ordinal);
        Assert.Contains("if (!superseded)", reminder, StringComparison.Ordinal);

        var failedCas = supersede.IndexOf("if (updated == 0)", StringComparison.Ordinal);
        var pendingCleanup = supersede.IndexOf("RemoveDraftPendingAsync(", StringComparison.Ordinal);
        Assert.True(failedCas >= 0, "supersession helper must inspect the DB transition result");
        Assert.True(
            pendingCleanup > failedCas,
            "pending confirmation cleanup must happen only after supersession actually wins");
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VolleyDraft.sln")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
