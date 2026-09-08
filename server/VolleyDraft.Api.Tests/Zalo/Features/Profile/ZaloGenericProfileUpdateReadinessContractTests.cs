using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGenericProfileUpdateReadinessContractTests
{
    [Fact]
    public void Generic_profile_update_uses_canonical_draft_readiness_after_mutation()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "server", "VolleyDraft.Api", "Services", "ZaloBotService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("new ZaloDraftReadinessService(db)", source, StringComparison.Ordinal);
        Assert.Contains("ZaloProfileUpdateReadinessCopy.Build(readiness)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Hồ sơ đã đủ điều kiện để draft.", source, StringComparison.Ordinal);
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
