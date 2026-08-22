using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Resolves an already-validated semantic plan from authoritative application state.
/// Existing ambient fact capabilities are delegated back to ZaloAmbientFactResponder;
/// only capabilities that require a specifically grounded member/offer are handled
/// here. No method in this type mutates roster, teams, waitlist or slot offers.
/// </summary>
internal sealed class ZaloReadOnlyGroundedFactResolver(VolleyDraftDbContext db)
{
    public async Task<ZaloAmbientFactReply?> TryBuildAsync(
        string accountId,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientParticipationDecision ambientDecision,
        ZaloReadOnlySemanticPlan plan,
        ZaloReadOnlyGroundingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (plan.Route != ZaloReadOnlySemanticRoute.ReadOnlyQuestion) return null;

        if (plan.FactKind == ZaloReadOnlyFactKind.MemberMembership)
            return await BuildMemberMembershipAsync(groupId, incoming, plan, cancellationToken);
        if (plan.FactKind == ZaloReadOnlyFactKind.MemberTeam)
            return await BuildMemberTeamAsync(groupId, incoming, plan, cancellationToken);
        if (plan.FactKind == ZaloReadOnlyFactKind.CanMemberTakeSlot)
            return await BuildCanMemberTakeSlotAsync(
                connectionId,
                groupId,
                incoming,
                plan,
                cancellationToken);

        var intent = MapExistingFactIntent(plan.FactKind);
        if (intent is null || !ZaloAmbientFactResponder.IsAllowedIntent(intent.Value)) return null;

        var groundedIncoming = incoming;
        if (plan.SessionId is not null)
        {
            var session = snapshot.Sessions.FirstOrDefault(item =>
                string.Equals(item.SessionId, plan.SessionId, StringComparison.Ordinal));
            if (session is null) return null;
            // The legacy responder already has authoritative DB resolution. Supplying
            // the validated session name simply avoids re-parsing a referential phrase
            // such as "bữa đó" from scratch.
            groundedIncoming = incoming with { Content = session.Name };
        }

        var groundedDecision = ambientDecision with
        {
            WouldReply = true,
            Score = Math.Max(ambientDecision.Score, 100),
            Kind = ZaloAmbientParticipationKind.Fact,
            Intent = intent.Value.ToString(),
            IntentConfidence = plan.Confidence,
            Signals = ambientDecision.Signals
                .Append("grounded_readonly_semantic")
                .Append($"grounded_readonly_{plan.FactKind}")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        return await new ZaloAmbientFactResponder(db).TryBuildAsync(
            accountId,
            groupId,
            groundedIncoming,
            groundedDecision,
            minimumScore: 60,
            cancellationToken);
    }

    private async Task<ZaloAmbientFactReply?> BuildMemberMembershipAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloReadOnlySemanticPlan plan,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(groupId, plan.SessionId, cancellationToken);
        if (session is null) return null;

        var subject = ResolveSubject(session, incoming, plan);
        if (subject.Member is null)
        {
            if (!plan.SubjectIsCurrentSender) return null;
            var senderName = DisplayName(incoming.SenderName, "Bạn");
            return new ZaloAmbientFactReply(
                ZaloBotIntent.SelfMembership,
                $"Tui chưa thấy {senderName} trong danh sách {session.Name}.",
                session.Id);
        }

        return new ZaloAmbientFactReply(
            ZaloBotIntent.SelfMembership,
            subject.Member.IsPresent
                ? $"{subject.Name} đang có tên trong {session.Name}."
                : $"{subject.Name} hiện không có trong danh sách chơi {session.Name}.",
            session.Id);
    }

    private async Task<ZaloAmbientFactReply?> BuildMemberTeamAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloReadOnlySemanticPlan plan,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(groupId, plan.SessionId, cancellationToken);
        if (session is null) return null;

