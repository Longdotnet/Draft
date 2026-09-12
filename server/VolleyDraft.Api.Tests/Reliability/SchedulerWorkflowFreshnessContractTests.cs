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
        Assert.Contains("scheduler_verified_failed \"$health_state\" \"$current_failure_code\"", workflow, StringComparison.Ordinal);

        // Verification-window exhaustion is not a scheduler failure while either the
        // accepted fresh attempt or the exact baseline predecessor is still running
        // under a live API-observed durable lease.
        Assert.Contains("current_observed_at=$(jq -r '.observedAt // empty'", workflow, StringComparison.Ordinal);
        Assert.Contains("current_lease_until=$(jq -r '.leaseUntil // empty'", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_verified_in_progress", workflow, StringComparison.Ordinal);
        Assert.Contains("scheduler_verified_predecessor_in_progress", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$baseline_attempt\"", workflow, StringComparison.Ordinal);
        Assert.Contains("the accepted wake remains queued behind that authoritative cycle", workflow, StringComparison.Ordinal);
        Assert.Contains("terminal_healthy=false", workflow, StringComparison.Ordinal);
        Assert.Contains("terminal_healthy=true", workflow, StringComparison.Ordinal);
        Assert.Contains("success() && steps.scheduler.outputs.terminal_healthy == 'true'", workflow, StringComparison.Ordinal);

        Assert.Contains("server_requested_at=$(jq -r '.requestedAt // empty'", helper, StringComparison.Ordinal);
        Assert.Contains("printf 'server\\t%s\\t%s\\n' \"$server_requested_at\" \"$((server_epoch - 1))\"", helper, StringComparison.Ordinal);
        Assert.Contains("baseline advancement is still required", helper, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("printf 'runner\\t%s\\t%s\\n' \"$runner_requested_at\" \"$runner_epoch\"", helper, StringComparison.Ordinal);
        Assert.Contains("Failure authority belongs to the accepted cycle only after that cycle's own", helper, StringComparison.Ordinal);
        Assert.Contains("[ \"$failure_code\" = \"abandoned:leaseexpired\" ]", helper, StringComparison.Ordinal);
        Assert.Contains("One scheduler cycle can legitimately outlive the short GitHub polling window", helper, StringComparison.Ordinal);
        Assert.Contains("[ \"$lease_epoch\" -gt \"$observed_epoch\" ]", helper, StringComparison.Ordinal);
        Assert.Contains("The exact baseline attempt must still own a live", helper, StringComparison.OrdinalIgnoreCase);
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
