using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Roster;

public sealed class ZaloRosterAudienceBuilderTests
{
    [Fact]
    public void Build_preserves_roster_order_and_binds_grounded_zalo_ids()
    {
        var result = ZaloRosterAudienceBuilder.Build(
            "T6 30/10 18:00",
            ["Bảo Long", "Chu Thuận Thành", "Thanh Long"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["u-thanh"] = "Thanh Long",
                ["u-bao_0"] = "Bảo Long",
                ["u-chu"] = "Chu Thuận Thành"
            });

        Assert.Equal("roster", result.SourceKind);
        Assert.Equal("danh sách tham gia T6 30/10 18:00", result.ContextLabel);
        Assert.Equal(
            new[] { "Bảo Long", "Chu Thuận Thành", "Thanh Long" },
            result.Targets.Select(item => item.DisplayName));
        Assert.Equal(
            new[] { "u-bao", "u-chu", "u-thanh" },
            result.Targets.Select(item => item.ZaloUserId));
    }

    [Fact]
    public void Build_keeps_unresolved_or_ambiguous_member_as_null_instead_of_silently_dropping_them()
    {
        var result = ZaloRosterAudienceBuilder.Build(
            "T6",
            ["An", "Bình"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["u-an-1"] = "An",
                ["u-an-2"] = "An"
            });

        Assert.Equal(2, result.Targets.Count);
        Assert.Null(result.Targets[0].ZaloUserId);
        Assert.Equal("An", result.Targets[0].DisplayName);
        Assert.Null(result.Targets[1].ZaloUserId);
        Assert.Equal("Bình", result.Targets[1].DisplayName);
    }
}
