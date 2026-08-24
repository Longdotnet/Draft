namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    // Keep the public worker entry point stable while the real-world conversation
    // engine lives in its own file. This makes the safety/routing policy independently
    // testable without changing the scheduler contract.
    public Task<int> ProcessMissingProfileRepliesDueAsync(
        CancellationToken cancellationToken = default) =>
        ProcessMissingProfileRepliesDueV2Async(cancellationToken);
}
