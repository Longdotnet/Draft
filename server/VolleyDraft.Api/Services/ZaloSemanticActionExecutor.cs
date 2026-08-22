using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Executes already-grounded targets one at a time by re-entering the existing
/// deterministic member-assist/open-offer services. The semantic plan never writes
/// database state directly and one rejected target does not block another target.
/// </summary>
internal sealed class ZaloSemanticActionExecutor(VolleyDraftDbContext db)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<ZaloSemanticActionExecutionResult> ExecuteAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionPlanValidationResult validation,
        ZaloActionGroundingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ZaloSemanticActionTargetResult>(validation.Targets.Count);
        foreach (var validated in validation.Targets)
        {
            if (!validated.Executable)
            {
                results.Add(new ZaloSemanticActionTargetResult(
                    validated.Target,
                    validated.Code is "ExplicitExclude" or "Uncertain" or "TargetLowConfidence"
                        ? ZaloSemanticActionExecutionStatus.Skipped
                        : ZaloSemanticActionExecutionStatus.Rejected,
                    validated.Code,
                    null,
                    validated.Target.SessionId,
                    validated.Target.OpenOfferId));
                continue;
            }

            var result = validation.Plan.Action switch
            {
                ZaloSemanticActionKind.PassOwnSlot => await ExecutePassAsync(
                    connectionId, groupId, incoming, validated.Target, snapshot, cancellationToken),
                ZaloSemanticActionKind.ClaimOpenSlot => await ExecuteClaimAsync(
                    connectionId, groupId, incoming, validated.Target, snapshot, cancellationToken),
                ZaloSemanticActionKind.CancelPass => await ExecuteCancelPassAsync(
                    connectionId, groupId, incoming, validated.Target, snapshot, cancellationToken),
                ZaloSemanticActionKind.CancelClaim => await ExecuteCancelClaimAsync(
                    connectionId, groupId, incoming, validated.Target, cancellationToken),
                ZaloSemanticActionKind.ConfirmClaim => await ExecuteConfirmClaimAsync(
                    connectionId, groupId, incoming, validated.Target, cancellationToken),
                _ => Rejected(validated.Target, "ActionNotAllowed", null)
            };
            results.Add(result);
        }

        return new ZaloSemanticActionExecutionResult(validation.Plan.Action, results);
    }

    private async Task<ZaloSemanticActionTargetResult> ExecutePassAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var session = snapshot.Sessions.FirstOrDefault(item =>
            string.Equals(item.SessionId, target.SessionId, StringComparison.Ordinal));
        if (session is null) return Rejected(target, "SessionNotConfigured", null);

        var selector = session.StartTime is { } start
            ? start.ToOffset(VietnamOffset).ToString("dd/MM/yyyy")
            : session.Name;
        var promoted = incoming with
        {
            Content = $"tui pass slot {selector}",
            Mentions = incoming.Mentions
                .Where(mention => !ZaloAmbientDomainIntentPromotion.IsBroadcastMention(incoming, mention))
                .ToArray()
        };
        var reply = await new ZaloMemberAssistService(db).TryBuildAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        if (reply is null || reply.Kind != ZaloMemberAssistKind.PassSlotHelp ||
            !string.Equals(reply.SessionId, session.SessionId, StringComparison.Ordinal))
            return Rejected(target, "DomainRejected", reply?.Text);

        return Success(target, "OfferOpened", reply.Text, session.SessionId, null);
    }

    private async Task<ZaloSemanticActionTargetResult> ExecuteClaimAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var offer = snapshot.OpenSlotOffers.FirstOrDefault(item =>
            string.Equals(item.OfferId, target.OpenOfferId, StringComparison.Ordinal));
        if (offer is null) return Rejected(target, "NoGroundedOpenOffer", null);

        // The owner mention is synthetic routing metadata only. The existing service
        // still reloads the live offer/session and checks ownership/state before claim.
        var promoted = incoming with
        {
            Content = "tui nhận",
            Mentions = [new ZaloBridgeMention(offer.OwnerZaloUserId, 0, 0)]
        };
        var reply = await new ZaloOpenSlotOfferService(db).TryHandleAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        var pending = await new ZaloOpenSlotOfferStore(db).LoadPendingClaimAsync(
            connectionId,
            groupId,
            Clean(incoming.SenderId),
            cancellationToken);
        if (pending is not null && string.Equals(pending.Id, offer.OfferId, StringComparison.Ordinal))
            return Success(target, "ClaimPending", reply.Response, offer.SessionId, offer.OfferId);

        return Rejected(target, "DomainRejected", reply.Response);
    }

    private async Task<ZaloSemanticActionTargetResult> ExecuteCancelPassAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var offer = snapshot.OpenSlotOffers.FirstOrDefault(item =>
            string.Equals(item.OfferId, target.OpenOfferId, StringComparison.Ordinal));
        if (offer is null) return Rejected(target, "NoOwnedOpenOffer", null);

        var promoted = incoming with
        {
            Content = $"hủy pass {offer.SessionName}",
            Mentions = []
        };
        var reply = await new ZaloOpenSlotOfferService(db).TryHandleAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        var active = await new ZaloOpenSlotOfferStore(db).ListOwnedActiveAsync(
            connectionId,
            groupId,
            Clean(incoming.SenderId),
            cancellationToken);
        if (active.All(item => !string.Equals(item.Id, offer.OfferId, StringComparison.Ordinal)))
            return Success(target, "OfferCancelled", reply.Response, offer.SessionId, offer.OfferId);

        return Rejected(target, "DomainRejected", reply.Response);
    }

    private async Task<ZaloSemanticActionTargetResult> ExecuteCancelClaimAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionTarget target,
        CancellationToken cancellationToken)
    {
        var promoted = incoming with { Content = "hủy", Mentions = [] };
        var reply = await new ZaloOpenSlotOfferService(db).TryHandleAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        var pending = await new ZaloOpenSlotOfferStore(db).LoadPendingClaimAsync(
            connectionId,
            groupId,
            Clean(incoming.SenderId),
            cancellationToken);
        if (pending is null || !string.Equals(pending.Id, target.OpenOfferId, StringComparison.Ordinal))
            return Success(target, "ClaimCancelled", reply.Response, target.SessionId, target.OpenOfferId);

        return Rejected(target, "DomainRejected", reply.Response);
    }

    private async Task<ZaloSemanticActionTargetResult> ExecuteConfirmClaimAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloSemanticActionTarget target,
        CancellationToken cancellationToken)
    {
        var promoted = incoming with { Content = "chốt", Mentions = [] };
        var reply = await new ZaloOpenSlotOfferService(db).TryHandleAsync(
            connectionId,
            groupId,
            promoted,
            cancellationToken);
        var pending = await new ZaloOpenSlotOfferStore(db).LoadPendingClaimAsync(
            connectionId,
            groupId,
            Clean(incoming.SenderId),
            cancellationToken);
        if (reply.Handled && (pending is null || !string.Equals(pending.Id, target.OpenOfferId, StringComparison.Ordinal)))
            return Success(target, "ClaimConfirmed", reply.Response, target.SessionId, target.OpenOfferId);

        return Rejected(target, "ClaimStillPending", reply.Response);
    }

    private static ZaloSemanticActionTargetResult Success(
        ZaloSemanticActionTarget target,
        string code,
        string? message,
        string? sessionId,
        string? offerId) =>
        new(target, ZaloSemanticActionExecutionStatus.Success, code, message, sessionId, offerId);

    private static ZaloSemanticActionTargetResult Rejected(
        ZaloSemanticActionTarget target,
        string code,
        string? message) =>
        new(target, ZaloSemanticActionExecutionStatus.Rejected, code, message, target.SessionId, target.OpenOfferId);

    private static string Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}

