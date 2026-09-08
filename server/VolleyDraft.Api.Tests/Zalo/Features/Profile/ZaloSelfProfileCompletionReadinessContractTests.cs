using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfProfileCompletionReadinessContractTests
{
    [Fact]
    public void Worker_profile_completion_uses_readiness_in_the_primary_ack_for_both_lanes()
    {
        var root = FindRepositoryRoot();
        var orchestrationPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.MissingProfileReplies.cs");
        var deterministicPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", "Profile",
            "ZaloOverbookService.MissingProfileRepliesV2.cs");
        var semanticPath = Path.Combine(
            root, "server", "VolleyDraft.Api", "Services", "Zalo", "AI", "Semantic",
            "ZaloOverbookService.ContextFirstSemantic.cs");
        var orchestration = File.ReadAllText(orchestrationPath);
        var deterministic = File.ReadAllText(deterministicPath);
        var semantic = File.ReadAllText(semanticPath);

        Assert.Contains("ProcessMissingProfileRepliesContextFirstAsync", orchestration, StringComparison.Ordinal);
        Assert.Contains("ProcessMissingProfileRepliesDueV2Async", orchestration, StringComparison.Ordinal);
        Assert.DoesNotContain("SendProfileCompletionReadinessFollowUpsAsync", orchestration, StringComparison.Ordinal);

        Assert.Contains("BuildSelfProfileCompletionReplyAsync", deterministic, StringComparison.Ordinal);
        Assert.Contains("alreadyComplete: true", deterministic, StringComparison.Ordinal);
        Assert.Contains("alreadyComplete: false", deterministic, StringComparison.Ordinal);
        Assert.DoesNotContain("xong, không cần làm gì thêm", deterministic, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("BuildSelfProfileCompletionReplyAsync", semantic, StringComparison.Ordinal);
        Assert.Contains("profile_semantic_already_complete", semantic, StringComparison.Ordinal);
        Assert.Contains("alreadyComplete: true", semantic, StringComparison.Ordinal);
        Assert.Contains("alreadyComplete: false", semantic, StringComparison.Ordinal);
        Assert.DoesNotContain("Hồ sơ kèo {session.Name} xong.\"", semantic, StringComparison.Ordinal);
        Assert.DoesNotContain("Hiện chỉ còn không còn gì thiếu", semantic, StringComparison.OrdinalIgnoreCase);
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
