using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloRetentionPolicyTests
{
    [Fact]
    public void Default_policy_keeps_traces_shorter_than_message_relations()
    {
        Assert.True(ZaloRetentionPolicy.Default.TraceRetention < ZaloRetentionPolicy.Default.MessageRelationRetention);
        Assert.Null(ZaloRetentionPolicy.Default.ActiveUserConceptRetention);
    }

    [Fact]
    public void Configuration_clamps_retention_ranges()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Retention:TraceDays"] = "9999",
                ["ZaloBot:Retention:MessageRelationDays"] = "0",
                ["ZaloBot:Retention:UserConceptDays"] = "45"
            })
            .Build();

        var policy = ZaloRetentionPolicy.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromDays(365), policy.TraceRetention);
        Assert.Equal(TimeSpan.FromDays(1), policy.MessageRelationRetention);
        Assert.Equal(TimeSpan.FromDays(45), policy.ActiveUserConceptRetention);
    }
}
