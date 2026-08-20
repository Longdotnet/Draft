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
    public void Greeting_test_assets_are_scoped_to_kind_and_target(string kindName, string expectedStart)
    {
        Assert.True(ZaloGreetingTestPolicy.TryParseKind(kindName, out var kind));

        var groupA = ZaloGreetingTestPolicy.AssetPrefix(kind, "connection-a", "group-a");
        var groupB = ZaloGreetingTestPolicy.AssetPrefix(kind, "connection-a", "group-b");
        var otherConnection = ZaloGreetingTestPolicy.AssetPrefix(kind, "connection-b", "group-a");

        Assert.StartsWith(expectedStart, groupA, StringComparison.Ordinal);
        Assert.NotEqual(groupA, groupB);
        Assert.NotEqual(groupA, otherConnection);
        Assert.Equal(groupA, ZaloGreetingTestPolicy.AssetPrefix(kind, "connection-a", "group-a"));
    }

    [Theory]
    [InlineData("Morning")]
    [InlineData("Night")]
    public void Greeting_test_outbound_marker_cannot_count_as_production_greeting(string kindName)
    {
        Assert.True(ZaloGreetingTestPolicy.TryParseKind(kindName, out var kind));
        var production = ZaloDailyGreetingPhraseCatalog.All(kind).First();
        var outboundTest = ZaloGreetingTestPolicy.BuildOutboundTestMessage(kind, production);

        Assert.True(ZaloGreetingTestPolicy.IsProductionGreetingMessage(production, kind));
        Assert.StartsWith($"🧪 TEST {kindName.ToUpperInvariant()} · ", outboundTest, StringComparison.Ordinal);
        Assert.False(ZaloGreetingTestPolicy.IsProductionGreetingMessage(outboundTest, kind));
    }
}
