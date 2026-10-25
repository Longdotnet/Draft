namespace VolleyDraft.Api.Services;

public sealed record ZaloMemberIntegrationSettings(
    bool Enabled,
    string GroupNameContains,
    int NewMemberWindowDays,
    string BotCreatorName,
    string GroupFounderName,
    string GroupPurpose,
    string CultureSummary,
    int? NormalCourtFeeVnd,
    int? MaxPlayersPerSession,
    IReadOnlyList<string> ApprovedLore)
{
    public static ZaloMemberIntegrationSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("ZaloBot:MemberIntegration");
        var lore = section.GetSection("ApprovedLore")
            .GetChildren()
            .Select(item => item.Value?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Take(20)
            .ToArray();

        var fee = section.GetValue<int?>("NormalCourtFeeVnd");
        var capacity = section.GetValue<int?>("MaxPlayersPerSession");
        return new ZaloMemberIntegrationSettings(
            section.GetValue("Enabled", true),
            section["GroupNameContains"]?.Trim() ?? string.Empty,
            Math.Clamp(section.GetValue("NewMemberWindowDays", 28), 7, 60),
            section["BotCreatorName"]?.Trim() ?? string.Empty,
            section["GroupFounderName"]?.Trim() ?? string.Empty,
            section["GroupPurpose"]?.Trim() ?? string.Empty,
            section["CultureSummary"]?.Trim() ?? string.Empty,
            fee is > 0 ? fee : null,
            capacity is > 0 ? capacity : null,
            lore);
    }

    public bool AppliesTo(string? groupName)
    {
        if (!Enabled) return false;
        if (string.IsNullOrWhiteSpace(GroupNameContains)) return true;
        return (groupName ?? string.Empty).Contains(GroupNameContains, StringComparison.CurrentCultureIgnoreCase);
    }
}
