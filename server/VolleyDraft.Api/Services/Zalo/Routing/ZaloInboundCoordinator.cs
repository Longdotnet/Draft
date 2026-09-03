using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Production ingress boundary for Zalo message turns while legacy facades are being
/// strangled into explicit feature modules. The endpoint must dispatch through this
/// coordinator instead of knowing the ordering between Overbook and Bot lanes.
///
/// This class intentionally contains no intent matching or feature logic. It preserves
/// the current compatibility precedence and enforces a single terminal owner per turn:
/// once the Overbook lane handles a message, the generic Bot lane is never invoked.
/// </summary>
public sealed class ZaloInboundCoordinator(
    ZaloOverbookService overbookService,
    ZaloBotService botService)
{
    public Task<ZaloInboundHandlingResult> HandleAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            incoming,
            overbookService.TryHandleZaloConfirmationAsync,
            async (message, token) => await botService.HandleIncomingAsync(message, token),
            cancellationToken);

    internal static async Task<ZaloInboundHandlingResult> DispatchAsync(
        ZaloIncomingMessageEvent incoming,
        Func<ZaloIncomingMessageEvent, CancellationToken, Task<bool>> tryHandleOverbook,
        Func<ZaloIncomingMessageEvent, CancellationToken, Task> handleBot,
        CancellationToken cancellationToken = default)
    {
        if (await tryHandleOverbook(incoming, cancellationToken))
            return new(true, "overbook-confirmation");

        await handleBot(incoming, cancellationToken);
        return new(true, "bot");
    }
}

public sealed record ZaloInboundHandlingResult(
    bool Accepted,
    string HandledBy);
