using System.Globalization;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Authoritative structural grounding boundary between AI meaning and domain execution.
/// Fabricated IDs reject the whole plan. Legitimate semantic targets that simply do
/// not exist in configured state are retained as non-executable target results so one
/// missing target cannot roll back another valid target.
/// </summary>
internal static class ZaloSemanticActionPlanValidator
{
    private static readonly HashSet<ZaloSemanticActionKind> AllowedActions =
    [
        ZaloSemanticActionKind.PassOwnSlot,
        ZaloSemanticActionKind.ClaimOpenSlot,
        ZaloSemanticActionKind.CancelPass,
        ZaloSemanticActionKind.CancelClaim,
        ZaloSemanticActionKind.ConfirmClaim
    ];

    public static ZaloSemanticActionPlanValidationResult Validate(
        ZaloSemanticActionPlan plan,
        ZaloIncomingMessageEvent incoming,
        ZaloActionGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (plan.Route == ZaloSemanticActionRoute.ReadOnlyQuestion)
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_readonly_question");
        if (plan.Route != ZaloSemanticActionRoute.MutationRequest)
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_not_mutation");
        if (!AllowedActions.Contains(plan.Action))
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_not_allowed");
        if (plan.Confidence < settings.MinimumConfidence)
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_low_confidence");
        if (plan.ActorKind != ZaloSemanticActionActorKind.CurrentSender)
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_actor");
        if (!string.IsNullOrWhiteSpace(plan.ActorMemberId) &&
            !string.Equals(plan.ActorMemberId, snapshot.CurrentSender.MemberId, StringComparison.Ordinal))
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_actor");
        if (!string.Equals(Clean(incoming.SenderId), snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal))
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_actor");
        if (plan.Targets.Count is < 1 or > 8)
            return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_target_ambiguous");

        // First pass: reject model-fabricated identifiers before any target is allowed
        // to execute. A missing real-world target is represented by a null ID instead.
        foreach (var target in plan.Targets)
        {
            if (target.SessionId is not null &&
                !snapshot.Sessions.Any(session => string.Equals(session.SessionId, target.SessionId, StringComparison.Ordinal)))
                return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_session");

            if (target.ReferencedMemberId is not null &&
                !snapshot.Members.Any(member => string.Equals(member.MemberId, target.ReferencedMemberId, StringComparison.Ordinal)))
                return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_member");

            if (target.OpenOfferId is not null &&
                !snapshot.OpenSlotOffers.Any(offer => string.Equals(offer.OfferId, target.OpenOfferId, StringComparison.Ordinal)))
                return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_offer");

            if (target.ResolvedDate is not null && !TryParseDate(target.ResolvedDate, out _))
                return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_target_ambiguous");

            if (target.SessionId is not null && target.ResolvedDate is not null)
            {
                var groundedSession = snapshot.Sessions.Single(session =>
                    string.Equals(session.SessionId, target.SessionId, StringComparison.Ordinal));
                if (groundedSession.LocalDate is not null &&
                    !string.Equals(groundedSession.LocalDate, target.ResolvedDate, StringComparison.Ordinal))
                    return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_session");
            }

            if (target.OpenOfferId is not null)
            {
                var groundedOffer = snapshot.OpenSlotOffers.Single(offer =>
                    string.Equals(offer.OfferId, target.OpenOfferId, StringComparison.Ordinal));
                if (target.SessionId is not null &&
                    !string.Equals(groundedOffer.SessionId, target.SessionId, StringComparison.Ordinal))
                    return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_offer");
                if (target.ReferencedMemberId is not null &&
                    !MemberMatchesZalo(snapshot, target.ReferencedMemberId, groundedOffer.OwnerZaloUserId))
                    return ZaloSemanticActionPlanValidationResult.Reject(plan, "semantic_action_invalid_offer");
            }
        }

        var validated = plan.Targets
            .Select(target => ValidateTarget(plan.Action, target, snapshot, settings))
            .ToArray();
        return ZaloSemanticActionPlanValidationResult.Accept(plan, validated);
    }

    private static ZaloSemanticActionValidatedTarget ValidateTarget(
        ZaloSemanticActionKind action,
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot,
        ZaloSemanticActionSettings settings)
    {
        if (target.Disposition == ZaloSemanticActionTargetDisposition.Exclude)
            return new(target, false, "ExplicitExclude");
        if (target.Disposition == ZaloSemanticActionTargetDisposition.Uncertain)
            return new(target, false, "Uncertain");
        if (target.Confidence < settings.MinimumConfidence)
            return new(target, false, "TargetLowConfidence");

        return action switch
        {
            ZaloSemanticActionKind.PassOwnSlot => ValidatePass(target, snapshot),
            ZaloSemanticActionKind.ClaimOpenSlot => ValidateClaim(target, snapshot),
            ZaloSemanticActionKind.CancelPass => ValidateCancelPass(target, snapshot),
            ZaloSemanticActionKind.CancelClaim => ValidateClaimContinuation(target, snapshot),
            ZaloSemanticActionKind.ConfirmClaim => ValidateClaimContinuation(target, snapshot),
            _ => new(target, false, "ActionNotAllowed")
        };
    }

