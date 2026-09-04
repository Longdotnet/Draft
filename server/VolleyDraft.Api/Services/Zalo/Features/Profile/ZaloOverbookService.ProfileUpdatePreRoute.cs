using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Priority mutation lane used by the ingress coordinator before read-only/status
    /// pre-routing. Keeping this explicit prevents a phrase such as "cập nhật ... T6"
    /// from being consumed as Match Brief while preserving all normal AI/chat routes.
    /// </summary>
    public Task<bool> TryHandleZaloProfileUpdatePreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default) =>
        TryHandleProfileUpdateConversationAsync(incoming, cancellationToken);
}
