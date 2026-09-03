using Xunit;

namespace VolleyDraft.Api.Tests;

/// <summary>
/// Prevents future work (including coding agents) from silently rebuilding the Zalo god-class layout.
/// The limits are intentionally generous enough for temporary delegation code but new feature logic
/// must live under Services/Zalo/Features.
/// </summary>
public sealed class ZaloModularArchitectureGuardTests
{
    [Fact]
    public void Migrated_feature_files_must_not_return_to_services_root()
    {
        var root = FindRepoRoot();
        var services = Path.Combine(root, "server", "VolleyDraft.Api", "Services");

        string[] forbiddenRootPatterns =
        [
            "Zalo*Draft*.cs",
            "Zalo*OpenSlot*.cs",
            "Zalo*PassSlot*.cs",
            "Zalo*Guest*.cs",
            "Zalo*RecruitmentGuest*.cs",
            "Zalo*TeamPreference*.cs"
        ];

        var violations = forbiddenRootPatterns
            .SelectMany(pattern => Directory.GetFiles(services, pattern, SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Feature-owned Zalo files were added back to Services root: " + string.Join(", ", violations));
    }

    [Fact]
    public void Legacy_orchestrators_may_not_keep_growing()
    {
        var root = FindRepoRoot();
        var services = Path.Combine(root, "server", "VolleyDraft.Api", "Services");

        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZaloBotService.cs"] = 300_000,
            ["ZaloOverbookReminder.cs"] = 27_000,
            ["ZaloBotIntelligence.cs"] = 36_000
        };

        foreach (var (fileName, maxBytes) in limits)
        {
            var path = Path.Combine(services, fileName);
            Assert.True(File.Exists(path), $"Architecture guard expected legacy facade {fileName} to exist during migration.");
            var length = new FileInfo(path).Length;
            Assert.True(
                length <= maxBytes,
                $"{fileName} grew to {length:N0} bytes (limit {maxBytes:N0}). Move feature logic to Services/Zalo/Features instead.");
        }
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("PassSlot")]
    [InlineData("TeamPreference")]
    [InlineData("ShareSlot")]
    [InlineData("Guest")]
    public void Migrated_features_have_explicit_ownership_folder(string feature)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", feature);
        Assert.True(Directory.Exists(path), $"Missing feature ownership folder: {feature}");
        Assert.NotEmpty(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "server", "VolleyDraft.Api")) &&
                    File.Exists(Path.Combine(directory.FullName, "package.json")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root for Zalo architecture guard tests.");
    }
}
