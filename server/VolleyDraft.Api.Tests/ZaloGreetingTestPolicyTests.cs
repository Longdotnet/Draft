using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingTestPolicyTests
{
    [Theory]
    [InlineData("Morning", "Morning")]
    [InlineData("morning", "Morning")]
    [InlineData("Night", "Night")]
    [InlineData(" night ", "Night")]
    public void Greeting_test_kind_parser_accepts_only_supported_kinds(string input, string expected)
    {
        Assert.True(ZaloGreetingTestPolicy.TryParseKind(input, out var kind));
        Assert.Equal(expected, kind.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Noon")]
    [InlineData("MorningAndNight")]
    public void Greeting_test_kind_parser_rejects_unknown_values(string input)
    {
        Assert.False(ZaloGreetingTestPolicy.TryParseKind(input, out _));
    }

    [Theory]
    [InlineData("Morning")]
    [InlineData("Night")]
    public void Greeting_test_allows_only_server_catalog_messages(string kindName)
    {
        Assert.True(ZaloGreetingTestPolicy.TryParseKind(kindName, out var kind));
        var allowed = ZaloDailyGreetingPhraseCatalog.All(kind);

        Assert.NotEmpty(allowed);
        Assert.True(ZaloGreetingTestPolicy.IsAllowedMessage(kind, allowed[0]));
        Assert.False(ZaloGreetingTestPolicy.IsAllowedMessage(kind, "tin nhắn tự nhập từ client"));
    }

    [Theory]
    [InlineData("Morning", "greeting-test-morning-")]
    [InlineData("Night", "greeting-test-night-")]
    public void Greeting_test_assets_have_isolated_prefix(string kindName, string expectedPrefix)
    {
        Assert.True(ZaloGreetingTestPolicy.TryParseKind(kindName, out var kind));
        Assert.Equal(expectedPrefix, ZaloGreetingTestPolicy.AssetPrefix(kind));
    }
}
