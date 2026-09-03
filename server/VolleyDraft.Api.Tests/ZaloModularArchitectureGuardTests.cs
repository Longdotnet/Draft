using Xunit;

namespace VolleyDraft.Api.Tests;

/// <summary>
/// Prevents future work (including coding agents) from silently rebuilding the Zalo god-class layout.
/// Legacy facades may exist during the strangler migration, but feature logic and infrastructure must
/// stay inside their explicit ownership boundaries under Services/Zalo.
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
            "Zalo*TeamPreference*.cs",
            "Zalo*ShareSlot*.cs",
            "ZaloAutoSession*.cs",
            "ZaloAmbient*.cs",
            "Zalo*Social*.cs",
            "Zalo*Profile*.cs",
            "Zalo*Identity*.cs",
            "ZaloPoll*.cs",
            "Zalo*Proactive*.cs",
            "Zalo*Roster*.cs",
            "Zalo*DomainEvent*.cs",
            "Zalo*OperatorPermission*.cs",
            "Zalo*ConversationState*.cs",
            "Zalo*MessageGraph*.cs",
            "Zalo*Retention*.cs",
            "Zalo*Trace*.cs",
            "ZaloBridge*.cs",
            "ZaloCredential*.cs",
            "ZaloListener*.cs",
            "ZaloIntegration*.cs",
            "ZaloQr*.cs",
            "ZaloOutboundReceipt*.cs",
            "ZaloLegacyOutbound*.cs",
            "ZaloUpcomingMatch*.cs"
        ];

        var violations = forbiddenRootPatterns
            .SelectMany(pattern => Directory.GetFiles(services, pattern, SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Feature/infrastructure-owned Zalo files were added back to Services root: " + string.Join(", ", violations));
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
            ["ZaloBotIntelligence.cs"] = 36_000,
            ["ZaloOverbookService.cs"] = 20_000,
            ["AiAssistantService.cs"] = 56_000
        };

        foreach (var (fileName, maxBytes) in limits)
        {
            var path = Path.Combine(services, fileName);
            Assert.True(File.Exists(path), $"Architecture guard expected legacy facade {fileName} to exist during migration.");
            var length = new FileInfo(path).Length;
            Assert.True(
                length <= maxBytes,
                $"{fileName} grew to {length:N0} bytes (limit {maxBytes:N0}). Move feature logic to Services/Zalo/Features or a shared Zalo core boundary instead.");
        }
    }

    [Theory]
    [InlineData("AutoSession")]
    [InlineData("Draft")]
    [InlineData("PassSlot")]
    [InlineData("TeamPreference")]
    [InlineData("ShareSlot")]
    [InlineData("Guest")]
    [InlineData("Social")]
    [InlineData("ReadOnlyFacts")]
    [InlineData("Reminder")]
    [InlineData("Profile")]
    [InlineData("Identity")]
    [InlineData("Poll")]
    [InlineData("Proactive")]
    [InlineData("Roster")]
    [InlineData("DomainEvents")]
    [InlineData("Permissions")]
    [InlineData("Overbook")]
    public void Migrated_features_have_explicit_ownership_folder(string feature)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", "Features", feature);
        Assert.True(Directory.Exists(path), $"Missing feature ownership folder: {feature}");
        Assert.NotEmpty(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Conversation")]
    [InlineData("Routing")]
    [InlineData("AI")]
    [InlineData("Infrastructure")]
    public void Cross_cutting_Zalo_capabilities_have_explicit_core_boundary(string boundary)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "server", "VolleyDraft.Api", "Services", "Zalo", boundary);
        Assert.True(Directory.Exists(path), $"Missing Zalo core boundary: {boundary}");
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
