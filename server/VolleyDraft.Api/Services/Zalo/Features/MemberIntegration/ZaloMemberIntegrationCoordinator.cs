namespace VolleyDraft.Api.Services;

/// <summary>
/// Parent coordinator for the MemberIntegration feature family.
/// It composes independent capabilities; it intentionally does not use inheritance as the
/// extension mechanism so a future capability can be added or removed without changing siblings.
/// </summary>
public sealed class ZaloMemberIntegrationCoordinator
{
    private readonly IReadOnlyList<IZaloMemberIntegrationCapability> capabilities;

    public ZaloMemberIntegrationCoordinator(IEnumerable<IZaloMemberIntegrationCapability> capabilities)
    {
        this.capabilities = capabilities
            .Where(capability => capability is not null)
            .OrderByDescending(capability => capability.Priority)
            .ThenBy(capability => capability.Key, StringComparer.Ordinal)
            .ToArray();

        var duplicateKey = this.capabilities
            .GroupBy(capability => capability.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateKey))
            throw new InvalidOperationException($"Duplicate member-integration capability key '{duplicateKey}'.");
    }

    public async Task<ZaloMemberIntegrationPlan> EvaluateAsync(
        ZaloMemberIntegrationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var suggestions = new List<ZaloMemberIntegrationSuggestion>();
        foreach (var capability in capabilities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suggestion = await capability.EvaluateAsync(context, cancellationToken);
            if (suggestion is null) continue;

            if (!string.Equals(suggestion.CapabilityKey, capability.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Member-integration capability '{capability.Key}' returned suggestion key '{suggestion.CapabilityKey}'.");
            }

            suggestions.Add(suggestion);
        }

        return new ZaloMemberIntegrationPlan(context, suggestions);
    }
}
