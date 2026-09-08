namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    // Context-first semantic interpretation gets first chance on targeted profile
    // replies so users can speak naturally. Anything uncertain/unconfigured remains
    // untouched and falls through to the hardened deterministic V2 parser.
    public async Task<int> ProcessMissingProfileRepliesDueAsync(
        CancellationToken cancellationToken = default)
    {
        var semanticHandled = await ProcessMissingProfileRepliesContextFirstAsync(cancellationToken);
        var deterministicHandled = await ProcessMissingProfileRepliesDueV2Async(cancellationToken);
        return semanticHandled + deterministicHandled;
    }
}
