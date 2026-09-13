using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class AiAssistantArchitectureGuardTests
{
    [Fact]
    public void Legacy_assistant_must_not_send_provider_http_directly()
    {
        var path = FindRepositoryFile("server", "VolleyDraft.Api", "Services", "AiAssistantService.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("httpClient.SendAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationHeaderValue(\"Bearer\"", source, StringComparison.Ordinal);
        Assert.Contains("IZaloAiGateway", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', segments)}");
    }
}
