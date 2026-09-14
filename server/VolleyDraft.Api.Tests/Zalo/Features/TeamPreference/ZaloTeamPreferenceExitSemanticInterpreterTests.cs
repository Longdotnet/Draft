using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.AI;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamPreferenceExitSemanticInterpreterTests
{
    [Fact]
    public async Task Dislike_reason_can_only_suggest_preview_target_from_grounded_group()
    {
        var gateway = new StubGateway(new ZaloAiCompletionResult(
            true,
            "{\"kind\":\"SuggestExit\",\"confidence\":0.93,\"targetZaloUserId\":\"user-toan\",\"targetDisplayName\":\"To An\",\"sessionReference\":null,\"reason\":\"strong_dislike_playing_together\"}",
            ZaloAiFailureKind.None,
            "test",
            "test-model",
            1,
            200,
            TimeSpan.Zero,
            false));
        var interpreter = new ZaloTeamPreferenceExitSemanticInterpreter(
            Configuration(), gateway, NullLogger.Instance);
        var candidate = Candidate();

        var result = await interpreter.InterpretAsync(
            "conn-1",
            "g1",
            "user-long",
            "tui không thích chơi với To An do nó giành banh hoài",
            new ZaloReadOnlyConversationContext([], []),
            [candidate],
            [],
            default);

        Assert.Equal(ZaloTeamPreferenceExitMeaningKind.SuggestExit, result.Kind);
        Assert.Equal("user-toan", result.TargetZaloUserId);
        Assert.True(result.Confidence >= .9);
        Assert.Single(gateway.Requests);
        Assert.Contains("KHÔNG quyết định ai có quyền kick ai", gateway.Requests[0].Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_fails_closed_without_executable_semantics()
    {
        var gateway = new StubGateway(new ZaloAiCompletionResult(
            false,
            null,
            ZaloAiFailureKind.RateLimited,
            "test",
            "test-model",
            1,
            429,
            TimeSpan.Zero,
            false));
        var interpreter = new ZaloTeamPreferenceExitSemanticInterpreter(
            Configuration(), gateway, NullLogger.Instance);

        var result = await interpreter.InterpretAsync(
            "conn-2",
            "g2",
            "user-long-2",
            "tui ghét To An, né nó ra dùm",
            new ZaloReadOnlyConversationContext([], []),
            [Candidate("conn-2")],
            [],
            default);

        Assert.Equal(ZaloTeamPreferenceExitMeaningKind.Unknown, result.Kind);
        Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData("tui muốn chung team với To An")]
    [InlineData("xếp tui với To An chung team nha")]
    [InlineData("To An đánh hay ghê")]
    public void Existing_positive_or_ordinary_language_is_not_exit_candidate(string text)
    {
        Assert.False(ZaloTeamPreferenceExitSemanticInterpreter.LooksPotentialExitLanguage(text));
    }

    [Theory]
    [InlineData("tui không muốn chung team với To An nữa")]
    [InlineData("tui k thích chơi với To An")]
    [InlineData("tui ghét thằng To An, giành banh hoài")]
    [InlineData("né To An ra dùm")]
    public void Exit_or_dislike_language_is_candidate_for_read_only_semantic_check(string text)
    {
        Assert.True(ZaloTeamPreferenceExitSemanticInterpreter.LooksPotentialExitLanguage(text));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZaloBot:TeamPreferenceExit:AiEnabled"] = "true",
            ["ZaloBot:AiMaxUserCallsPerMinute"] = "60",
            ["ZaloBot:AiMaxGroupCallsPerMinute"] = "300"
        })
        .Build();

    private static ZaloTeamPreferenceExitCandidate Candidate(string connectionSuffix = "") => new(
        "session-t4" + connectionSuffix,
        "T4 16/09 17:30 - thứ 4",
        DateTimeOffset.UtcNow.AddDays(2),
        VolleyDraft.Api.Models.SessionStatus.Setup,
        "pref-1" + connectionSuffix,
        "sp-long" + connectionSuffix,
        "Thanh Long",
        [
            new("sp-long" + connectionSuffix, "user-long" + (connectionSuffix.Length == 0 ? string.Empty : "-2"), "Thanh Long"),
            new("sp-toan" + connectionSuffix, "user-toan", "To An")
        ],
        true);

    private sealed class StubGateway(ZaloAiCompletionResult result) : IZaloAiGateway
    {
        public bool IsConfigured => true;
        public List<ZaloAiCompletionRequest> Requests { get; } = [];

        public Task<ZaloAiCompletionResult> CompleteAsync(
            ZaloAiCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
