using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class SchedulerWorkflowFreshnessContractTests
{
    [Fact]
    public void Reminder_scheduler_requires_terminal_timestamps_from_the_current_queue_request()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "reminder-scheduler-v2.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("queue_requested_at=$(date -u +'%Y-%m-%dT%H:%M:%SZ')", workflow, StringComparison.Ordinal);
        Assert.Contains("queue_requested_epoch=$(parse_timestamp_epoch \"$queue_requested_at\")", workflow, StringComparison.Ordinal);
        Assert.Contains("attempt_is_fresh=1", workflow, StringComparison.Ordinal);
        Assert.Contains("success_is_fresh=1", workflow, StringComparison.Ordinal);
        Assert.Contains("failure_is_fresh=1", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$attempt_is_fresh\" -eq 1 ] && [ \"$success_is_fresh\" -eq 1 ]", workflow, StringComparison.Ordinal);
        Assert.Contains("[ \"$failure_is_fresh\" -eq 1 ] || [ \"$attempt_is_fresh\" -eq 1 ]", workflow, StringComparison.Ordinal);
        Assert.Contains("Baseline comparison alone", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".github", "workflows")) &&
                Directory.Exists(Path.Combine(directory.FullName, "server", "VolleyDraft.Api")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
