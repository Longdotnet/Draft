using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Prevents a read-only ambient same-team proposal from turning into a materially
/// broader mutation at confirmation time.
///
/// The ambient advisor discloses exactly requester + partner. The authoritative
/// draft planner may legitimately expand that pair through existing same-team groups
/// or shared slots. That transitive expansion is valid domain behavior, but it must be
/// previewed to the user before it becomes a write. This guard runs only after the
/// normal handoff has already proven quote/address provenance and built its fresh
/// deterministic plan, but before the legacy router consumes the pending confirmation.
/// </summary>
public sealed class ZaloAmbientTeamPreferenceConfirmationGuard(VolleyDraftDbContext db)
{
    public async Task<ZaloAmbientTeamPreferenceDisclosure?> CaptureDisclosureAsync(
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        var active = await new ZaloConversationStateV2Store(db)
            .LoadActiveAsync(Clean(groupId, 100), Clean(senderZaloUserId, 100), cancellationToken);
        if (active is null ||
            !string.Equals(active.Intent, ZaloAmbientTeamPreferenceHandoff.ProposalIntent, StringComparison.Ordinal))
            return null;

        try
        {
            using var document = JsonDocument.Parse(active.CollectedArgumentsJson);
            var root = document.RootElement;
            var requesterUid = GetString(root, "requesterZaloUserId", 100);
            var requesterName = GetString(root, "requesterDisplayName", 160);
            var partnerUid = GetString(root, "partnerZaloUserId", 100);
            var partnerName = GetString(root, "partnerDisplayName", 160);
            var sessionId = GetString(root, "sessionId", 100);
            var sessionName = GetString(root, "sessionName", 160);
            if (requesterUid.Length == 0 || requesterName.Length == 0 ||
                partnerUid.Length == 0 || partnerName.Length == 0 || sessionId.Length == 0)
                return null;

            return new(
                requesterUid,
                requesterName,
                partnerUid,
                partnerName,
                sessionId,
                sessionName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string?> RejectUndisclosedExpansionAsync(
        ZaloAmbientTeamPreferenceDisclosure? disclosure,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (disclosure is null) return null;

        var pending = await FindPromotedPendingAsync(incoming, cancellationToken);
        if (pending is null) return null;

        TeamPreferencePendingPlan? plan = null;
        try
        {
            using var document = JsonDocument.Parse(pending.PendingPayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("SessionId", out var sessionNode) &&
                root.TryGetProperty("Plan", out var planNode) &&
                planNode.TryGetProperty("SessionPlayerIds", out var idsNode) &&
                idsNode.ValueKind == JsonValueKind.Array &&
                planNode.TryGetProperty("PlayerNames", out var namesNode) &&
                namesNode.ValueKind == JsonValueKind.Array)
            {
                plan = new(
                    Clean(sessionNode.GetString(), 100),
                    idsNode.EnumerateArray()
                        .Select(item => Clean(item.GetString(), 100))
                        .Where(item => item.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    namesNode.EnumerateArray()
                        .Select(item => Clean(item.GetString(), 160))
                        .Where(item => item.Length > 0)
                        .ToArray());
            }
        }
        catch (JsonException)
        {
            // A promoted confirmation that cannot be proven equivalent to the
            // disclosed pair is unsafe to pass through. Fall through to rejection.
        }

        if (plan is not null && string.Equals(plan.SessionId, disclosure.SessionId, StringComparison.Ordinal))
        {
            var disclosedUids = new[] { disclosure.RequesterZaloUserId, disclosure.PartnerZaloUserId }
                .ToHashSet(StringComparer.Ordinal);
            var disclosedPlayerIds = await db.SessionPlayers
                .AsNoTracking()
                .Where(player =>
                    player.SessionId == disclosure.SessionId &&
                    player.PlayerProfile != null &&
                    player.PlayerProfile.ZaloUserId != null &&
                    disclosedUids.Contains(player.PlayerProfile.ZaloUserId))
                .Select(player => player.Id)
                .ToListAsync(cancellationToken);

            if (disclosedPlayerIds.Count == 2 &&
                plan.SessionPlayerIds.ToHashSet(StringComparer.Ordinal).SetEquals(disclosedPlayerIds))
                return null;
        }

        return await RejectAsync(pending, disclosure, plan, cancellationToken);
    }

    public async Task AbortPromotedConfirmationAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var pending = await FindPromotedPendingAsync(incoming, cancellationToken);
        if (pending is null) return;
        db.ZaloBotConversationStates.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ZaloBotConversationState?> FindPromotedPendingAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var groupId = Clean(incoming.GroupId, 100);
        var senderId = Clean(incoming.SenderId, 100);
        var messageId = Clean(incoming.MessageId, 160);
        if (groupId.Length == 0 || senderId.Length == 0 || messageId.Length == 0) return null;

        var pendingRows = await db.ZaloBotConversationStates
            .Where(state =>
                state.GroupId == groupId &&
                state.SenderZaloUserId == senderId &&
                state.PendingIntent == ZaloBotIntent.TeamPreferenceConfirm.ToString())
            .ToListAsync(cancellationToken);
        return pendingRows.SingleOrDefault(state =>
            (state.PreviousCommand ?? string.Empty).EndsWith($":{messageId}", StringComparison.Ordinal));
    }

    private async Task<string> RejectAsync(
        ZaloBotConversationState pending,
        ZaloAmbientTeamPreferenceDisclosure disclosure,
        TeamPreferencePendingPlan? plan,
        CancellationToken cancellationToken)
    {
        // The fresh plan is no longer provably identical to the two-person proposal
        // that the member saw. Remove the one-shot legacy envelope so the same webhook
        // cannot fall through and mutate an undisclosed transitive group.
        db.ZaloBotConversationStates.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);

        var currentNames = plan?.PlayerNames is { Count: > 0 }
            ? string.Join(", ", plan.PlayerNames)
            : "một nhóm khác với đề xuất ban đầu";
        var sessionLabel = disclosure.SessionName.Length == 0 ? "kèo này" : disclosure.SessionName;
        var retrySyntax = disclosure.SessionName.Length == 0
            ? $"@Npc xếp tui chung team với @{disclosure.PartnerDisplayName} đi"
            : $"@Npc xếp tui chung team với @{disclosure.PartnerDisplayName} ở {disclosure.SessionName} đi";

        return $"Yêu cầu chung team đã đổi phạm vi: nếu xác nhận lúc này sẽ thành {currentNames}, không còn đúng đề xuất {disclosure.RequesterDisplayName} + {disclosure.PartnerDisplayName} ở {sessionLabel}. " +
               $"Mình chưa áp dụng để tránh đổi thêm người mà bạn chưa xem. Gửi lại: {retrySyntax} (chọn đúng @mention); mình sẽ hiện phương án mới đầy đủ rồi bạn xác nhận.";
    }

    private static string GetString(JsonElement root, string propertyName, int maxLength)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;
        return Clean(node.GetString(), maxLength);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private sealed record TeamPreferencePendingPlan(
        string SessionId,
        IReadOnlyList<string> SessionPlayerIds,
        IReadOnlyList<string> PlayerNames);
}

public sealed record ZaloAmbientTeamPreferenceDisclosure(
    string RequesterZaloUserId,
    string RequesterDisplayName,
    string PartnerZaloUserId,
    string PartnerDisplayName,
    string SessionId,
    string SessionName);
