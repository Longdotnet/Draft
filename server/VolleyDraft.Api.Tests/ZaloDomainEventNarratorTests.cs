using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDomainEventNarratorTests
{
    [Fact]
    public async Task Pilot_disabled_never_calls_bridge()
    {
        var handler = new RecordingHandler();
        var narrator = Narrator(handler, new Dictionary<string, string?>
        {
            ["ZaloBot:Ambient:ShadowMode"] = "false",
            ["ZaloBot:Ambient:DomainEventPilot:Enabled"] = "false",
            ["ZaloBot:Ambient:DomainEventPilot:SendEnabled"] = "true"
        });

        var result = await narrator.HandleAsync("bot", "group", "session", "T6", Filled());

        Assert.True(result.Eligible);
        Assert.False(result.Sent);
        Assert.Equal("pilot_disabled", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Global_shadow_mode_blocks_send_even_when_pilot_send_is_enabled()
    {
        var handler = new RecordingHandler();
        var narrator = Narrator(handler, new Dictionary<string, string?>
        {
            ["ZaloBot:Ambient:ShadowMode"] = "true",
            ["ZaloBot:Ambient:DomainEventPilot:Enabled"] = "true",
            ["ZaloBot:Ambient:DomainEventPilot:SendEnabled"] = "true"
        });

        var result = await narrator.HandleAsync("bot", "group", "session", "T6", Filled());

        Assert.False(result.Sent);
        Assert.Equal("global_shadow_mode", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Non_narratable_count_change_never_calls_bridge()
    {
        var handler = new RecordingHandler();
        var narrator = Narrator(handler, new Dictionary<string, string?>
        {
            ["ZaloBot:Ambient:ShadowMode"] = "false",
            ["ZaloBot:Ambient:DomainEventPilot:Enabled"] = "true",
            ["ZaloBot:Ambient:DomainEventPilot:SendEnabled"] = "true"
        });
        var decision = new ZaloDomainEventShadowDecision("RosterIncreased", 3, 4, 18, "trace");

        var result = await narrator.HandleAsync("bot", "group", "session", "T6", decision);

        Assert.False(result.Eligible);
        Assert.Equal("event_not_narratable", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Explicit_live_gates_send_deterministic_roster_filled_message_once()
    {
        var handler = new RecordingHandler();
        var narrator = Narrator(handler, new Dictionary<string, string?>
        {
            ["ZaloBot:Ambient:ShadowMode"] = "false",
            ["ZaloBot:Ambient:DomainEventPilot:Enabled"] = "true",
            ["ZaloBot:Ambient:DomainEventPilot:SendEnabled"] = "true"
        });

        var result = await narrator.HandleAsync("bot", "group", "session", "T6", Filled());

        Assert.True(result.Sent);
        Assert.Equal("sent", result.Reason);
        Assert.Equal("✅ T6 đã đủ 18/18 người theo poll hiện tại.", result.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("domain-event:session:RosterFilled:17-18:18", handler.LastBody);
        Assert.Contains("✅ T6 đã đủ 18/18 người theo poll hiện tại.", handler.LastBody);
    }

    [Fact]
    public void Reopened_message_reports_current_available_slots()
    {
        var message = ZaloDomainEventNarrator.BuildMessage(
            "CN",
            new ZaloDomainEventShadowDecision("RosterReopened", 18, 16, 18, "trace"));

        Assert.Equal("📢 CN vừa trống lại 2 suất (16/18) theo poll hiện tại.", message);
    }

    private static ZaloDomainEventShadowDecision Filled() =>
        new("RosterFilled", 17, 18, 18, "trace");

    private static ZaloDomainEventNarrator Narrator(
        RecordingHandler handler,
        IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        };
        return new ZaloDomainEventNarrator(configuration, new ZaloBridgeClient(httpClient));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sent\":true,\"mock\":false,\"messageId\":\"provider-1\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
