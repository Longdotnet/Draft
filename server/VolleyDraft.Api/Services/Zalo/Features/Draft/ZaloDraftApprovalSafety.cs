using System.Text.Json;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Small deterministic guards shared by the natural/proactive draft approval flow.
/// They intentionally fail closed: an active pending action can only be reused when
/// it is the AutoDraft confirmation for the exact same session.
/// </summary>
public static class ZaloDraftApprovalSafety
{
    public static bool PendingTargetsSession(
        string? pendingIntent,
        string? pendingPayloadJson,
        string sessionId)
    {
        if (!string.Equals(
                pendingIntent,
                ZaloBotIntent.AutoDraftConfirm.ToString(),
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionId))
            return false;

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(pendingPayloadJson ?? "[]") ?? [];
            return ids.Contains(sessionId, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool CanReservePending(
        ZaloBotConversationState? state,
        string sessionId,
        DateTimeOffset now)
    {
        if (state is null || state.ExpiresAt <= now) return true;
        return PendingTargetsSession(state.PendingIntent, state.PendingPayloadJson, sessionId);
    }

    public static bool IsDraftCompleted(SessionStatus status) =>
        status == SessionStatus.Finished;
}
