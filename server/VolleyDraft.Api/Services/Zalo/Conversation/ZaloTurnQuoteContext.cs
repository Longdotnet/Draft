using System.Diagnostics;
using System.Runtime.CompilerServices;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Request-turn quote context shared across async service boundaries. ASP.NET keeps
/// one Activity instance for the request, so a quote observed in the pre-routing
/// service remains visible when control returns to the endpoint and enters the main
/// bot/AI path. A ConditionalWeakTable avoids keeping completed request activities
/// alive. AsyncLocal is only a fallback for direct/manual calls without an Activity.
/// </summary>
public static class ZaloTurnQuoteContext
{
    private sealed record State(string SenderZaloUserId, ZaloQuotedSemanticContext? Quote);
    private sealed class Holder
    {
        public State? Value { get; set; }
    }

    private static readonly ConditionalWeakTable<Activity, Holder> RequestStates = new();
    private static readonly AsyncLocal<State?> FallbackState = new();

    public static void Set(ZaloIncomingMessageEvent incoming)
    {
        var senderId = Clean(incoming.SenderId);
        var quote = incoming.Quote is null ? null : ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        var state = new State(senderId, quote);
        if (Activity.Current is { } activity)
            RequestStates.GetValue(activity, static _ => new Holder()).Value = state;
        else
            FallbackState.Value = state;

        // During incremental migration, existing member-targeting handlers already
        // understand structured mention UIDs. Project only an explicit deictic quote
        // ("ông này", "anh đó"...) into that safe UID path without changing text.
        ZaloIncomingIdentityEnricher.TryAddQuotedPersonMention(incoming);
    }

    public static ZaloQuotedSemanticContext? GetFor(ZaloAiSender sender)
    {
        var state = Activity.Current is { } activity && RequestStates.TryGetValue(activity, out var holder)
            ? holder.Value
            : FallbackState.Value;
        if (state is null || string.IsNullOrWhiteSpace(sender.Id)) return null;
        return string.Equals(state.SenderZaloUserId, sender.Id.Trim(), StringComparison.Ordinal)
            ? state.Quote
            : null;
    }

    public static void Clear()
    {
        if (Activity.Current is { } activity && RequestStates.TryGetValue(activity, out var holder))
            holder.Value = null;
        FallbackState.Value = null;
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
