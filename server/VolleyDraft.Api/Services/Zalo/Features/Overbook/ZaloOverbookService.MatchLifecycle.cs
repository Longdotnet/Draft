using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<ZaloOverbookStatusResponse> AttachMatchLifecycleAsync(
        MatchSession session,
        ZaloOverbookStatusResponse status,
        CancellationToken cancellationToken)
    {
        var lifecycle = await new MatchLifecycleCoordinator(db)
            .GetAsync(session.AdminUserId, session.Id, cancellationToken);
        return lifecycle.IsSuccess && lifecycle.Value is not null
            ? status with { Lifecycle = lifecycle.Value }
            : status;
    }
}
