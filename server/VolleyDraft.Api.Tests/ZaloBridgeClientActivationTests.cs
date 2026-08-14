using Microsoft.Extensions.DependencyInjection;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBridgeClientActivationTests
{
    [Fact]
    public void Typed_client_has_exactly_one_public_constructor()
    {
        Assert.Single(typeof(ZaloBridgeClient).GetConstructors());
    }

    [Fact]
    public void ActivatorUtilities_can_build_the_typed_client_with_explicit_http_client()
    {
        var factory = ActivatorUtilities.CreateFactory(
            typeof(ZaloBridgeClient),
            [typeof(HttpClient)]);
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:3000/")
        };

        var client = factory(services, [httpClient]);

        Assert.IsType<ZaloBridgeClient>(client);
    }
}
