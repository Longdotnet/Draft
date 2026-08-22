using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string GuestTaskDomain = "RecruitmentGuest";

    private async Task<ZaloSemanticGuestGroundingSnapshot> EnrichSemanticGuestSnapshotAsync(
        ZaloSemanticGuestGroundingSnapshot snapshot,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var world = await new ZaloRecruitmentWorldModelBuilder(db)
                .BuildAsync(snapshot.SessionId, groupId, senderId, cancellationToken);
            return snapshot with { World = world };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Could not enrich semantic guest world Session={SessionId}", snapshot.SessionId);
            return snapshot;
        }
    }

    private async Task ProjectGuestTaskStackAsync(
        string connectionId,
        string groupId,
        string senderId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-2);
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           !item.IsFromBot &&
                           item.SenderId == senderId &&
                           item.ReceivedAt >= cutoff &&
                           item.ReplyOutcome != null &&
                           item.SelectedIntent != null)
            .Take(160)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return;

        var grouped = rows
            .Select(item => new
            {
                Row = item,
                SessionId = ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(item.SelectedIntent)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .GroupBy(item => item.SessionId!, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Row.ReceivedAt).First())
            .ToArray();
        if (grouped.Length == 0) return;

        var sessionIds = grouped.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).ToArray();
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(item => sessionIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name, item.StartTime, item.Status })
            .ToListAsync(cancellationToken);
        var sessionMap = sessions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var store = new ZaloConversationTaskStackStore(db);
        var pendingMinutes = Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:GuestPendingClarificationMinutes", 15), 5, 60);
        var profileMinutes = Math.Clamp(configuration.GetValue("ZaloBot:DraftAutopilot:GuestProfileConversationMinutes", 60), 10, 180);

        foreach (var item in grouped)
        {
            if (!sessionMap.TryGetValue(item.SessionId, out var session) ||
                session.Status is SessionStatus.Cancelled or SessionStatus.Drafting or SessionStatus.Finished ||
                session.StartTime is null || session.StartTime <= now)
                continue;

            var pendingKind = ZaloStatefulGuestFollowupPolicy.PendingKind(item.Row.ReplyOutcome);
            if (pendingKind != ZaloStatefulGuestPendingKind.None)
            {
                string? recruitmentMessageId = null;
                if (pendingKind == ZaloStatefulGuestPendingKind.AddQuantity)
                {
                    var senderRows = rows
                        .Where(row => string.Equals(
                            ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(row.SelectedIntent),
                            item.SessionId,
                            StringComparison.Ordinal))
                        .OrderByDescending(row => row.ReceivedAt)
                        .ToArray();
                    recruitmentMessageId = await ResolvePendingRecruitmentMessageIdAsync(
                        connectionId,
                        groupId,
                        senderId,
                        item.SessionId,
                        senderRows,
                        cancellationToken);
                }

                List<ZaloGuestReservation> guests = pendingKind == ZaloStatefulGuestPendingKind.AddQuantity
                    ? []
                    : await LoadStatefulSponsorGuestsAsync(item.SessionId, senderId, cancellationToken);
                var intent = PendingTaskIntent(pendingKind);
                await store.UpsertAsync(
                    GuestTaskKey(item.SessionId, intent),
                    groupId,
                    senderId,
                    GuestTaskDomain,
                    intent,
                    item.SessionId,
                    session.Name,
                    JsonSerializer.Serialize(new { sessionId = item.SessionId, recruitmentMessageId }),
                    JsonSerializer.Serialize(ZaloStatefulGuestFollowupPolicy.MissingFields(pendingKind)),
                    JsonSerializer.Serialize(guests.Select(GuestCandidate)),
                    recruitmentMessageId ?? item.Row.MessageId,
                    item.Row.MessageId,
                    now.AddMinutes(pendingMinutes),
                    cancellationToken);
                continue;
            }

            if (item.Row.ReplyOutcome is "guest_semantic_added" or "guest_semantic_add_idempotent" or "guest_semantic_profile_updated")
            {
                var guests = await LoadStatefulSponsorGuestsAsync(item.SessionId, senderId, cancellationToken);
                var missing = guests.Where(guest => guest.Gender is null)
                    .Select(guest => $"gender:#{guest.SponsorSequence}")
                    .ToArray();
                if (missing.Length > 0)
                {
                    const string intent = "GuestProfile";
                    await store.UpsertAsync(
                        GuestTaskKey(item.SessionId, intent),
                        groupId,
                        senderId,
                        GuestTaskDomain,
                        intent,
                        item.SessionId,
                        session.Name,
                        JsonSerializer.Serialize(new { sessionId = item.SessionId }),
                        JsonSerializer.Serialize(missing),
                        JsonSerializer.Serialize(guests.Select(GuestCandidate)),
                        item.Row.MessageId,
                        item.Row.MessageId,
                        now.AddMinutes(profileMinutes),
                        cancellationToken);
                }
                else
                {
                    await store.CompleteSessionDomainAsync(groupId, senderId, GuestTaskDomain, item.SessionId, cancellationToken);
                }
                continue;
            }

            if (item.Row.ReplyOutcome is "guest_semantic_cancelled" or "guest_semantic_pending_abandoned")
                await store.CompleteSessionDomainAsync(groupId, senderId, GuestTaskDomain, item.SessionId, cancellationToken);
        }
    }

    private async Task<StatefulSemanticGuestContext?> ResolveGuestTaskStackContextAsync(
        string connectionId,
        string groupId,
        string senderId,
        string currentMessage,
        CancellationToken cancellationToken)
    {
        await ProjectGuestTaskStackAsync(connectionId, groupId, senderId, cancellationToken);
        var store = new ZaloConversationTaskStackStore(db);
        var tasks = await store.LoadActiveAsync(groupId, senderId, GuestTaskDomain, 12, cancellationToken);
        if (tasks.Count == 0) return null;

        var normalized = ZaloBotIntelligence.Normalize(currentMessage);
        var named = tasks.Where(item =>
        {
            var sessionName = ZaloBotIntelligence.Normalize(item.SessionName);
            return sessionName.Length >= 2 && normalized.Contains(sessionName, StringComparison.Ordinal);
        }).OrderByDescending(item => item.UpdatedAt).Take(2).ToArray();

        ZaloConversationTaskSnapshot? selected;
        if (named.Length == 1)
        {
            selected = named[0];
        }
        else if (named.Length > 1)
        {
            return null;
        }
        else
        {
            var pending = tasks.Where(item => TaskPendingKind(item.Intent) != ZaloStatefulGuestPendingKind.None)
                .OrderByDescending(item => item.UpdatedAt).ToArray();
            if (pending.Length == 1) selected = pending[0];
            else if (pending.Length > 1) return null;
            else
            {
                var profile = tasks.Where(item => item.Intent == "GuestProfile").OrderByDescending(item => item.UpdatedAt).ToArray();
                selected = profile.Length == 1 ? profile[0] : null;
            }
        }
        if (selected is null) return null;

        var session = await LoadStatefulGuestSessionAsync(connectionId, groupId, selected.SessionId, cancellationToken);
        if (session is null)
        {
            await store.CompleteAsync(selected.TaskKey, cancellationToken);
            return null;
        }

        var pendingKind = TaskPendingKind(selected.Intent);
        List<ZaloGuestReservation> guests = pendingKind == ZaloStatefulGuestPendingKind.AddQuantity
            ? []
            : await LoadStatefulSponsorGuestsAsync(session.Id, senderId, cancellationToken);
        string? recruitmentMessageId = null;
        if (pendingKind == ZaloStatefulGuestPendingKind.AddQuantity)
            recruitmentMessageId = ReadTaskRecruitmentMessageId(selected.CollectedArgumentsJson);

        var anchor = pendingKind != ZaloStatefulGuestPendingKind.None
            ? ZaloSemanticGuestAnchorKind.PendingGuestAction
            : ZaloSemanticGuestAnchorKind.ActiveGuestConversation;
        var missing = ReadStringArray(selected.MissingArgumentsJson);
        return new StatefulSemanticGuestContext(
            new SemanticGuestTurnContext(session, anchor, recruitmentMessageId, guests, missing),
            selected.LastMessageId ?? selected.SourceMessageId ?? string.Empty,
            pendingKind,
            TechnicalFallbackShouldExplain: pendingKind != ZaloStatefulGuestPendingKind.None);
    }

    private static object GuestCandidate(ZaloGuestReservation guest) => new
    {
        guest.Id,
        guest.SponsorSequence,
        guest.DisplayName,
        guest.Gender,
        guest.Level,
        guest.Role,
        Status = guest.Status.ToString()
    };

    private static string GuestTaskKey(string sessionId, string intent) => $"guest:{sessionId}:{intent}";

    private static string PendingTaskIntent(ZaloStatefulGuestPendingKind kind) => kind switch
    {
        ZaloStatefulGuestPendingKind.AddQuantity => "PendingAddQuantity",
        ZaloStatefulGuestPendingKind.UpdateTarget => "PendingUpdateTarget",
        ZaloStatefulGuestPendingKind.UpdateFields => "PendingUpdateFields",
        ZaloStatefulGuestPendingKind.CancelTarget => "PendingCancelTarget",
        _ => "GuestProfile"
    };

    private static ZaloStatefulGuestPendingKind TaskPendingKind(string intent) => intent switch
    {
        "PendingAddQuantity" => ZaloStatefulGuestPendingKind.AddQuantity,
        "PendingUpdateTarget" => ZaloStatefulGuestPendingKind.UpdateTarget,
        "PendingUpdateFields" => ZaloStatefulGuestPendingKind.UpdateFields,
        "PendingCancelTarget" => ZaloStatefulGuestPendingKind.CancelTarget,
        _ => ZaloStatefulGuestPendingKind.None
    };

    private static string? ReadTaskRecruitmentMessageId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("recruitmentMessageId", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
