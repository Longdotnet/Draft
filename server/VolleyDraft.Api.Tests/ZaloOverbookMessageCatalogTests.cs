using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOverbookMessageCatalogTests
{
    [Theory]
    [InlineData(1, ZaloOverbookMessageCatalog.LightStage)]
    [InlineData(2, ZaloOverbookMessageCatalog.LightStage)]
    [InlineData(3, ZaloOverbookMessageCatalog.CalloutStage)]
    [InlineData(5, ZaloOverbookMessageCatalog.CalloutStage)]
    [InlineData(6, ZaloOverbookMessageCatalog.SarcasticStage)]
    [InlineData(15, ZaloOverbookMessageCatalog.SarcasticStage)]
    [InlineData(16, ZaloOverbookMessageCatalog.StubbornStage)]
    [InlineData(100, ZaloOverbookMessageCatalog.StubbornStage)]
    public void Reminder_number_maps_to_expected_stage(int reminderNumber, string expectedStage)
    {
        Assert.Equal(expectedStage, ZaloOverbookMessageCatalog.GetStageName(reminderNumber));
    }

    [Theory]
    [InlineData(ZaloOverbookMessageCatalog.LightStage, 50)]
    [InlineData(ZaloOverbookMessageCatalog.CalloutStage, 50)]
    [InlineData(ZaloOverbookMessageCatalog.SarcasticStage, 100)]
    [InlineData(ZaloOverbookMessageCatalog.StubbornStage, 100)]
    public void Default_stage_has_expected_distinct_genz_templates(string stage, int expectedCount)
    {
        var bank = ZaloOverbookMessageCatalog.GetDefaultStageBank(stage);

        Assert.Equal(expectedCount, bank.Count);
        Assert.Equal(expectedCount, bank.Distinct(StringComparer.Ordinal).Count());
        Assert.All(bank, text => Assert.Contains("{names}", text));
    }

    [Fact]
    public void Reminder_bank_uses_the_same_large_pool_for_the_whole_stage()
    {
        Assert.Same(
            ZaloOverbookMessageCatalog.GetDefaultStageBank(ZaloOverbookMessageCatalog.LightStage),
            ZaloOverbookMessageCatalog.GetBank(1));
        Assert.Same(
            ZaloOverbookMessageCatalog.GetDefaultStageBank(ZaloOverbookMessageCatalog.LightStage),
            ZaloOverbookMessageCatalog.GetBank(2));
        Assert.Same(
            ZaloOverbookMessageCatalog.GetDefaultStageBank(ZaloOverbookMessageCatalog.StubbornStage),
            ZaloOverbookMessageCatalog.GetBank(100));
    }

    [Fact]
    public void Ui_stage_bank_prefers_custom_stage_and_falls_back_for_others()
    {
        var overrides = new Dictionary<int, List<string>>
        {
            [ZaloOverbookMessageCatalog.LightStorageKey] = ["custom {names}"],
        };

        var banks = ZaloOverbookMessageCatalog.GetUiStageBanks(overrides);

        Assert.Equal(["custom {names}"], banks[ZaloOverbookMessageCatalog.LightStage]);
        Assert.Equal(50, banks[ZaloOverbookMessageCatalog.CalloutStage].Count);
        Assert.Equal(100, banks[ZaloOverbookMessageCatalog.SarcasticStage].Count);
        Assert.Equal(100, banks[ZaloOverbookMessageCatalog.StubbornStage].Count);
    }

    [Fact]
    public void Exact_reminder_ui_overrides_do_not_expose_stage_storage_keys()
    {
        var overrides = new Dictionary<int, List<string>>
        {
            [10] = ["legacy #10 {names}"],
            [ZaloOverbookMessageCatalog.GetAdvancedExactStorageKey(10)] = ["special #10 {names}"],
            [ZaloOverbookMessageCatalog.LightStorageKey] = ["stage {names}"],
        };

        var exact = ZaloOverbookMessageCatalog.GetUiBanks(overrides);

        Assert.Single(exact);
        Assert.Equal(["special #10 {names}"], exact[10]);
        Assert.DoesNotContain(ZaloOverbookMessageCatalog.LightStorageKey, exact.Keys);
    }
}
