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
        var start = source.IndexOf("private async Task<BotAnswer> UpdatePlayerProfileAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<BotAnswer> AddGuestPlayerAsync(", start, StringComparison.Ordinal);

        Assert.True(start >= 0, "Could not find the generic profile-update route.");
        Assert.True(end > start, "Could not isolate the generic profile-update route.");
        var route = source[start..end];

        Assert.Contains("new ZaloDraftReadinessService(db)", route, StringComparison.Ordinal);
        Assert.Contains("ZaloProfileUpdateReadinessCopy.Build(readiness)", route, StringComparison.Ordinal);
        Assert.DoesNotContain("GetIncompletePlayerProfilesAsync", route, StringComparison.Ordinal);
        Assert.DoesNotContain("Hồ sơ đã đủ điều kiện để draft.", route, StringComparison.Ordinal);
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