        var subject = ResolveSubject(session, incoming, plan);
        if (subject.Member is null)
        {
            if (!plan.SubjectIsCurrentSender) return null;
            return new ZaloAmbientFactReply(
                ZaloBotIntent.TeamLineup,
                $"Tui chưa thấy {subject.Name} trong danh sách {session.Name}, nên chưa có team để đối chiếu.",
                session.Id);
        }
        if (!subject.Member.IsPresent)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.TeamLineup,
                $"{subject.Name} hiện không có trong danh sách chơi {session.Name}, nên chưa thuộc team nào.",
                session.Id);
        }

        var slots = await db.DraftSlots
            .AsNoTracking()
            .Include(slot => slot.AssignedTeam)
            .Include(slot => slot.Players)
            .Where(slot => slot.SessionId == session.Id)
            .ToListAsync(cancellationToken);
        var assigned = slots.FirstOrDefault(slot =>
            slot.AssignedTeamId is not null &&
            slot.Players.Any(player => player.SessionPlayerId == subject.Member.Id));
        if (assigned?.AssignedTeam is null)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.TeamLineup,
                $"{subject.Name} có trong {session.Name} nhưng hiện chưa được xếp vào team nào.",
                session.Id);
        }

        return new ZaloAmbientFactReply(
            ZaloBotIntent.TeamLineup,
            $"{subject.Name} đang ở {assigned.AssignedTeam.Name} của {session.Name}.",
            session.Id);
    }

    private async Task<ZaloAmbientFactReply?> BuildCanMemberTakeSlotAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloReadOnlySemanticPlan plan,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(groupId, plan.SessionId, cancellationToken);
        if (session is null) return null;

        var subject = ResolveSubject(session, incoming, plan);
        var subjectName = subject.Name;
        if (subject.Member?.IsPresent == true)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.SlotTransfer,
                $"{subjectName} hiện đã có slot trong {session.Name}, nên chưa thể nhận thêm slot ở kèo này.",
                session.Id);
        }

        var subjectZaloUserId = plan.SubjectIsCurrentSender
            ? Clean(incoming.SenderId)
            : Clean(subject.Member?.PlayerProfile?.ZaloUserId);
        if (subjectZaloUserId.Length == 0)
        {
            return new ZaloAmbientFactReply(
                ZaloBotIntent.SlotTransfer,
                $"Tui chưa có đủ định danh Zalo của {subjectName} để xác nhận khả năng nhận slot {session.Name}.",
                session.Id);
        }

        var referenced = plan.ReferencedMemberId is null
            ? null
            : session.Players.FirstOrDefault(player => player.Id == plan.ReferencedMemberId);
        var referencedOwnerId = Clean(referenced?.PlayerProfile?.ZaloUserId);
        var referencedOwnerName = referenced is null
            ? null
            : DisplayName(referenced.DisplayName, "người được nhắc");

        var liveOffers = await new ZaloOpenSlotOfferStore(db)
            .ListClaimableAsync(connectionId, groupId, subjectZaloUserId, cancellationToken);
        var offer = plan.OpenOfferId is not null
            ? liveOffers.FirstOrDefault(item =>
                item.Id == plan.OpenOfferId && item.SessionId == session.Id)
            : referencedOwnerId.Length > 0
                ? liveOffers.FirstOrDefault(item =>
                    item.SessionId == session.Id && item.OwnerZaloUserId == referencedOwnerId)
                : null;

        if (offer is null)
        {
            if (referenced is not null)
            {
                return new ZaloAmbientFactReply(
                    ZaloBotIntent.SlotTransfer,
                    $"{subjectName} hiện chưa có slot ở {session.Name}, nhưng slot của {referencedOwnerName} chưa được pass/mở nên chưa nhận thay được.",
                    session.Id);
            }

            return new ZaloAmbientFactReply(
                ZaloBotIntent.SlotTransfer,
                $"{subjectName} hiện chưa có slot ở {session.Name}, nhưng tui chưa thấy slot nào đang mở để nhận.",
                session.Id);
        }

        return new ZaloAmbientFactReply(
            ZaloBotIntent.SlotTransfer,
            $"Slot của {DisplayName(offer.OwnerDisplayName, "người đang pass")} đang mở và {subjectName} hiện chưa có slot ở {session.Name}, nên {subjectName} có thể nhận.",
            session.Id);
    }

    private async Task<MatchSession?> LoadSessionAsync(
        string groupId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return await db.MatchSessions
            .AsNoTracking()
            .Include(session => session.Players)
                .ThenInclude(player => player.PlayerProfile)
            .SingleOrDefaultAsync(session =>
                session.Id == sessionId &&
                session.ZaloGroupId == groupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled,
                cancellationToken);
    }

    private static (SessionPlayer? Member, string Name) ResolveSubject(
        MatchSession session,
        ZaloIncomingMessageEvent incoming,
        ZaloReadOnlySemanticPlan plan)
    {
        if (plan.SubjectIsCurrentSender)
        {
            var senderId = Clean(incoming.SenderId);
            var member = session.Players.FirstOrDefault(player =>
                string.Equals(Clean(player.PlayerProfile?.ZaloUserId), senderId, StringComparison.Ordinal));
            return (member, DisplayName(member?.DisplayName ?? incoming.SenderName, "Bạn"));
        }

        var resolved = plan.SubjectMemberId is null
            ? null
            : session.Players.FirstOrDefault(player => player.Id == plan.SubjectMemberId);
        return (resolved, DisplayName(resolved?.DisplayName, "Người này"));
    }

    private static ZaloBotIntent? MapExistingFactIntent(ZaloReadOnlyFactKind factKind) => factKind switch
    {
        ZaloReadOnlyFactKind.SessionSchedule => ZaloBotIntent.SessionSchedule,
        ZaloReadOnlyFactKind.SelfMembership => ZaloBotIntent.SelfMembership,
        ZaloReadOnlyFactKind.LocationParking => ZaloBotIntent.LocationParking,
        ZaloReadOnlyFactKind.MissingSlots => ZaloBotIntent.MissingSlots,
        ZaloReadOnlyFactKind.UpcomingSessions => ZaloBotIntent.UpcomingSessions,
        ZaloReadOnlyFactKind.Roster => ZaloBotIntent.Roster,
        ZaloReadOnlyFactKind.WeeklySessionCount => ZaloBotIntent.WeeklySessionCount,
        ZaloReadOnlyFactKind.TeamLineup => ZaloBotIntent.TeamLineup,
        ZaloReadOnlyFactKind.ReminderStatus => ZaloBotIntent.ReminderStatus,
        ZaloReadOnlyFactKind.WaitlistStatus => ZaloBotIntent.WaitlistStatus,
        _ => null
    };

    private static string DisplayName(string? value, string fallback)
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
