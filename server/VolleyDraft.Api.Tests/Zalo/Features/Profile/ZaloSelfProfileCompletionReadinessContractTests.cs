using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfProfileCompletionReadinessContractTests
{
    [Fact]
    public void Self_profile_completion_hands_off_to_canonical_draft_readiness()
    {
        var root = FindRepositoryRoot();
        var orchestrationPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.MissingProfileReplies.cs");
        var handoffPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.ProfileCompletionReadiness.cs");
        var orchestration = File.ReadAllText(orchestrationPath);
        var handoff = File.ReadAllText(handoffPath);

        Assert.Contains("ProcessMissingProfileRepliesContextFirstAsync", orchestration, StringComparison.Ordinal);
        Assert.Contains("ProcessMissingProfileRepliesDueV2Async", orchestration, StringComparison.Ordinal);
        Assert.Contains("SendProfileCompletionReadinessFollowUpsAsync", orchestration, StringComparison.Ordinal);

        Assert.Contains("profile_semantic_updated", handoff, StringComparison.Ordinal);
        Assert.Contains("profile_updated", handoff, StringComparison.Ordinal);
        Assert.Contains("new ZaloDraftReadinessService(db)", handoff, StringComparison.Ordinal);
        Assert.Contains("ZaloProfileUpdateReadinessCopy.Build(readiness)", handoff, StringComparison.Ordinal);
        Assert.Contains("profile-readiness:{prompt.Id}", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("không cần làm gì thêm", handoff, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Immediate_targeted_profile_completion_uses_readiness_in_the_primary_ack()
    {
        var root = FindRepositoryRoot();
        var routePath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.MissingProfilePreRoute.cs");
        var ackPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.ProfileCompletionAck.cs");
        var route = File.ReadAllText(routePath);
        var ack = File.ReadAllText(ackPath);

        Assert.Contains("BuildSelfProfileCompletionReplyAsync", route, StringComparison.Ordinal);
        Assert.DoesNotContain("xong, không cần làm gì thêm", route, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new ZaloDraftReadinessService(db)", ack, StringComparison.Ordinal);
        Assert.Contains("ZaloProfileUpdateReadinessCopy.Build(readiness)", ack, StringComparison.Ordinal);
        Assert.Contains("Tui không ghi đè thêm", ack, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "server", "VolleyDraft.Api")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
