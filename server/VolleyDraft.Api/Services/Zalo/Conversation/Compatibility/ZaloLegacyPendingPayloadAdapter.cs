using System.Text.Json;

namespace VolleyDraft.Api.Services;

public sealed record ZaloTypedLegacyPendingPayload(
    string CollectedArgumentsJson,
    string MissingArgumentsJson,
    string CandidateEntitiesJson);

/// <summary>
/// Converts handler-specific legacy pending JSON into a stable V2 envelope. It keeps
/// only typed identifiers/argument shape needed for routing and deliberately does not
/// copy an arbitrary legacy JSON blob into the new conversation state.
/// </summary>
public static class ZaloLegacyPendingPayloadAdapter
{
    public static ZaloTypedLegacyPendingPayload Adapt(string? pendingIntent, string? payloadJson)
    {
        var intent = (pendingIntent ?? string.Empty).Trim();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var people = new HashSet<string>(StringComparer.Ordinal);
        var teams = new HashSet<string>(StringComparer.Ordinal);
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var shape = "empty";

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            shape = document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                _ => "scalar"
            };
            Collect(document.RootElement, null, intent, sessions, people, teams, candidateIds);
        }
        catch (JsonException)
        {
            shape = "invalid";
        }

        var collected = new Dictionary<string, object?>
        {
            ["migrationSource"] = "legacy_pending",
            ["payloadShape"] = shape
        };
        if (sessions.Count == 1) collected["sessionId"] = sessions.Single();
        if (sessions.Count > 1) collected["sessionIds"] = sessions.Order(StringComparer.Ordinal).ToArray();
        if (people.Count > 0) collected["personIds"] = people.Order(StringComparer.Ordinal).ToArray();
        if (teams.Count > 0) collected["teamIds"] = teams.Order(StringComparer.Ordinal).ToArray();

        var missing = new List<string>();
        if (intent.EndsWith("Confirm", StringComparison.OrdinalIgnoreCase) ||
            intent.EndsWith("Confirmation", StringComparison.OrdinalIgnoreCase))
            missing.Add("confirmation");
        if (RequiresSessionReference(intent) && sessions.Count == 0)
            missing.Add("sessionReference");

        var candidates = new List<object>();
        candidates.AddRange(sessions.Select(id => (object)new { type = "session", id }));
        candidates.AddRange(people.Select(id => (object)new { type = "person", id }));
        candidates.AddRange(teams.Select(id => (object)new { type = "team", id }));
        candidates.AddRange(candidateIds
            .Where(id => !sessions.Contains(id) && !people.Contains(id) && !teams.Contains(id))
            .Select(id => (object)new { type = "candidate", id }));

        return new ZaloTypedLegacyPendingPayload(
            JsonSerializer.Serialize(collected),
            JsonSerializer.Serialize(missing.Distinct(StringComparer.Ordinal).ToArray()),
            JsonSerializer.Serialize(candidates));
    }

    private static void Collect(
        JsonElement element,
        string? propertyName,
        string intent,
        HashSet<string> sessions,
        HashSet<string> people,
        HashSet<string> teams,
        HashSet<string> candidates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, property.Name, intent, sessions, people, teams, candidates);
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, propertyName, intent, sessions, people, teams, candidates);
                return;
            case JsonValueKind.String:
                var value = (element.GetString() ?? string.Empty).Trim();
                if (value.Length == 0 || value.Length > 200) return;
                Classify(propertyName, intent, value, sessions, people, teams, candidates);
                return;
            default:
                return;
        }
    }

    private static void Classify(
        string? propertyName,
        string intent,
        string value,
        HashSet<string> sessions,
        HashSet<string> people,
        HashSet<string> teams,
        HashSet<string> candidates)
    {
        var key = (propertyName ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (key.Length == 0 && (intent.Contains("AutoDraft", StringComparison.OrdinalIgnoreCase) ||
                                intent.Contains("Redraft", StringComparison.OrdinalIgnoreCase)))
        {
            sessions.Add(value);
            return;
        }
        if (key.Contains("sessionid", StringComparison.Ordinal))
        {
            sessions.Add(value);
            if (key.Contains("candidate", StringComparison.Ordinal)) candidates.Add(value);
            return;
        }
        if (key.Contains("zalouserid", StringComparison.Ordinal) ||
            key.Contains("playerid", StringComparison.Ordinal) ||
            key.Contains("memberid", StringComparison.Ordinal) ||
            key.Contains("personid", StringComparison.Ordinal))
        {
            people.Add(value);
            if (key.Contains("candidate", StringComparison.Ordinal)) candidates.Add(value);
            return;
        }
        if (key.Contains("teamid", StringComparison.Ordinal))
        {
            teams.Add(value);
            if (key.Contains("candidate", StringComparison.Ordinal)) candidates.Add(value);
            return;
        }
        if (key.Contains("candidate", StringComparison.Ordinal)) candidates.Add(value);
    }

    private static bool RequiresSessionReference(string intent) =>
        intent.Contains("Draft", StringComparison.OrdinalIgnoreCase) ||
        intent.Contains("Team", StringComparison.OrdinalIgnoreCase) ||
        intent.Contains("Slot", StringComparison.OrdinalIgnoreCase) ||
        intent.Contains("Waitlist", StringComparison.OrdinalIgnoreCase) ||
        intent.Contains("Undo", StringComparison.OrdinalIgnoreCase);
}
