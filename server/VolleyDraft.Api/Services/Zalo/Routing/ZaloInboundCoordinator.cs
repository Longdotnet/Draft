using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Production ingress boundary for Zalo message turns while legacy facades are being
/// strangled into explicit feature modules. The endpoint must dispatch through this
/// coordinator instead of knowing the ordering between pre-routing lanes and Bot.
///
/// Identity correction is evaluated first because it is a short, explicitly-confirmed
/// recovery conversation for a stable UID. Once that lane handles a turn, no other
/// feature may interpret the same numeric choice or send a second reply.
/// </summary>
public sealed class ZaloInboundCoordinator(
    ZaloOverbookService overbookService,
    ZaloBotService botService,
    VolleyDraftDbContext db,
    ZaloIntegrationService integration,
    ZaloBridgeClient bridge)
{
    public async Task<ZaloInboundHandlingResult> HandleAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var identity = await new ZaloIdentityCorrectionConversation(db, integration)
            .TryHandleAsync(incoming, cancellationToken);
        if (identity.Handled)
        {
            if (!string.IsNullOrWhiteSpace(identity.Response))
            {
                var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
                var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
                var messageId = ZaloOverbookLogic.NormalizeId(incoming.MessageId);
                await bridge.SendGroupMessageAsync(
                    accountId,
                    groupId,
                    identity.Response!,
                    [],
                    idempotencyKey: $"identity-correction:{accountId}:{messageId}");
            }
            return new(true, "identity-correction");
        }

        return await DispatchAsync(
            incoming,
            overbookService.TryHandleZaloConfirmationAsync,
            async (message, token) => await botService.HandleIncomingAsync(message, token),
            cancellationToken);
    }

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
