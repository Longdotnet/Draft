using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAdminDeepLinkBuilderTests
{
    [Fact]
    public void Uses_explicit_admin_web_base_url_and_targets_exact_session()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["AdminWeb:BaseUrl"] = "https://draft.example.com/"
        });

        var url = new ZaloAdminDeepLinkBuilder(configuration).Build(Snapshot(needsWebsite: true));

        Assert.Equal(
            "https://draft.example.com/app?focus=bot-overbook-control&sessionId=s1#bot-overbook-control",
            url);
    }

    [Fact]
    public void Falls_back_to_public_cors_frontend_origin()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Cors:Origins:0"] = "https://volley-draft-web.onrender.com"
        });

        var url = new ZaloAdminDeepLinkBuilder(configuration).Build(Snapshot(needsWebsite: true));

        Assert.StartsWith("https://volley-draft-web.onrender.com/app?", url, StringComparison.Ordinal);
        Assert.Contains("sessionId=s1", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_build_link_when_lifecycle_does_not_need_web()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["AdminWeb:BaseUrl"] = "https://draft.example.com"
        });

        Assert.Null(new ZaloAdminDeepLinkBuilder(configuration).Build(Snapshot(needsWebsite: false)));
    }

    [Fact]
    public void Rejects_localhost_so_zalo_never_receives_dead_dev_link()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["AdminWeb:BaseUrl"] = "http://localhost:5173",
            ["Cors:Origins:0"] = "http://127.0.0.1:5173"
        });

        Assert.Null(new ZaloAdminDeepLinkBuilder(configuration).Build(Snapshot(needsWebsite: true)));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static MatchLifecycleResponse Snapshot(bool needsWebsite) => new(
        SessionId: "s1",
        SessionName: "T6",
        Stage: MatchLifecycleStage.ResolvingOverbook,
        StageLabel: "Cần xác nhận dư slot",
        Headline: "headline",
        NextStep: "next",
        Owner: MatchLifecycleOwner.AdminWebsite,
        NeedsWebsite: needsWebsite,
        WebTarget: "bot-overbook-control",
        SuggestedZaloCommand: null,
        StartTime: DateTimeOffset.UtcNow.AddHours(2),
        PresentPlayerCount: 19,
        EffectiveSlotCount: 19,
        Capacity: 18,
        MissingProfileCount: 0,
        MissingProfileNames: [],
        ActiveSlotRiskCount: 0,
        LeaderDecision: null,
        ReasonCode: "test",
        EvaluatedAt: DateTimeOffset.UtcNow);
}
