namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Supersedes a reminder-owned request only when the DB state still permits it.
    /// A zero-row update means another instance may have claimed the execution fence;
    /// callers must fail closed and must not clear pending confirmations in that case.
    /// </summary>
    private async Task<bool> TrySupersedeDraftReminderRequestAsync(
        ZaloDraftEscalationStore escalationStore,
        ZaloDraftEscalationSnapshot request,
        MatchSession session,
        CancellationToken cancellationToken)
    {
        var updated = await escalationStore.SetStateAsync(
            request.Id,
            ZaloDraftEscalationState.Superseded,
            cancellationToken);
        if (updated == 0)
            return false;

        if (request.PrimaryApproverId is not null)
        {
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                request.PrimaryApproverId,
                session.Id,
                cancellationToken);
        }

        if (request.SecondaryApproverId is not null)
        {
            await RemoveDraftPendingAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                request.SecondaryApproverId,
                session.Id,
                cancellationToken);
        }

        return true;
    }
}
