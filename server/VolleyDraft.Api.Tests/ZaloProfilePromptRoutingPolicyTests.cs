using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloProfilePromptRoutingPolicyTests
{
    [Fact]
    public void Explicit_session_reference_routes_only_to_that_prompt()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal) { "p-t6" };

        Assert.Equal(
            ZaloProfilePromptRoute.SkipThisPrompt,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t4", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
        Assert.Equal(
            ZaloProfilePromptRoute.Accept,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t6", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
    }

    [Fact]
    public void Bare_profile_answer_with_two_matches_never_mutates_either_prompt()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(
            ZaloProfilePromptRoute.ClarifyOnce,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t4", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
        Assert.Equal(
            ZaloProfilePromptRoute.SkipThisPrompt,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t6", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
    }

    [Fact]
    public void Exact_reply_to_prompt_is_stronger_than_session_ambiguity()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(
            ZaloProfilePromptRoute.Accept,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t6", "p-t4", "p-t6", referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
        Assert.Equal(
            ZaloProfilePromptRoute.SkipThisPrompt,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t4", "p-t4", "p-t6", referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
    }

    [Fact]
    public void Message_referencing_two_matches_asks_once_instead_of_guessing()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal) { "p-t4", "p-t6" };

        Assert.Equal(
            ZaloProfilePromptRoute.ClarifyOnce,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t4", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
        Assert.Equal(
            ZaloProfilePromptRoute.SkipThisPrompt,
            ZaloProfilePromptRoutingPolicy.Resolve(
                "p-t6", "p-t4", null, referenced, 2,
                looksLikeProfileAnswer: true,
                wantsToSkip: false));
    }

    [Theory]
    [InlineData("T6: nam, công", "T6 giao lưu", true)]
    [InlineData("T6: nam, công", "T4 giao lưu", false)]
    [InlineData("CN tui thủ", "Kèo CN", true)]
    public void Session_reference_matching_is_natural(
        string message,
        string sessionName,
        bool expected)
    {
        Assert.Equal(expected, ZaloOverbookService.ReferencesProfileSession(message, sessionName));
    }
}
