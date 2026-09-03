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
    public async Task<ZaloInboundHandlingResult> HandleAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (await overbookService.TryHandleZaloConfirmationAsync(incoming, cancellationToken))
            return new(true, "overbook-confirmation");

        await botService.HandleIncomingAsync(incoming, cancellationToken);
        return new(true, "bot");
    }
}

public sealed record ZaloInboundHandlingResult(
    bool Accepted,
    string HandledBy);
