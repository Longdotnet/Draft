using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloVenueGuidance(
    string? ParkingInstructions,
    string? LocationImageUrl);

/// <summary>
/// Reuses venue guidance already attached to a previous session in the same Zalo group.
/// Auto Session only stores a default location today, so this keeps parking/image guidance
/// durable across newly-created sessions without inventing a second media store.
/// </summary>
internal sealed class ZaloVenueGuidanceResolver(VolleyDraftDbContext db)
{
    private static readonly HashSet<string> GenericVenueWords = new(StringComparer.Ordinal)
    {
        "san", "bong", "chuyen", "nha", "thi", "dau", "dia", "diem"
    };

    public async Task<ZaloVenueGuidance> ResolveAsync(
        string connectionId,
        string groupId,
        string? location,
        string? parkingInstructions = null,
        string? locationImageUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentParking = Clean(parkingInstructions);
        var currentImage = Clean(locationImageUrl);
        if (currentParking is not null && currentImage is not null)
            return new(currentParking, currentImage);

        var rows = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId &&
                (session.ParkingInstructions != null || session.LocationImageUrl != null))
            .OrderByDescending(session => session.UpdatedAt)
            .Take(50)
            .Select(session => new VenueRow(
                session.Location,
                session.ParkingInstructions,
                session.LocationImageUrl))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
            return new(currentParking, currentImage);

        var requestedKey = NormalizeVenueKey(location);
        var candidate = requestedKey.Length == 0
            ? rows.FirstOrDefault()
            : rows.FirstOrDefault(row =>
                string.Equals(NormalizeVenueKey(row.Location), requestedKey, StringComparison.Ordinal));

        return candidate is null
            ? new(currentParking, currentImage)
            : new(
                currentParking ?? Clean(candidate.ParkingInstructions),
                currentImage ?? Clean(candidate.LocationImageUrl));
    }

    internal static string NormalizeVenueKey(string? value)
    {
        var normalized = ZaloBotIntelligence.Normalize(value ?? string.Empty);
        if (normalized.Length == 0) return string.Empty;

        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => !GenericVenueWords.Contains(word)));
    }

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed record VenueRow(
        string? Location,
        string? ParkingInstructions,
        string? LocationImageUrl);
}
