using VolleyDraft.Api.Services;
using Xunit;
namespace VolleyDraft.Api.Tests;
public sealed class ZaloOverbookMessageCatalogTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void First_seven_reminders_have_twenty_distinct_genz_templates(int number)
    {
        var bank = ZaloOverbookMessageCatalog.GetBank(number);
        Assert.Equal(20, bank.Count);
        Assert.Equal(20, bank.Distinct(StringComparer.Ordinal).Count());
        Assert.All(bank, text => Assert.Contains("{names}", text));
    }
    [Fact] public void Reminder_100_has_fallback_bank() => Assert.Equal(20, ZaloOverbookMessageCatalog.GetBank(100).Count);
}