    private static ZaloSemanticActionValidatedTarget ValidatePass(
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot)
    {
        if (target.SessionId is null)
            return new(target, false, target.ResolvedDate is null ? "TargetAmbiguous" : "SessionNotConfigured");
        if (!snapshot.CurrentSender.OwnedSessionIds.Contains(target.SessionId, StringComparer.Ordinal))
            return new(target, false, "SenderDoesNotOwnSlot");
        return new(target, true, "Ready");
    }

    private static ZaloSemanticActionValidatedTarget ValidateClaim(
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot)
    {
        var normalized = target;
        var offer = target.OpenOfferId is null
            ? ResolveUniqueOffer(
                snapshot,
                target,
                offer => string.Equals(offer.Status, ZaloOpenSlotOfferStatus.Open.ToString(), StringComparison.Ordinal) &&
                         !string.Equals(offer.OwnerZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal))
            : snapshot.OpenSlotOffers.Single(offer => string.Equals(offer.OfferId, target.OpenOfferId, StringComparison.Ordinal));

        if (offer is null)
            return new(target, false, "NoGroundedOpenOffer");
        if (!string.Equals(offer.Status, ZaloOpenSlotOfferStatus.Open.ToString(), StringComparison.Ordinal))
            return new(target, false, "OpenOfferNotClaimable");
        if (string.Equals(offer.OwnerZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal))
            return new(target, false, "CannotClaimOwnSlot");

        normalized = normalized with
        {
            OpenOfferId = offer.OfferId,
            SessionId = normalized.SessionId ?? offer.SessionId
        };
        return new(normalized, true, "Ready");
    }

    private static ZaloSemanticActionValidatedTarget ValidateCancelPass(
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot)
    {
        var offer = target.OpenOfferId is null
            ? ResolveUniqueOffer(
                snapshot,
                target,
                item => string.Equals(item.OwnerZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal) &&
                        item.Status is "Open" or "ClaimPending" or "Applying")
            : snapshot.OpenSlotOffers.Single(item => string.Equals(item.OfferId, target.OpenOfferId, StringComparison.Ordinal));

        if (offer is null) return new(target, false, "NoOwnedOpenOffer");
        if (!string.Equals(offer.OwnerZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal))
            return new(target, false, "OfferNotOwnedBySender");

        return new(target with
        {
            OpenOfferId = offer.OfferId,
            SessionId = target.SessionId ?? offer.SessionId
        }, true, "Ready");
    }

    private static ZaloSemanticActionValidatedTarget ValidateClaimContinuation(
        ZaloSemanticActionTarget target,
        ZaloActionGroundingSnapshot snapshot)
    {
        var offer = target.OpenOfferId is null
            ? ResolveUniqueOffer(
                snapshot,
                target,
                item => string.Equals(item.ClaimantZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal) &&
                        item.Status is "ClaimPending" or "Applying")
            : snapshot.OpenSlotOffers.Single(item => string.Equals(item.OfferId, target.OpenOfferId, StringComparison.Ordinal));

        if (offer is null) return new(target, false, "NoPendingClaim");
        if (!string.Equals(offer.ClaimantZaloUserId, snapshot.CurrentSender.ZaloUserId, StringComparison.Ordinal))
            return new(target, false, "ClaimNotOwnedBySender");
        if (offer.Status is not "ClaimPending" and not "Applying")
            return new(target, false, "NoPendingClaim");

        return new(target with
        {
            OpenOfferId = offer.OfferId,
            SessionId = target.SessionId ?? offer.SessionId
        }, true, "Ready");
    }

    private static ZaloActionGroundingOffer? ResolveUniqueOffer(
        ZaloActionGroundingSnapshot snapshot,
        ZaloSemanticActionTarget target,
        Func<ZaloActionGroundingOffer, bool> predicate)
    {
        var candidates = snapshot.OpenSlotOffers.Where(predicate);
        if (target.SessionId is not null)
            candidates = candidates.Where(offer => string.Equals(offer.SessionId, target.SessionId, StringComparison.Ordinal));
        if (target.ReferencedMemberId is not null)
            candidates = candidates.Where(offer => MemberMatchesZalo(snapshot, target.ReferencedMemberId, offer.OwnerZaloUserId));
        var matches = candidates.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool MemberMatchesZalo(
        ZaloActionGroundingSnapshot snapshot,
        string memberId,
        string zaloUserId) =>
        snapshot.Members.Any(member =>
            string.Equals(member.MemberId, memberId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(member.ZaloUserId) &&
            string.Equals(Clean(member.ZaloUserId), Clean(zaloUserId), StringComparison.Ordinal));

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