internal static class ZaloGroundedActionResultComposer
{
    public static string Compose(ZaloSemanticActionExecutionResult execution)
    {
        var lines = new List<string>();
        foreach (var result in execution.Results)
        {
            if (result.Status == ZaloSemanticActionExecutionStatus.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Message)) lines.Add(result.Message.Trim());
                continue;
            }

            if (result.Status == ZaloSemanticActionExecutionStatus.Skipped)
            {
                if (result.Code == "Uncertain")
                    lines.Add($"Còn {Label(result.Target)} ông chưa chắc nên tui không đụng nha.");
                continue;
            }

            lines.Add(ComposeFailure(execution.Action, result));
        }

        return lines.Count == 0
            ? "Oke, phần ông nói giữ nguyên thì tui không đụng gì nha."
            : string.Join("\n", lines.Distinct(StringComparer.Ordinal));
    }

    private static string ComposeFailure(
        ZaloSemanticActionKind action,
        ZaloSemanticActionTargetResult result)
    {
        var label = Label(result.Target);
        return result.Code switch
        {
            "SessionNotConfigured" =>
                $"Còn {label} tui chưa thấy kèo được tạo trên hệ thống nên chưa làm được nha.",
            "NoGroundedOpenOffer" =>
                $"Tui hiểu ông muốn nhận {label}, nhưng hiện chưa có slot pass thật trên hệ thống để giữ nha.",
            "SenderDoesNotOwnSlot" =>
                $"Tui hiểu ông muốn pass {label}, nhưng hiện tui không xác minh được slot đó là của ông nên chưa mở nha.",
            "NoOwnedOpenOffer" =>
                $"Tui chưa thấy offer pass của ông ở {label} để huỷ nha.",
            "NoPendingClaim" =>
                $"Tui chưa thấy claim đang giữ ở {label} để xử lý nha.",
            "TargetLowConfidence" or "TargetAmbiguous" =>
                $"Phần {label} chưa đủ rõ nên tui chưa đụng dữ liệu nha.",
            "DomainRejected" or "ClaimStillPending" when !string.IsNullOrWhiteSpace(result.Message) =>
                result.Message!,
            _ when !string.IsNullOrWhiteSpace(result.Message) => result.Message!,
            _ => action == ZaloSemanticActionKind.ClaimOpenSlot
                ? $"Tui hiểu ông muốn nhận {label}, nhưng dữ liệu thật chưa cho phép nên tui chưa giữ slot nha."
                : $"Tui hiểu ý ở {label}, nhưng dữ liệu thật chưa cho phép nên tui chưa làm gì nha."
        };
    }

    private static string Label(ZaloSemanticActionTarget target)
    {
        var reference = string.IsNullOrWhiteSpace(target.ReferenceText) ? "target này" : target.ReferenceText.Trim();
        if (string.IsNullOrWhiteSpace(target.ResolvedDate)) return reference;
        if (reference.Contains(target.ResolvedDate, StringComparison.Ordinal)) return reference;
        return $"{reference} ({target.ResolvedDate})";
    }
}
