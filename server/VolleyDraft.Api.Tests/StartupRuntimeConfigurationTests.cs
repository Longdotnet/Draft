using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class StartupRuntimeConfigurationTests
{
    [Fact]
    public void Cors_origins_always_include_production_and_normalize_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:Origins:0"] = "  https://customer.example.com/  ",
                ["Cors:Origins:1"] = "   ",
                ["Cors:Origins:2"] = "javascript:alert(1)"
            })
            .Build();

        var origins = StartupRuntimeConfiguration.GetAllowedCorsOrigins(configuration);

        Assert.Contains("https://volley-draft.onrender.com", origins);
        Assert.Contains("https://customer.example.com", origins);
        Assert.Contains("http://localhost:5173", origins);
        Assert.DoesNotContain(origins, string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(origins, origin => origin.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_bridge_url_falls_back_without_throwing_during_dependency_resolution(string? configured)
    {
        var configuration = Config("Zalo:BridgeBaseUrl", configured);

        var uri = StartupRuntimeConfiguration.GetZaloBridgeBaseUri(configuration);

        Assert.Equal("http://localhost:3000/", uri.AbsoluteUri);
    }

    [Fact]
    public void Bridge_url_is_trimmed_and_normalized()
    {
        var configuration = Config("Zalo:BridgeBaseUrl", "  https://zalo-bridge.example.com///  ");

        var uri = StartupRuntimeConfiguration.GetZaloBridgeBaseUri(configuration);

        Assert.Equal("https://zalo-bridge.example.com/", uri.AbsoluteUri);
    }

    [Fact]
    public void Invalid_non_http_bridge_url_fails_with_actionable_configuration_error()
    {
        var configuration = Config("Zalo:BridgeBaseUrl", "ftp://bridge.example.com");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupRuntimeConfiguration.GetZaloBridgeBaseUri(configuration));

        Assert.Contains("absolute HTTP(S) URL", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_internal_key_uses_development_fallback_instead_of_invalid_header(string? configured)
    {
        var configuration = Config("Zalo:BridgeInternalKey", configured);

        Assert.Equal(
            StartupRuntimeConfiguration.DevelopmentBridgeInternalKey,
            StartupRuntimeConfiguration.GetZaloBridgeInternalKey(configuration));
    }

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
}
