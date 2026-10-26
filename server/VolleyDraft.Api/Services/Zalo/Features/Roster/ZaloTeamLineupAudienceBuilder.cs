using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal static class ZaloTeamLineupAudienceBuilder
{
    public static async Task<ZaloTeamLineupMessage> BuildAsync(
        VolleyDraftDbContext db,
        string sessionName,
        IReadOnlyList<TeamPreviewResponse> teams,
        bool mentionPlayers,
        CancellationToken cancellationToken,
        ZaloDraftReadinessSnapshot? readiness = null)
    {
        if (readiness is { HasTeams: false })
            return ZaloTeamLineupFormatter.Format(sessionName, teams, readiness: readiness);

        var slotIds = teams
            .SelectMany(team => team.Slots)
            .Select(slot => slot.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (slotIds.Count == 0)
            return ZaloTeamLineupFormatter.Format(sessionName, teams, readiness: readiness);

        var links = await db.DraftSlotPlayers
            .AsNoTracking()
            .Where(link => slotIds.Contains(link.DraftSlotId))
            .OrderBy(link => link.DraftSlotId)
            .ThenBy(link => link.RotationOrder)
            .Select(link => new
            {
                link.DraftSlotId,
                link.SessionPlayer.DisplayName,
                ZaloUserId = link.SessionPlayer.PlayerProfile == null
                    ? null
                    : link.SessionPlayer.PlayerProfile.ZaloUserId
            })
            .ToListAsync(cancellationToken);
        var playersBySlot = links
            .GroupBy(link => link.DraftSlotId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ZaloTeamMentionPlayer>)group
                    .Select(link => new ZaloTeamMentionPlayer(link.DisplayName, Clean(link.ZaloUserId, 100)))
                    .ToList(),
                StringComparer.Ordinal);
        return ZaloTeamLineupFormatter.Format(
            sessionName,
            teams,
            playersBySlot,
            readiness,
            mentionPlayers);
    }

    private static string? Clean(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
