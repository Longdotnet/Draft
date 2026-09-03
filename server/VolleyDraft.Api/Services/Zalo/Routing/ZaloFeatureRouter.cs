namespace VolleyDraft.Api.Services.Zalo.Routing;

/// <summary>
/// Selects exactly one feature module for an inbound turn.
/// Deterministic matches outrank model-assisted matches. Equal top candidates fail closed
/// instead of allowing multiple feature handlers to reply to the same message.
/// </summary>
public sealed class ZaloFeatureRouter(IEnumerable<IZaloFeatureModule> modules)
{
    private readonly IReadOnlyList<IZaloFeatureModule> _modules = modules.ToList();

    public async Task<ZaloFeatureRouteResult> RouteAsync(
        ZaloFeatureTurn turn,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(IZaloFeatureModule Module, ZaloFeatureMatch Match)>();

        foreach (var module in _modules)
        {
            var match = await module.MatchAsync(turn, cancellationToken);
            if (match is null || match.NormalizedScore <= 0)
                continue;

            candidates.Add((module, match));
        }

        if (candidates.Count == 0)
            return new(false, null, "no_feature_match");

        var ordered = candidates
            .OrderByDescending(item => item.Match.Deterministic)
            .ThenByDescending(item => item.Match.NormalizedScore)
            .ThenByDescending(item => item.Module.Priority)
            .ThenBy(item => item.Module.Feature)
            .ToList();

        var winner = ordered[0];
        if (ordered.Count > 1)
        {
            var runnerUp = ordered[1];
            var sameRank = winner.Match.Deterministic == runnerUp.Match.Deterministic &&
                           winner.Match.NormalizedScore == runnerUp.Match.NormalizedScore &&
                           winner.Module.Priority == runnerUp.Module.Priority;
            if (sameRank)
            {
                return new(
                    false,
                    null,
                    $"ambiguous_feature_match:{winner.Module.Feature}:{runnerUp.Module.Feature}",
                    Ambiguous: true);
            }
        }

        var execution = await winner.Module.HandleAsync(turn, cancellationToken);
        return new(
            execution.Handled,
            winner.Module.Feature,
            execution.Reason,
            Ambiguous: false);
    }
}
