using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

internal static class ZaloReadOnlySemanticPlanValidator
{
    private static readonly HashSet<ZaloReadOnlyFactKind> AllowedReadOnlyFacts =
    [
        ZaloReadOnlyFactKind.SessionSchedule,
        ZaloReadOnlyFactKind.SelfMembership,
        ZaloReadOnlyFactKind.LocationParking,
        ZaloReadOnlyFactKind.MissingSlots,
        ZaloReadOnlyFactKind.UpcomingSessions,
        ZaloReadOnlyFactKind.Roster,
        ZaloReadOnlyFactKind.WeeklySessionCount,
        ZaloReadOnlyFactKind.TeamLineup,
        ZaloReadOnlyFactKind.ReminderStatus,
        ZaloReadOnlyFactKind.WaitlistStatus,
        ZaloReadOnlyFactKind.MemberTeam,
        ZaloReadOnlyFactKind.MemberMembership,
        ZaloReadOnlyFactKind.CanMemberTakeSlot
    ];

    private static readonly HashSet<ZaloReadOnlyFactKind> SessionRequiredFacts =
    [
        ZaloReadOnlyFactKind.SessionSchedule,
        ZaloReadOnlyFactKind.LocationParking,
        ZaloReadOnlyFactKind.MissingSlots,
        ZaloReadOnlyFactKind.Roster,
        ZaloReadOnlyFactKind.TeamLineup,
        ZaloReadOnlyFactKind.WaitlistStatus,
        ZaloReadOnlyFactKind.MemberTeam,
        ZaloReadOnlyFactKind.MemberMembership,
        ZaloReadOnlyFactKind.CanMemberTakeSlot
    ];

    public static ZaloReadOnlyPlanValidationResult Validate(
        ZaloReadOnlySemanticPlan plan,
        ZaloIncomingMessageEvent incoming,
        ZaloReadOnlyConversationContext context,
        ZaloReadOnlyGroundingSnapshot snapshot,
        ZaloReadOnlySemanticSettings settings)
    {
        if (plan.Route == ZaloReadOnlySemanticRoute.MutationRequest)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_mutation_request");
        if (plan.Route != ZaloReadOnlySemanticRoute.ReadOnlyQuestion)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_not_readonly");
        if (!AllowedReadOnlyFacts.Contains(plan.FactKind))
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_fact_not_allowed");
        if (plan.Confidence < settings.MinimumConfidence)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_low_confidence");
        if (plan.NeedsClarification)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_needs_clarification");

        var normalized = plan;
        if (!string.IsNullOrWhiteSpace(plan.SessionId))
        {
            if (!snapshot.Sessions.Any(session => string.Equals(session.SessionId, plan.SessionId, StringComparison.Ordinal)))
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");
        }
        else if (SessionRequiredFacts.Contains(plan.FactKind))
        {
            if (snapshot.Sessions.Count != 1)
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_ambiguous_session");
            normalized = normalized with { SessionId = snapshot.Sessions[0].SessionId };
        }

        if (!string.IsNullOrWhiteSpace(normalized.SubjectMemberId))
        {
            var subject = snapshot.Members.FirstOrDefault(member =>
                string.Equals(member.MemberId, normalized.SubjectMemberId, StringComparison.Ordinal));
            if (subject is null ||
                (normalized.SessionId is not null && !string.Equals(subject.SessionId, normalized.SessionId, StringComparison.Ordinal)))
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");
        }

        if (!string.IsNullOrWhiteSpace(normalized.ReferencedMemberId))
        {
            var referenced = snapshot.Members.FirstOrDefault(member =>
                string.Equals(member.MemberId, normalized.ReferencedMemberId, StringComparison.Ordinal));
            if (referenced is null ||
                (normalized.SessionId is not null && !string.Equals(referenced.SessionId, normalized.SessionId, StringComparison.Ordinal)))
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");
        }

        if (normalized.SubjectIsCurrentSender && normalized.SubjectMemberId is not null)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_subject");

        if (normalized.FactKind is ZaloReadOnlyFactKind.MemberTeam or ZaloReadOnlyFactKind.MemberMembership &&
            !normalized.SubjectIsCurrentSender && normalized.SubjectMemberId is null)
            return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_subject");

        if (normalized.OpenOfferId is not null)
        {
            var offer = snapshot.OpenOffers.FirstOrDefault(item =>
                string.Equals(item.OfferId, normalized.OpenOfferId, StringComparison.Ordinal));
            if (offer is null ||
                (normalized.SessionId is not null && !string.Equals(offer.SessionId, normalized.SessionId, StringComparison.Ordinal)))
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");

            if (normalized.ReferencedMemberId is not null)
            {
                var referenced = snapshot.Members.First(member =>
                    string.Equals(member.MemberId, normalized.ReferencedMemberId, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(referenced.ZaloUserId) &&
                    !string.Equals(referenced.ZaloUserId, offer.OwnerZaloUserId, StringComparison.Ordinal))
                    return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");
            }
        }

        if (normalized.FactKind == ZaloReadOnlyFactKind.CanMemberTakeSlot)
        {
            if (!normalized.SubjectIsCurrentSender && normalized.SubjectMemberId is null)
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_subject");
            if (normalized.ReferencedMemberId is null && normalized.OpenOfferId is null)
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_unresolved_slot_reference");
        }

        if (normalized.SourceMessageId is not null)
        {
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            var validSource = context.MessageIds.Contains(normalized.SourceMessageId, StringComparer.Ordinal) ||
                              string.Equals(quote.MessageId, normalized.SourceMessageId, StringComparison.Ordinal) ||
                              snapshot.OpenOffers.Any(offer =>
                                  string.Equals(offer.SourceMessageId, normalized.SourceMessageId, StringComparison.Ordinal));
            if (!validSource)
                return ZaloReadOnlyPlanValidationResult.Reject(plan, "semantic_invalid_entity");
        }

        return ZaloReadOnlyPlanValidationResult.Accept(normalized);
    }
}
