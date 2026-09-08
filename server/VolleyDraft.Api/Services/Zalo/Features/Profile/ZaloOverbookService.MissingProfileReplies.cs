namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    // Context-first semantic interpretation gets first chance on targeted profile
    // replies so users can speak naturally. Anything uncertain/unconfigured remains
    // untouched and falls through to the hardened deterministic V2 parser.
    public async Task<int> ProcessMissingProfileRepliesDueAsync(
        CancellationToken cancellationToken = default)
    {
        var cycleStartedAt = DateTimeOffset.UtcNow;
        var promptStore = new ZaloMissingProfilePromptStore(db);
        var activeAtCycleStart = await promptStore.GetActiveAsync(cycleStartedAt, 100, cancellationToken);

        var semanticHandled = await ProcessMissingProfileRepliesContextFirstAsync(cancellationToken);
        var deterministicHandled = await ProcessMissingProfileRepliesDueV2Async(cancellationToken);
        var handled = semanticHandled + deterministicHandled;

        if (handled > 0 && activeAtCycleStart.Count > 0)
        {
            await SendProfileCompletionReadinessFollowUpsAsync(
                activeAtCycleStart,
                cycleStartedAt,
                cancellationToken);
        }

        return handled;
    }
}
