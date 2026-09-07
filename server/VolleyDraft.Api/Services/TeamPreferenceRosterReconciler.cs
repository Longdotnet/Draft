using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Keeps durable same-team preferences aligned with the authoritative current roster.
/// A preference is only meaningful while at least two of its members are still present.
/// </summary>
internal sealed class TeamPreferenceRosterReconciler(VolleyDraftDbContext db)
{
    internal async Task ReconcileAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var groups = await db.TeamPreferenceGroups
            .Include(group => group.Players)
            .ThenInclude(link => link.SessionPlayer)
            .Where(group => group.SessionId == sessionId)
            .ToListAsync(cancellationToken);

        foreach (var group in groups)
        {
            var activeLinks = group.Players
                .Where(link => link.SessionPlayer.IsPresent)
                .OrderBy(link => link.RotationOrder)
                .ToList();
            var inactiveLinks = group.Players
                .Where(link => !link.SessionPlayer.IsPresent)
                .ToList();

            if (inactiveLinks.Count == 0)
                continue;

            if (activeLinks.Count < 2)
            {
                db.TeamPreferenceGroupPlayers.RemoveRange(group.Players);
                db.TeamPreferenceGroups.Remove(group);
                continue;
            }

            db.TeamPreferenceGroupPlayers.RemoveRange(inactiveLinks);
            for (var index = 0; index < activeLinks.Count; index += 1)
                activeLinks[index].RotationOrder = index + 1;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
