namespace VolleyDraft.Api.Services;

internal static class ZaloRosterAudienceBuilder
{
    public static ZaloAudienceReferenceDraft Build(
        string sessionName,
        IReadOnlyList<string> playerNames,
        IReadOnlyDictionary<string, string> playerNamesByZaloUserId)
    {
        var idsByName = playerNamesByZaloUserId
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Value.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => NormalizeZaloId(item.Key))
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.CurrentCultureIgnoreCase);

        var targets = playerNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
            {
                var ids = idsByName.GetValueOrDefault(name) ?? [];
                return new ZaloAudienceReferenceTarget(
                    ids.Length == 1 ? ids[0] : null,
                    name,
                    "roster",
                    "danh sách tham gia");
            })
            .ToArray();

        return new ZaloAudienceReferenceDraft(
            "roster",
            $"danh sách tham gia {sessionName}",
            targets);
    }

    private static string NormalizeZaloId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
