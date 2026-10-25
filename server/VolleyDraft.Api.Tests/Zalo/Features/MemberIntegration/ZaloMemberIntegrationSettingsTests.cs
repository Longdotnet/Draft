using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberIntegrationSettingsTests
{
    [Fact]
    public void FromConfiguration_uses_safe_defaults()
    {
        var settings = ZaloMemberIntegrationSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(settings.Enabled);
        Assert.Equal(string.Empty, settings.GroupNameContains);
        Assert.Equal(28, settings.NewMemberWindowDays);
        Assert.Equal(string.Empty, settings.BotCreatorName);
        Assert.Equal(string.Empty, settings.GroupFounderName);
        Assert.Equal(string.Empty, settings.GroupPurpose);
        Assert.Equal(string.Empty, settings.CultureSummary);
        Assert.Null(settings.NormalCourtFeeVnd);
        Assert.Null(settings.MaxPlayersPerSession);
        Assert.Empty(settings.ApprovedLore);
        Assert.True(settings.AppliesTo("any group"));
    }

    [Theory]
    [InlineData("1", 7)]
    [InlineData("28", 28)]
    [InlineData("90", 60)]
    public void FromConfiguration_clamps_new_member_window(string configuredDays, int expectedDays)
    {
        var settings = FromValues(new Dictionary<string, string?>
        {
            ["ZaloBot:MemberIntegration:NewMemberWindowDays"] = configuredDays
        });

        Assert.Equal(expectedDays, settings.NewMemberWindowDays);
    }

    [Fact]
    public void FromConfiguration_trims_grounded_facts_and_discards_invalid_numbers_and_blank_lore()
    {
        var values = new Dictionary<string, string?>
        {
            ["ZaloBot:MemberIntegration:GroupNameContains"] = "  Volley Draft  ",
            ["ZaloBot:MemberIntegration:BotCreatorName"] = "  Thanh Long  ",
            ["ZaloBot:MemberIntegration:GroupFounderName"] = "  Nick Tran  ",
            ["ZaloBot:MemberIntegration:GroupPurpose"] = "  giao lưu  ",
            ["ZaloBot:MemberIntegration:CultureSummary"] = "  vui vẻ  ",
            ["ZaloBot:MemberIntegration:NormalCourtFeeVnd"] = "0",
            ["ZaloBot:MemberIntegration:MaxPlayersPerSession"] = "-1",
            ["ZaloBot:MemberIntegration:ApprovedLore:0"] = "  lore one  ",
            ["ZaloBot:MemberIntegration:ApprovedLore:1"] = "   ",
            ["ZaloBot:MemberIntegration:ApprovedLore:2"] = "lore two"
        };

        var settings = FromValues(values);

        Assert.Equal("Volley Draft", settings.GroupNameContains);
        Assert.Equal("Thanh Long", settings.BotCreatorName);
        Assert.Equal("Nick Tran", settings.GroupFounderName);
        Assert.Equal("giao lưu", settings.GroupPurpose);
        Assert.Equal("vui vẻ", settings.CultureSummary);
        Assert.Null(settings.NormalCourtFeeVnd);
        Assert.Null(settings.MaxPlayersPerSession);
        Assert.Equal(new[] { "lore one", "lore two" }, settings.ApprovedLore);
    }

    [Fact]
    public void FromConfiguration_limits_approved_lore_to_twenty_entries()
    {
        var values = Enumerable.Range(0, 25)
            .ToDictionary(
                index => $"ZaloBot:MemberIntegration:ApprovedLore:{index}",
                index => (string?)$"lore-{index:00}");

        var settings = FromValues(values);

        Assert.Equal(20, settings.ApprovedLore.Count);
        Assert.Equal("lore-00", settings.ApprovedLore[0]);
        Assert.Equal("lore-19", settings.ApprovedLore[^1]);
    }

    [Fact]
    public void AppliesTo_requires_enabled_setting_and_matches_group_name_case_insensitively()
    {
        var enabled = FromValues(new Dictionary<string, string?>
        {
            ["ZaloBot:MemberIntegration:Enabled"] = "true",
            ["ZaloBot:MemberIntegration:GroupNameContains"] = "Volley Draft"
        });
        var disabled = enabled with { Enabled = false };

        Assert.True(enabled.AppliesTo("Hội VOLLEY DRAFT Sài Gòn"));
        Assert.False(enabled.AppliesTo("Nhóm cầu lông"));
        Assert.False(enabled.AppliesTo(null));
        Assert.False(disabled.AppliesTo("Volley Draft"));
    }

    private static ZaloMemberIntegrationSettings FromValues(Dictionary<string, string?> values) =>
        ZaloMemberIntegrationSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build());
}
