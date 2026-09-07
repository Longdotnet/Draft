using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Production ingress boundary for Zalo message turns while legacy facades are being
/// strangled into explicit feature modules. The endpoint must dispatch through this
/// coordinator instead of knowing the ordering between Overbook and Bot lanes.
///
/// Idempotency is owned here, before any feature/pre-routing lane can mutate state or
/// emit a reply. The coordinator reuses ZaloGroupMessage as the durable delivery ledger
/// so duplicate bridge deliveries remain suppressed across concurrent requests and
/// process restarts. Once the Overbook/pre-routing lane handles a message, the generic
/// Bot lane is never invoked.
///
/// Durable tracked-group ownership also applies to message capture. A configured group
/// remains an ingress target even when it currently has no bot-enabled MatchSession, so
/// Member Intelligence does not lose realtime message activity merely because the match
/// lifecycle is temporarily empty. Bot/session feature lanes may still decline the turn.
/// </summary>
public sealed class ZaloInboundCoordinator(
    VolleyDraftDbContext db,
    ZaloOverbookService overbookService,
    ZaloBotService botService,
    ILogger<ZaloInboundCoordinator> logger)
{
    private const string PreRouteHandledOutcome = "pre_route_handled";
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);

    public Task<ZaloInboundHandlingResult> HandleAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default) =>
        DispatchClaimedAsync(
            incoming,
            TryClaimAsync,
            async (message, token) =>
                await overbookService.TryHandleZaloProfileUpdatePreRouteAsync(message, token) ||
                await overbookService.TryHandlePassSlotGuidancePreRouteAsync(message, token) ||
                await overbookService.TryHandleAddressedOpenSlotOfferPreRouteAsync(message, token) ||
                await overbookService.TryHandleZaloPreRouteAsync(message, token),
            async (message, token) => await botService.HandleIncomingAsync(message, token),
            CompletePreRouteAsync,
            ReleaseAsync,
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

    internal static async Task<ZaloInboundHandlingResult> DispatchClaimedAsync(
        ZaloIncomingMessageEvent incoming,
        Func<ZaloIncomingMessageEvent, CancellationToken, Task<ZaloInboundClaim>> tryClaim,
        Func<ZaloIncomingMessageEvent, CancellationToken, Task<bool>> tryHandleOverbook,
        Func<ZaloIncomingMessageEvent, CancellationToken, Task> handleBot,
        Func<ZaloInboundClaim, CancellationToken, Task> completePreRoute,
        Func<ZaloInboundClaim, CancellationToken, Task> release,
        CancellationToken cancellationToken = default)
    {
        var claim = await tryClaim(incoming, cancellationToken);
        if (claim.IsDuplicate)
            return new(true, "duplicate");

        if (!claim.IsTracked)
            return await DispatchAsync(incoming, tryHandleOverbook, handleBot, cancellationToken);

        try
        {
            if (await tryHandleOverbook(incoming, cancellationToken))
            {
                await completePreRoute(claim, cancellationToken);
                return new(true, "overbook-confirmation");
            }

            // The Bot lane already owns its own durable reply lease on ZaloGroupMessage.
            // Release the ingress lease before handing off so Bot can claim the same row.
            await release(claim, cancellationToken);
            await handleBot(incoming, cancellationToken);
            return new(true, "bot");
        }
        catch
        {
            // Cleanup must not inherit an already-cancelled request token; otherwise a
            // client disconnect can leave the ingress lease stuck until stale recovery.
            await release(claim, CancellationToken.None);
            throw;
        }
    }

    private Task<ZaloInboundClaim> TryClaimAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken) =>
        TryClaimTrackedAsync(db, logger, incoming, cancellationToken);

    internal static async Task<ZaloInboundClaim> TryClaimTrackedAsync(
        VolleyDraftDbContext db,
        ILogger<ZaloInboundCoordinator> logger,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var messageId = NormalizeId(incoming.MessageId);
        if (accountId.Length == 0 || groupId.Length == 0 || messageId.Length == 0)
            return ZaloInboundClaim.Untracked;

        // Tracked groups are durable configuration and outlive MatchSession lifecycle.
        // Resolve the inbound provider account/group through the same canonical target
        // boundary used by proactive and realtime poll processing. MatchSessions remain
        // only the resolver's compatibility fallback for installations not seeded yet.
        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(accountId, groupId, cancellationToken);
        if (target is null)
            return ZaloInboundClaim.Untracked;

        var canonicalGroupId = target.GroupId;
        var storedMessage = await db.ZaloGroupMessages.SingleOrDefaultAsync(message =>
            message.ZaloConnectionId == target.ConnectionId && message.MessageId == messageId, cancellationToken);
        if (storedMessage is null)
        {
            var now = DateTimeOffset.UtcNow;
            storedMessage = new ZaloGroupMessage
            {
                ZaloConnectionId = target.ConnectionId,
                GroupId = canonicalGroupId,
                MessageId = messageId,
                SenderId = NormalizeId(incoming.SenderId),
                SenderName = Clean(incoming.SenderName, 160, "Thành viên Zalo"),
                Content = Clean(incoming.Content, 4000, string.Empty),
                IsFromBot = false,
                SentAt = ToSafeTimestamp(incoming.SentAtUnixMs),
                ReceivedAt = now,
                FirstObservedAt = now,
                LastObservedAt = now
            };
            db.ZaloGroupMessages.Add(storedMessage);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                storedMessage = await db.ZaloGroupMessages.SingleAsync(message =>
                    message.ZaloConnectionId == target.ConnectionId && message.MessageId == messageId, cancellationToken);
            }
        }

        if (storedMessage.BotReplySentAt is not null ||
            storedMessage.ReplyOutcome is "throttled" or "no_reply" or PreRouteHandledOutcome)
        {
            logger.LogInformation(
                "Zalo ingress duplicate skipped Account={AccountId} Group={GroupId} Message={MessageId} Outcome={Outcome}",
                target.AccountId,
                canonicalGroupId,
                messageId,
                storedMessage.ReplyOutcome);
            return ZaloInboundClaim.Duplicate;
        }

        var token = Guid.NewGuid().ToString("n");
        var nowUtc = DateTimeOffset.UtcNow;
        var leaseCutoff = nowUtc - ProcessingLease;
        var claimed = await db.ZaloGroupMessages
            .Where(message => message.Id == storedMessage.Id &&
                              message.BotReplySentAt == null &&
                              message.ReplyOutcome != "throttled" &&
                              message.ReplyOutcome != "no_reply" &&
                              message.ReplyOutcome != PreRouteHandledOutcome &&
                              (message.ProcessingStartedAt == null || message.ProcessingStartedAt < leaseCutoff))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, nowUtc)
                .SetProperty(message => message.ProcessingToken, token)
                .SetProperty(message => message.ReplyAttemptCount, message => message.ReplyAttemptCount + 1)
                .SetProperty(message => message.ReplyOutcome, "ingress_processing"), cancellationToken);

        if (claimed == 0)
        {
            logger.LogInformation(
                "Zalo ingress concurrent duplicate skipped Account={AccountId} Group={GroupId} Message={MessageId}",
                target.AccountId,
                canonicalGroupId,
                messageId);
            return ZaloInboundClaim.Duplicate;
        }

        return new(true, false, storedMessage.Id, token);
    }

    private async Task CompletePreRouteAsync(ZaloInboundClaim claim, CancellationToken cancellationToken)
    {
        if (!claim.IsTracked) return;
        await db.ZaloGroupMessages
            .Where(message => message.Id == claim.MessageRowId && message.ProcessingToken == claim.Token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(message => message.ProcessingToken, (string?)null)
                .SetProperty(message => message.SelectedIntent, "pre-route")
                .SetProperty(message => message.ReplyOutcome, PreRouteHandledOutcome), cancellationToken);
    }

    private async Task ReleaseAsync(ZaloInboundClaim claim, CancellationToken cancellationToken)
    {
        if (!claim.IsTracked) return;
        await db.ZaloGroupMessages
            .Where(message => message.Id == claim.MessageRowId && message.ProcessingToken == claim.Token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(message => message.ProcessingToken, (string?)null)
                .SetProperty(message => message.ReplyOutcome, (string?)null), cancellationToken);
    }

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();

    private static string Clean(string? value, int maxLength, string fallback)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length == 0) return fallback;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static DateTimeOffset ToSafeTimestamp(long unixMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

public sealed record ZaloInboundHandlingResult(
    bool Accepted,
    string HandledBy);

internal sealed record ZaloInboundClaim(
    bool IsTracked,
    bool IsDuplicate,
    string? MessageRowId,
    string? Token)
{
    public static ZaloInboundClaim Untracked { get; } = new(false, false, null, null);
    public static ZaloInboundClaim Duplicate { get; } = new(false, true, null, null);
}
