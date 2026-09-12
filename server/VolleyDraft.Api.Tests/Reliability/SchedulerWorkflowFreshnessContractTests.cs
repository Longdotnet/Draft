using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class SchedulerWorkflowFreshnessContractTests
{
    [Fact]
    public void Reminder_scheduler_requires_terminal_timestamps_from_the_accepted_api_clock_domain()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "reminder-scheduler-v2.yml");
        var helperPath = Path.Combine(root, "scripts", "scheduler-verifier.sh");
        var workflow = File.ReadAllText(workflowPath);
        var helper = File.ReadAllText(helperPath);

        Assert.Contains("source scripts/scheduler-verifier.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("runner_attempt_requested_at=$(date -u +'%Y-%m-%dT%H:%M:%SZ')", workflow, StringComparison.Ordinal);
        Assert.Contains("runner_queue_requested_at=\"$runner_attempt_requested_at\"", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_choose_freshness_boundary \"$runner_queue_requested_at\" /tmp/scheduler-response", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_timestamp_is_fresh \"$current_attempt\" \"$queue_requested_epoch\"", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_timestamp_is_fresh \"$current_success\" \"$queue_requested_epoch\"", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_timestamp_is_fresh \"$current_failure\" \"$queue_requested_epoch\"", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_verified_healthy", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_verified_failed", workflow, StringComparison.Ordinal);

        Assert.Contains("server_requested_at=$(jq -r '.requestedAt // empty'", helper, StringComparison.Ordinal);
        Assert.Contains("printf 'server\\t%s\\t%s\\n' \"$server_requested_at\" \"$server_epoch\"", helper, StringComparison.Ordinal);
        Assert.Contains("printf 'runner\\t%s\\t%s\\n' \"$runner_requested_at\" \"$runner_epoch\"", helper, StringComparison.Ordinal);
        Assert.Contains("A failed health state can be backed either by a newly persisted failure marker", helper, StringComparison.Ordinal);
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
